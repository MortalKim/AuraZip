using System.Diagnostics;
using System.Text;

namespace AuraZip.Core;

public sealed class ArchivePasswordException(string message, Exception? inner = null) : IOException(message, inner);

public sealed partial class ArchiveService
{
    public static bool IsPasswordError(Exception error) => error is ArchivePasswordException
        or SharpCompress.Common.CryptographicException or System.Security.Cryptography.CryptographicException
        || error.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
        || error.Message.Contains("encrypted", StringComparison.OrdinalIgnoreCase)
        || (error.InnerException is { } inner && IsPasswordError(inner));

    private static bool NeedsVerifiedPreview(string path, string? password)
    {
        using var stream = File.OpenRead(ArchiveFormats.FirstVolume(path));
        return IsRar(stream) || (!string.IsNullOrEmpty(password) && (IsZip(stream) || IsSevenZip(stream)));
    }

    private static bool NeedsVerifiedRead(ArchiveIndex index)
    {
        if (index.IsSingleStream || index.Items.Any(i => i.IsEncrypted)) return true;
        using var stream = File.OpenRead(index.FilePath);
        return IsRar(stream);
    }

    private static void AddReadEncoding(List<string> arguments, ArchiveIndex index)
    {
        if (index.CodePage is not int codePage) return;
        using var source = File.OpenRead(index.FilePath);
        if (IsZip(source)) arguments.Add("-mcp=" + codePage);
    }

    private Process StartReadProcess(IEnumerable<string> arguments)
    {
        if (!File.Exists(_enginePath)) throw new FileNotFoundException("The bundled compression engine is missing.", _enginePath);
        var start = new ProcessStartInfo(_enginePath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var process = Process.Start(start)!;
        process.StandardInput.Close();
        return process;
    }

    private static void ThrowReadError(string error, int exitCode)
    {
        if (error.Contains("password", StringComparison.OrdinalIgnoreCase))
            throw new ArchivePasswordException("The password is incorrect or the encrypted data is damaged.");
        throw new InvalidDataException($"Archive engine error {exitCode}: {error.Trim()}");
    }

    private static void KillReadProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
    }

