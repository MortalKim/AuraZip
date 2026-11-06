using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AuraZip.Core;

public sealed partial class ArchiveService
{
    private string RequireRarEncoder() => CanCreateRar ? RarEncoderPath! : throw new NotSupportedException("RAR writing requires a separately installed WinRAR command-line encoder. Choose Rar.exe in Settings.");
    private static void ValidateRarPassword(string? password)
    {
        if (password is not null && (password.Length > 127 || password.Any(c => c is '\0' or '\r' or '\n')))
            throw new ArgumentException("RAR passwords must contain at most 127 characters without line breaks.");
    }

    private Task<CompressionResult> CreateRarAsync(string destination, IReadOnlyList<string> paths, CompressionOptions options,
        OperationControl control, IProgress<ArchiveProgress> progress, CancellationToken token, bool overwrite) => Task.Run(async () =>
    {
        RequireRarEncoder(); ValidateRarPassword(options.Password);
        if (options.Level is < 0 or > 9 || options.VolumeBytes < 0) throw new ArgumentException("Invalid compression options.");
        destination = Path.GetFullPath(destination);
        if (paths.Any(p => string.Equals(Path.GetFullPath(p), destination, StringComparison.OrdinalIgnoreCase))) throw new IOException("The output archive cannot replace its source file.");
        if (options.VolumeBytes > 0 && (!destination.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) || overwrite))
            throw new NotSupportedException("RAR split output requires a new .rar filename.");
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var stem = Path.GetFileNameWithoutExtension(destination);
        if (Directory.EnumerateFiles(directory).Any(p => Regex.IsMatch(Path.GetFileName(p), "^" + Regex.Escape(stem) + @"\.part\d+\.rar$", RegexOptions.IgnoreCase)))
            throw new IOException("An output archive already exists. Choose a different name.");
        if (Directory.Exists(destination) || (!overwrite && File.Exists(destination))) throw new IOException("An output archive already exists. Choose a different name.");
        var originalHash = File.Exists(destination) ? await ComputeHashesAsync(destination, token, control) : null;
        var entries = await Task.Run(() => SourceEntries(paths, destination, token).ToList(), token).ConfigureAwait(false);
        if (entries.Count == 0) throw new ArgumentException("Select at least one file or folder.");
        if (entries.Select(e => e.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count) throw new IOException("Selected items contain duplicate names.");
        long total = entries.Where(e => !e.Directory).Sum(e => new FileInfo(e.Source).Length);
        EnsureSpace(destination, checked(total * 2 + 1024 * 1024));
        using var workspace = new TemporaryWorkspace(directory);
        var roots = paths.Select(Path.GetFullPath).Select(Path.TrimEndingDirectorySeparator).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var parent = roots.Length > 0 ? Path.GetDirectoryName(roots[0]) : null;
        bool direct = parent is not null && roots.All(p => string.Equals(Path.GetDirectoryName(p), parent, StringComparison.OrdinalIgnoreCase))
            && !roots.Any(p => Directory.Exists(p) && destination.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        var input = direct ? parent! : Path.Combine(workspace.Path, "input");
        if (!direct) await StageRarSourcesAsync(input, entries, control, progress, total, token);
        var list = Path.Combine(workspace.Path, "inputs.txt");
        await File.WriteAllLinesAsync(list, roots.Select(Path.GetFileName)!, new UTF8Encoding(false), token);
        var output = Path.Combine(workspace.Path, "output.rar");
        int level = options.Level == 0 ? 0 : options.Level <= 2 ? 1 : options.Level <= 5 ? 3 : options.Level <= 7 ? 4 : 5;
        var arguments = new List<string> { "a", "-m" + level, "-r", "-ep1" };
        if (options.VolumeBytes > 0) arguments.Add("-v" + options.VolumeBytes + "b");
        arguments.AddRange(RarPassword(options.Password, options.EncryptNames));
        arguments.Add(output); arguments.Add("@" + list);
        var sizes = entries.Where(e => !e.Directory).ToDictionary(e => e.Key, e => new FileInfo(e.Source).Length, StringComparer.OrdinalIgnoreCase);
        await RunRarAsync(arguments, input, control, progress, total, sizes, token);
        var outputs = Directory.EnumerateFiles(workspace.Path, "output*.rar").OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal).ToArray();
        if (outputs.Length == 0) throw new IOException("The compression engine did not produce an archive.");
        var first = outputs.FirstOrDefault(p => p.EndsWith(".part1.rar", StringComparison.OrdinalIgnoreCase)) ?? outputs[0];
        await TestAsync(await OpenAsync(first, options.Password, token), options.Password, control, progress, token);
        var targets = outputs.Select(p => Path.GetFileName(p).Equals("output.rar", StringComparison.OrdinalIgnoreCase)
            ? destination : Path.Combine(directory, stem + Path.GetFileName(p)["output".Length..])).ToArray();
        if (targets.Any(p => Directory.Exists(p) || (!overwrite && File.Exists(p)))) throw new IOException("An output archive already exists. Choose a different name.");
        long packed = outputs.Sum(p => new FileInfo(p).Length);
        var committed = new List<string>();
        try
        {
            for (int i = 0; i < outputs.Length; i++)
            {
                control.Wait(token);
                if (originalHash is not null) await ReplaceArchiveAsync(outputs[i], targets[i], originalHash, token);
                else File.Move(outputs[i], targets[i]);
                committed.Add(targets[i]);
            }
        }
        catch { if (originalHash is null) foreach (var path in committed) File.Delete(path); throw; }
        progress.Report(new(Path.GetFileName(destination), total, total, sizes.Count, sizes.Count));
        return new CompressionResult(targets.OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal).ToArray(), total, packed);
    }, token);

    private static IEnumerable<string> RarPassword(string? password, bool encryptNames = false)
        => string.IsNullOrEmpty(password) ? [] : [(encryptNames ? "-hp" : "-p") + password];

    private static async Task StageRarSourcesAsync(string root, IReadOnlyList<(string Source, string Key, bool Directory)> entries,
        OperationControl control, IProgress<ArchiveProgress> progress, long total, CancellationToken token)
    {
        Directory.CreateDirectory(root); long copied = 0; var buffer = new byte[128 * 1024];
        foreach (var entry in entries)
        {
            control.Wait(token); var target = SafeOutputPath(root, entry.Key);
            if (entry.Directory) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var source = File.OpenRead(entry.Source))
            await using (var output = File.Create(target))
            {
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                { control.Wait(token); await output.WriteAsync(buffer.AsMemory(0, read), token); copied += read; progress.Report(new(entry.Key, copied, total, 0, entries.Count(e => !e.Directory), ArchiveOperationStage.Preparing)); }
            }
            File.SetLastWriteTime(target, File.GetLastWriteTime(entry.Source));
            File.SetAttributes(target, File.GetAttributes(entry.Source) & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive));
        }
        foreach (var entry in entries.Where(e => e.Directory).OrderByDescending(e => e.Key.Length)) Directory.SetLastWriteTime(SafeOutputPath(root, entry.Key), Directory.GetLastWriteTime(entry.Source));
    }

    private async Task RunRarAsync(IEnumerable<string> arguments, string workingDirectory, OperationControl control,
        IProgress<ArchiveProgress> progress, long total, IReadOnlyDictionary<string, long>? sizes, CancellationToken token)
    {
        var start = new ProcessStartInfo(RequireRarEncoder()) { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        var args = arguments.ToList(); start.ArgumentList.Add(args[0]);
        foreach (var arg in new[] { "-cfg-", "-scf", "-y", "-idcd" }.Concat(args.Skip(1))) start.ArgumentList.Add(arg);
        start.Environment.Remove("RARINISWITCHES");
        using var process = Process.Start(start)!; process.StandardInput.Close();
        var errors = process.StandardError.ReadToEndAsync();
        var parser = new CompressionProgressParser(progress, total, sizes?.Count ?? 0, sizes);
        var reading = Task.Run(async () =>
        {
            var buffer = new char[2048]; var record = new StringBuilder(); int length;
            void Flush()
            {
                var text = record.ToString().Trim(); record.Clear();
                var name = Regex.Match(text, @"^(?:Adding|Updating)\s+(.+?)(?:\s+OK)?$");
                if (name.Success) parser.Read("+ " + name.Groups[1].Value.Trim());
                foreach (Match percent in Regex.Matches(text, @"(\d{1,3})%")) parser.Read(percent.Value);
            }
            while ((length = await process.StandardOutput.ReadAsync(buffer)) > 0)
                for (int i = 0; i < length; i++)
                    if (buffer[i] is '\b' or '\r' or '\n') Flush(); else if (record.Length < 16384) record.Append(buffer[i]);
            Flush();
        });
        bool suspended = false;
        try
        {
            while (!process.HasExited)
            {
                if (control.IsPaused && !suspended && OperatingSystem.IsWindows()) suspended = NtSuspendProcess(process.Handle) >= 0;
                control.Wait(token);
                if (suspended) { NtResumeProcess(process.Handle); suspended = false; }
                await Task.Delay(80, token).ConfigureAwait(false);
            }
            await reading; var error = await errors; token.ThrowIfCancellationRequested();
            if (process.ExitCode == 11) throw new ArchivePasswordException("The password is incorrect or the encrypted data is damaged.");
            if (process.ExitCode != 0) throw new RarEncoderException(process.ExitCode, error.Trim());
            parser.Complete();
        }
        catch
        {
            if (!process.HasExited) { if (suspended) NtResumeProcess(process.Handle); process.Kill(true); }
            await process.WaitForExitAsync(); await reading; await errors; throw;
        }
    }

    private Task EditRarAsync(ArchiveIndex index, IReadOnlyList<string>? additions, IReadOnlyCollection<string>? remove,
        string? renameKey, string? renameTo, OperationControl control, IProgress<ArchiveProgress> progress, CancellationToken token, string? password, string additionRoot) => Task.Run(async () =>
    {
        RequireRarEncoder(); ValidateRarPassword(password);
        if (!SupportsEditing(index)) throw new NotSupportedException("Only single-volume archives without symbolic links can be modified.");
        foreach (var item in index.Items) SafeOutputPath(index.Directory, item.Key);
        if (index.Items.Any(e => e.IsEncrypted) && string.IsNullOrEmpty(password)) throw new ArchivePasswordException("A password is required to update this archive.");
        var hash = await ComputeHashesAsync(index.FilePath, token, control);
        EnsureSpace(index.Directory, new FileInfo(index.FilePath).Length * 2);
        using var workspace = new TemporaryWorkspace(index.Directory);
        var staged = Path.Combine(workspace.Path, "updated.rar");
        await CopyFileAsync(index.FilePath, staged, control, progress, token);
        await TestAsync(await OpenAsync(staged, password, token, index.CodePage), password, control, progress, token);
        var passwordArgs = RarPassword(password).ToArray();
        if (renameKey is not null)
        {
            if (string.IsNullOrWhiteSpace(renameTo) || renameTo.IndexOfAny(['/', '\\']) >= 0) throw new ArgumentException("Enter one file or folder name.");
            var source = index.Items.SingleOrDefault(e => e.Key == renameKey) ?? throw new IOException("The archive entry is no longer available.");
            var renamed = source.Parent.Length == 0 ? renameTo : source.Parent + "/" + renameTo;
            SafeOutputPath(workspace.Path, renamed);
            if (index.Items.Any(e => e.Key.Equals(renamed, StringComparison.OrdinalIgnoreCase) || e.Key.StartsWith(renamed + "/", StringComparison.OrdinalIgnoreCase))) throw new IOException("An entry with that name already exists.");
            await RunRarAsync(new[] { "rn" }.Concat(passwordArgs).Concat([staged, renameKey.Replace('/', '\\'), renamed.Replace('/', '\\')]), workspace.Path, control, progress, 0, null, token);
        }
        if (remove is { Count: > 0 })
        {
            var scope = index.Scope(remove);
            if (scope.Count == 0) throw new IOException("The archive entry is no longer available.");
            if (scope.Count == index.Items.Count) throw new NotSupportedException("RAR cannot retain an empty archive. Keep at least one entry.");
            var list = Path.Combine(workspace.Path, "remove.txt"); await File.WriteAllLinesAsync(list, scope.Select(e => e.Key), new UTF8Encoding(false), token);
            await RunRarAsync(new[] { "d", "-n@" + list }.Concat(passwordArgs).Append(staged), workspace.Path, control, progress, 0, null, token);
        }
        if (additions is { Count: > 0 })
        {
            if (additionRoot.Length > 0) SafeOutputPath(workspace.Path, additionRoot);
            var incoming = SourceEntries(additions, index.FilePath, token).Select(e => (e.Source, Key: additionRoot.Length == 0 ? e.Key : additionRoot + "/" + e.Key, e.Directory)).ToList();
            if (incoming.Select(e => e.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != incoming.Count) throw new IOException("Selected items contain duplicate names.");
            foreach (var entry in incoming)
                if (index.Items.Any(e => e.Key.Equals(entry.Key, StringComparison.OrdinalIgnoreCase) && e.IsDirectory != entry.Directory)) throw new IOException("A file and folder have the same name.");
            var input = Path.Combine(workspace.Path, "input");
            long total = incoming.Where(e => !e.Directory).Sum(e => new FileInfo(e.Source).Length);
            EnsureSpace(index.Directory, checked(total * 2 + new FileInfo(staged).Length));
            await StageRarSourcesAsync(input, incoming, control, progress, total, token);
            var roots = additions.Select(p => (additionRoot.Length == 0 ? "" : additionRoot + "/") + Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))).Distinct(StringComparer.OrdinalIgnoreCase);
            var list = Path.Combine(workspace.Path, "add.txt"); await File.WriteAllLinesAsync(list, roots, new UTF8Encoding(false), token);
            await RunRarAsync(new[] { "a", "-r" }.Concat(passwordArgs).Concat([staged, "@" + list]), input, control, progress, total, incoming.Where(e => !e.Directory).ToDictionary(e => e.Key, e => new FileInfo(e.Source).Length, StringComparer.OrdinalIgnoreCase), token);
        }
        var updated = await OpenAsync(staged, password, token, index.CodePage);
        await TestAsync(updated, password, control, progress, token);
        control.Wait(token); await ReplaceArchiveAsync(staged, index.FilePath, hash, token);
    }, token);
}