    private void CheckHeaderPassword(string path, string password, CancellationToken token)
    {
        using var process = StartReadProcess(new[] { "l", "-slt", "-sccUTF-8", "-p" + password, "--", path });
        using var registration = token.Register(() => KillReadProcess(process));
        var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit(); output.GetAwaiter().GetResult();
        var message = error.GetAwaiter().GetResult(); token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) ThrowReadError(message, process.ExitCode);
    }

    private void RunReadEngine(string command, ArchiveIndex index, string? password, OperationControl control,
        IProgress<ArchiveProgress> progress, CancellationToken token, string? output = null, string? list = null,
        IReadOnlyList<ArchiveItem>? entries = null)
    {
        var files = (entries ?? index.Items).Where(i => !i.IsDirectory).ToArray();
        if (files.Any(i => i.IsEncrypted) && string.IsNullOrEmpty(password)) throw new ArchivePasswordException("A password is required to read this archive.");
        var args = new List<string> { command, "-p" + (password ?? "-"), "-y", "-spd", "-ssc", "-scsUTF-8", "-sccUTF-8", "-bsp1", "-bb1" };
        if (output is not null) args.Add("-o" + output);
        if (list is not null) args.Add("-i@" + list);
        AddReadEncoding(args, index);
        args.Add("--"); args.Add(index.FilePath);
        long total = files.Sum(i => i.Size);
        try
        {
            RunEngineAsync(args, index.Directory, control, progress, total, files.Length, token,
                files.GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(i => i.Size), StringComparer.OrdinalIgnoreCase)).GetAwaiter().GetResult();
        }
        catch (IOException ex) when (IsPasswordError(ex))
        { throw new ArchivePasswordException("The password is incorrect or the encrypted data is damaged.", ex); }
    }

    private OperationResult ExtractVerifiedArchive(ArchiveIndex index, string target, IReadOnlyList<ArchiveItem> scope,
        ConflictPolicy policy, string? password, OperationControl control, IProgress<ArchiveProgress> progress,
        CancellationToken token, string relativeRoot)
    {
        EnsureSpace(target, scope.Where(i => !i.IsDirectory).Sum(i => i.Size));
        CheckReparsePoints(target); Directory.CreateDirectory(target);
        var files = scope.Where(i => !i.IsDirectory).ToArray();
        // Decompress into an isolated directory on the destination volume. No file is
        // published until the engine has verified the CRC/hash or AES authentication.
        using var workspace = new TemporaryWorkspace(Path.GetFullPath(target));
        var data = Path.Combine(workspace.Path, "data"); Directory.CreateDirectory(data);
        var list = Path.Combine(workspace.Path, "entries.txt");
        var planned = new List<(ArchiveItem Item, string Output)>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int skipped = 0; long skippedBytes = 0;
        foreach (var item in files)
        {
            control.Wait(token);
            var output = SafeOutputPath(target, RelativeKey(item.Key, relativeRoot));
            CheckReparsePoints(output);
            if (string.Equals(output, index.FilePath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The destination would overwrite the open archive. Choose a different directory.");
            if (File.Exists(output) || reserved.Contains(output))
            {
                if (policy == ConflictPolicy.Skip || (policy == ConflictPolicy.Newer && File.Exists(output) && (item.Modified is not DateTime incoming || File.GetLastWriteTime(output) >= incoming))) { skipped++; skippedBytes += item.Size; continue; }
                if (policy == ConflictPolicy.Fail) throw new IOException($"The destination file already exists: {output}");
                if (policy == ConflictPolicy.Rename)
                {
                    var original = output;
                    for (int suffix = 2; File.Exists(output) || reserved.Contains(output); suffix++)
                        output = Path.Combine(Path.GetDirectoryName(original)!, Path.GetFileNameWithoutExtension(original) + $" ({suffix})" + Path.GetExtension(original));
                }
            }
            reserved.Add(output); planned.Add((item, output));
        }
        var stagedRoots = new Dictionary<string, string>(StringComparer.Ordinal);
        if (planned.Select(p => p.Item.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != planned.Count)
        {
            // A case-insensitive staging directory cannot hold both a.txt and A.txt.
            // Isolate these selections so native extraction cannot overwrite a sibling.
            long completedBytes = 0, totalBytes = planned.Sum(p => p.Item.Size); int completedFiles = 0;
            foreach (var (item, _) in planned)
            {
                var itemRoot = Path.Combine(data, completedFiles.ToString()); Directory.CreateDirectory(itemRoot);
                stagedRoots[item.Key] = itemRoot;
                File.WriteAllLines(list, new[] { item.Key }, new UTF8Encoding(false));
                var itemProgress = new CompressionProgress(p => progress.Report(p with { BytesDone = completedBytes + p.BytesDone, TotalBytes = totalBytes, FilesDone = completedFiles + p.FilesDone, TotalFiles = planned.Count }));
                RunReadEngine("x", index, password, control, itemProgress, token, itemRoot, list, new[] { item });
                completedBytes += item.Size; completedFiles++;
            }
        }
        else if (planned.Count > 0)
        {
            File.WriteAllLines(list, planned.Select(i => i.Item.Key), new UTF8Encoding(false));
            RunReadEngine("x", index, password, control, progress, token, data, list, planned.Select(i => i.Item).ToArray());
        }
        foreach (var dir in scope.Where(i => i.IsDirectory))
        {
            var output = SafeOutputPath(target, RelativeKey(dir.Key, relativeRoot));
            CheckReparsePoints(output); Directory.CreateDirectory(output);
        }
        long writtenBytes = 0;
        foreach (var (item, output) in planned)
        {
            control.Wait(token);
            var staged = SafeOutputPath(stagedRoots.GetValueOrDefault(item.Key, data), item.Key);
            CheckReparsePoints(staged); CheckReparsePoints(output);
            if (!File.Exists(staged) || (item.IsSizeKnown && new FileInfo(staged).Length != item.Size)) throw new InvalidDataException($"Uncompressed size mismatch: {item.Key}");
            writtenBytes += new FileInfo(staged).Length;
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Move(staged, output, policy is ConflictPolicy.Overwrite or ConflictPolicy.Newer);
        }
        foreach (var dir in scope.Where(i => i.IsDirectory).OrderByDescending(i => i.Key.Length))
        {
            var output = SafeOutputPath(target, RelativeKey(dir.Key, relativeRoot));
            if (dir.Modified is DateTime modified) Directory.SetLastWriteTime(output, modified);
            if (dir.Attributes is int attributes) File.SetAttributes(output, (FileAttributes)attributes & (FileAttributes.Archive | FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.System));
        }
        long bytes = writtenBytes + skippedBytes;
        progress.Report(new(index.Name, bytes, bytes, files.Length, files.Length));
        return new(planned.Count, skipped, target);
    }

    private byte[] ReadVerifiedPreview(string path, string key, string? password, int limit, CancellationToken token, int? codePage, ArchiveIndex? openedIndex = null)
    {
        path = ArchiveFormats.FirstVolume(path);
        var index = openedIndex ?? OpenAsync(path, password, token, codePage).GetAwaiter().GetResult();
        var item = index.Items.FirstOrDefault(i => i.Key == key && !i.IsDirectory) ?? throw new FileNotFoundException("The archive entry is no longer available.", key);
        if (item.Size > limit) throw new InvalidOperationException($"Preview is limited to {ArchiveItem.FormatSize(limit)}. Extract the file to view it.");
        if (item.IsEncrypted && string.IsNullOrEmpty(password)) throw new ArchivePasswordException("A password is required to read this archive.");
        var arguments = new List<string> { "x", "-so", "-y", "-spd", "-ssc", "-sccUTF-8", "-p" + (password ?? "-"), "-i!" + key };
        AddReadEncoding(arguments, index); arguments.Add("--"); arguments.Add(path);
        using var process = StartReadProcess(arguments);
        using var registration = token.Register(() => KillReadProcess(process));
        var errors = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream(); var buffer = new byte[8192];
        try
        {
            int count;
            while ((count = process.StandardOutput.BaseStream.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                if (output.Length + count > limit) throw new InvalidOperationException("File exceeds the preview size limit.");
                output.Write(buffer, 0, count);
            }
            process.WaitForExit(); var error = errors.GetAwaiter().GetResult(); token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) ThrowReadError(error, process.ExitCode);
            if (item.IsSizeKnown && output.Length != item.Size) throw new InvalidDataException($"Uncompressed size mismatch: {key}");
            return output.ToArray();
        }
        finally { KillReadProcess(process); process.WaitForExit(); errors.GetAwaiter().GetResult(); }
    }
}
