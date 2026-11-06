using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace AuraZip.Core;

public sealed record CompressionOptions(string Format = "zip", int Level = 5, string? Password = null, bool EncryptNames = true, long VolumeBytes = 0);
public sealed record CompressionResult(IReadOnlyList<string> Files, long OriginalBytes, long PackedBytes);

public sealed class TemporaryWorkspace : IDisposable
{
    private static readonly string Root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AuraZip"));
    private readonly string _parent;
    public string Path { get; }
    public TemporaryWorkspace(string? parent = null)
    {
        _parent = System.IO.Path.GetFullPath(parent ?? Root);
        Path = System.IO.Path.Combine(_parent, parent is null ? Guid.NewGuid().ToString("N") : $".aurazip-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(Path);
    }
    public static void CleanupExpired()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var path in Directory.EnumerateDirectories(Root))
        {
            if (!Guid.TryParseExact(System.IO.Path.GetFileName(path), "N", out _) || Directory.GetCreationTimeUtc(path) > DateTime.UtcNow.AddDays(-7)) continue;
            try { Delete(new DirectoryInfo(path)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    public void Dispose()
    {
        var full = System.IO.Path.GetFullPath(Path);
        if (!string.Equals(System.IO.Path.GetDirectoryName(full), System.IO.Path.TrimEndingDirectorySeparator(_parent), StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid temporary workspace.");
        try { Delete(new DirectoryInfo(full)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private static void Delete(FileSystemInfo entry)
    {
        if (!entry.Exists) return;
        if (entry is DirectoryInfo directory && (entry.Attributes & FileAttributes.ReparsePoint) == 0)
            foreach (var child in directory.EnumerateFileSystemInfos()) Delete(child);
        entry.Attributes &= ~FileAttributes.ReadOnly;
        entry.Delete();
    }
}

public sealed partial class ArchiveService
{
    public static void EnsureSpace(string destination, long required)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(destination));
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return;
        var drive = new DriveInfo(root);
        if (drive.IsReady && drive.AvailableFreeSpace < required) throw new IOException($"Not enough free space. Required: {ArchiveItem.FormatSize(required)}; available: {ArchiveItem.FormatSize(drive.AvailableFreeSpace)}.");
    }
    private static IEnumerable<(string Source, string Key, bool Directory)> SourceEntries(IReadOnlyList<string> paths, string? excluded = null, CancellationToken token = default)
    {
        foreach (var source in paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(source) && !Directory.Exists(source)) throw new FileNotFoundException("The source no longer exists.", source);
            var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) throw new IOException("Select a folder rather than a drive root.");
            foreach (var item in Walk(source, name)) yield return item;
        }
        IEnumerable<(string, string, bool)> Walk(string path, string key)
        {
            token.ThrowIfCancellationRequested();
            if (excluded is not null && string.Equals(path, excluded, StringComparison.OrdinalIgnoreCase)) yield break;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"Symbolic-link sources are not supported: {path}");
            bool directory = (attributes & FileAttributes.Directory) != 0;
            yield return (path, key, directory);
            if (directory) foreach (var child in Directory.EnumerateFileSystemEntries(path))
                foreach (var item in Walk(child, key + "/" + Path.GetFileName(child))) yield return item;
        }
    }
    public async Task<CompressionResult> CreateArchiveAsync(string destination, IReadOnlyList<string> paths, CompressionOptions options,
        OperationControl control, IProgress<ArchiveProgress> progress, CancellationToken token, bool overwrite = false)
    {
        if (options.Format is not ("zip" or "7z" or "tar" or "tar.gz" or "tar.bz2" or "tar.xz") || options.Level is < 0 or > 9 || options.VolumeBytes < 0) throw new ArgumentException("Invalid compression options.");
        if (options.Format.StartsWith("tar", StringComparison.Ordinal) && !string.IsNullOrEmpty(options.Password)) throw new ArgumentException("TAR archives do not support password encryption.");
        if (options.Format == "zip" && options.Password is { Length: > 0 } zipPassword && (zipPassword.Length > 99 || zipPassword.Any(c => c < 32 || c > 126)))
            throw new ArgumentException("ZIP passwords must contain 1–99 printable ASCII characters. Use 7z for Unicode passwords.");
        destination = Path.GetFullPath(destination);
        if (!File.Exists(_enginePath)) throw new FileNotFoundException("The bundled compression engine is missing.", _enginePath);
        var entries = await Task.Run(() => SourceEntries(paths, destination, token).ToList(), token).ConfigureAwait(false);
        if (entries.Select(e => e.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count) throw new IOException("Selected items have duplicate names. Choose their parent folders instead.");
        long total = entries.Where(e => !e.Directory).Sum(e => new FileInfo(e.Source).Length);
        bool wrappedTar = options.Format.StartsWith("tar.", StringComparison.Ordinal);
        var roots = paths.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !string.Equals(p, destination, StringComparison.OrdinalIgnoreCase)).ToArray();
        var sourceParent = roots.Length > 0 ? Path.GetDirectoryName(roots[0]) : null;
        // Read ordinary selections directly. Isolate mixed-parent selections and outputs
        // inside a selected folder so archive names and source traversal stay unchanged.
        bool direct = sourceParent is not null && roots.All(p => string.Equals(Path.GetDirectoryName(p), sourceParent, StringComparison.OrdinalIgnoreCase))
            && !roots.Any(p => Directory.Exists(p) && destination.StartsWith(p.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        EnsureSpace(destination, checked(total * ((direct ? 1 : 2) + (wrappedTar ? 1 : 0)) + total / 20 + 1024 * 1024));
        var destinationDirectory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(destinationDirectory);
        using var workspace = new TemporaryWorkspace(destinationDirectory);
        var input = direct ? sourceParent! : Path.Combine(workspace.Path, "input");
        if (!direct) Directory.CreateDirectory(input);
        long copied = 0; int count = entries.Count(e => !e.Directory);
        progress.Report(new("", 0, total, 0, count, ArchiveOperationStage.Preparing));
        if (!direct)
        await Task.Run(() =>
        {
            foreach (var entry in entries)
            {
                control.Wait(token);
                var target = SafeOutputPath(input, entry.Key);
                if (entry.Directory) Directory.CreateDirectory(target);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var source = File.OpenRead(entry.Source); using var output = File.Create(target);
                    var buffer = new byte[128 * 1024]; int read;
                    while ((read = source.Read(buffer)) > 0) { control.Wait(token); output.Write(buffer, 0, read); copied += read; progress.Report(new(entry.Key, copied / 10, total, 0, count, ArchiveOperationStage.Preparing)); }
                }
                if (!entry.Directory)
                {
                    File.SetLastWriteTime(target, File.GetLastWriteTime(entry.Source));
                    File.SetAttributes(target, File.GetAttributes(entry.Source) & (FileAttributes.Archive | FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.System));
                }
            }
            foreach (var entry in entries.Where(e => e.Directory).OrderByDescending(e => e.Key.Length)) Directory.SetLastWriteTime(SafeOutputPath(input, entry.Key), Directory.GetLastWriteTime(entry.Source));
        }, token);
        var outputArchive = Path.Combine(workspace.Path, "output." + options.Format);
        var list = Path.Combine(workspace.Path, "inputs.txt");
        await File.WriteAllLinesAsync(list, (direct ? roots : Directory.GetFileSystemEntries(input)).Select(Path.GetFileName)!, new UTF8Encoding(false), token);
        var arguments = new List<string> { "a", "-t" + (wrappedTar ? "tar" : options.Format), "-scsUTF-8", "-sccUTF-8", "-bsp1", "-bb1", "-y", "-spd" };
        if (!options.Format.StartsWith("tar", StringComparison.Ordinal)) { arguments.Add("-mx=" + options.Level); arguments.Add("-mmt=on"); }
        if (!string.IsNullOrEmpty(options.Password)) { arguments.Add("-p" + options.Password); arguments.Add(options.Format == "zip" ? "-mem=AES256" : "-mhe=" + (options.EncryptNames ? "on" : "off")); }
        if (options.VolumeBytes > 0 && !wrappedTar) arguments.Add("-v" + options.VolumeBytes + "b");
        var tarArchive = Path.Combine(workspace.Path, "content.tar");
        arguments.Add(wrappedTar ? tarArchive : outputArchive); arguments.Add("@" + list);
        long start = direct ? 0 : total / 10;
        long firstEnd = wrappedTar ? total / 3 : total - total / 100;
        var fileSizes = entries.Where(e => !e.Directory).ToDictionary(e => e.Key, e => new FileInfo(e.Source).Length, StringComparer.OrdinalIgnoreCase);
        var firstProgress = new CompressionProgress(p => progress.Report(p with { BytesDone = start + (long)((double)p.BytesDone / Math.Max(1, total) * (firstEnd - start)), Stage = ArchiveOperationStage.Compressing }));
        await RunEngineAsync(arguments, input, control, firstProgress, total, count, token, fileSizes);
        if (wrappedTar)
        {
            var codec = options.Format[4..] == "gz" ? "gzip" : options.Format[4..] == "bz2" ? "bzip2" : "xz";
            var wrap = new List<string> { "a", "-t" + codec, "-mx=" + options.Level, "-bsp1", "-sccUTF-8", "-y" };
            if (options.VolumeBytes > 0) wrap.Add("-v" + options.VolumeBytes + "b");
            wrap.Add(outputArchive); wrap.Add(tarArchive);
            var wrapProgress = new CompressionProgress(p => progress.Report(p with { FileName = Path.GetFileName(destination), BytesDone = firstEnd + (long)((double)p.BytesDone / Math.Max(1, total) * (total - total / 100 - firstEnd)), FilesDone = count, Stage = ArchiveOperationStage.Compressing }));
            await RunEngineAsync(wrap, workspace.Path, control, wrapProgress, total, count, token);
        }
        var outputs = Directory.EnumerateFiles(workspace.Path, "output." + options.Format + "*").OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (outputs.Count == 0) throw new IOException("The compression engine did not produce an archive.");
        var targets = outputs.Select(p => destination + p[outputArchive.Length..]).ToArray();
        if (targets.Any(p => Directory.Exists(p) || (!overwrite && File.Exists(p)))) throw new IOException("An output archive already exists. Choose a different name.");
        long packed = outputs.Sum(p => new FileInfo(p).Length);
        // The engine output is already on the destination volume: commit by renaming.
        progress.Report(new(Path.GetFileName(destination), total - total / 100, total, count, count, ArchiveOperationStage.Finalizing));
        await Task.Run(() =>
        {
                var committed = new List<string>();
                try { for (int i = 0; i < outputs.Count; i++) { control.Wait(token); File.Move(outputs[i], targets[i], overwrite); committed.Add(targets[i]); } }
                catch { if (!overwrite) foreach (var path in committed) File.Delete(path); throw; }
        }, token);
        progress.Report(new(Path.GetFileName(destination), total, total, count, count));
        return new(targets, total, packed);
    }

    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(nint process);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(nint process);
    private async Task RunEngineAsync(IEnumerable<string> arguments, string workingDirectory, OperationControl control,
        IProgress<ArchiveProgress> progress, long total, int files, CancellationToken token, IReadOnlyDictionary<string, long>? fileSizes = null)
    {
        var start = new ProcessStartInfo(_enginePath) { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var parser = new CompressionProgressParser(progress, total, files, fileSizes);
        var reading = Task.Run(async () =>
        {
            var buffer = new char[2048]; var line = new StringBuilder(); int count;
            while ((count = await process.StandardOutput.ReadAsync(buffer)) > 0)
            {
                for (int i = 0; i < count; i++)
                    if (buffer[i] is '\r' or '\n') { parser.Read(line.ToString()); line.Clear(); }
                    else if (buffer[i] != '\b') line.Append(buffer[i]);
            }
            if (line.Length > 0) parser.Read(line.ToString());
        });
        var errors = process.StandardError.ReadToEndAsync();
        bool suspended = false;
        try
        {
            while (!process.HasExited)
            {
                if (control.IsPaused && !suspended && OperatingSystem.IsWindows()) { suspended = NtSuspendProcess(process.Handle) >= 0; }
                control.Wait(token);
                if (suspended) { NtResumeProcess(process.Handle); suspended = false; }
                await Task.Delay(100, token).ConfigureAwait(false);
            }
            await reading;
            var error = await errors;
            if (process.ExitCode != 0) throw new IOException("Compression engine error " + process.ExitCode + ": " + error.Trim());
            parser.Complete();
        }
        catch
        {
            if (suspended && !process.HasExited) NtResumeProcess(process.Handle);
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(); await reading; await errors;
            throw;
        }
    }

    public async Task EditZipAsync(ArchiveIndex index, IReadOnlyList<string>? additions, IReadOnlyCollection<string>? remove,
        string? renameKey, string? renameTo, OperationControl control, IProgress<ArchiveProgress> progress, CancellationToken token,
        string? password = null, string additionRoot = "")
    {
        if (index.FilePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            await EditSevenZipAsync(index, additions, remove, renameKey, renameTo, control, progress, token, password, additionRoot);
            return;
        }
        if (!index.Items.Any(e => e.IsEncrypted))
        {
            await EditPlainZipAsync(index, additions, remove, renameKey, renameTo, control, progress, token, additionRoot);
            return;
        }
        if (!index.FilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || ArchiveFormats.Volumes(index.FilePath).Count != 1)
            throw new NotSupportedException("Only single-volume ZIP archives can be modified.");
        if (string.IsNullOrEmpty(password)) throw new IOException("A password is required to update an encrypted ZIP archive.");
        var originalHash = await ComputeHashesAsync(index.FilePath, token);
        using var workspace = new TemporaryWorkspace();
        var extracted = Path.Combine(workspace.Path, "original");
        await ExtractAsync(index, extracted, null, ConflictPolicy.Fail, password, control, progress, token);
        var plain = Path.Combine(workspace.Path, "working.zip");
        await CreateZipAsync(plain, Directory.GetFileSystemEntries(extracted), control, progress, token);
        await EditPlainZipAsync(await OpenAsync(plain, token: token), additions, remove, renameKey, renameTo, control, progress, token, additionRoot);
        var revised = await OpenAsync(plain, token: token);
        var finalContent = Path.Combine(workspace.Path, "revised");
        await ExtractAsync(revised, finalContent, null, ConflictPolicy.Fail, null, control, progress, token);
        var encrypted = Path.Combine(workspace.Path, "encrypted.zip");
        if (revised.Items.Count == 0) { using var empty = ZipFile.Open(encrypted, ZipArchiveMode.Create); }
        else await CreateArchiveAsync(encrypted, Directory.GetFileSystemEntries(finalContent), new("zip", Password: password), control, progress, token);
        WriteZipComment(encrypted, ReadZipCommentBytes(index.FilePath));
        var temporary = index.FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.Copy(encrypted, temporary); control.Wait(token); if (await ComputeHashesAsync(index.FilePath, token) != originalHash) throw new IOException("The archive changed outside AuraZip. Reopen it before saving."); File.Move(temporary, index.FilePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private Task EditPlainZipAsync(ArchiveIndex index, IReadOnlyList<string>? additions, IReadOnlyCollection<string>? remove,
        string? renameKey, string? renameTo, OperationControl control, IProgress<ArchiveProgress> progress, CancellationToken token, string additionRoot) => Task.Run(async () =>
    {
        if (!index.FilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || ArchiveFormats.Volumes(index.FilePath).Count != 1) throw new NotSupportedException("Only single-volume ZIP archives can be modified.");
        if (index.Items.Any(e => e.IsEncrypted)) throw new NotSupportedException("Encrypted ZIP archives must be recreated to preserve their encryption.");
        var originalHash = await ComputeHashesAsync(index.FilePath, token);
        string? renamed = null;
        if (renameKey is not null)
        {
            if (string.IsNullOrWhiteSpace(renameTo) || renameTo.IndexOfAny(['/', '\\']) >= 0) throw new ArgumentException("Enter one file or folder name.");
            var parent = renameKey.Contains('/') ? renameKey[..renameKey.LastIndexOf('/')] + "/" : "";
            renamed = parent + renameTo;
            SafeOutputPath(Path.GetTempPath(), renamed);
            if (index.Items.Any(e => string.Equals(e.Key, renamed, StringComparison.OrdinalIgnoreCase))) throw new IOException("An entry with that name already exists.");
        }
        var toRemove = remove?.ToHashSet(StringComparer.Ordinal) ?? new();
        if (additionRoot.Length > 0) SafeOutputPath(Path.GetTempPath(), additionRoot);
        var incoming = additions is null ? [] : SourceEntries(additions, index.FilePath, token).Select(e => (e.Source, Key: additionRoot.Length == 0 ? e.Key : additionRoot + "/" + e.Key, e.Directory)).ToList();
        if (incoming.Select(e => e.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != incoming.Count) throw new IOException("Selected items contain duplicate names.");
        var incomingKeys = incoming.Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var metadata = index.Items.ToDictionary(e => e.Key, StringComparer.Ordinal);
        foreach (var source in incoming)
            if (index.Items.Any(e => string.Equals(e.Key, source.Key, StringComparison.OrdinalIgnoreCase) && e.IsDirectory != source.Directory))
                throw new IOException("A file and folder have the same name. Rename or remove the existing entry first.");
        EnsureSpace(index.Directory, new FileInfo(index.FilePath).Length + incoming.Where(e => !e.Directory).Sum(e => new FileInfo(e.Source).Length));
        var temporary = index.FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var original = ZipFile.Open(index.FilePath, ZipArchiveMode.Read, index.CodePage is int cp ? Encoding.GetEncoding(cp) : null))
            using (var output = ZipFile.Open(temporary, ZipArchiveMode.Create, Encoding.UTF8))
            {
                long bytes = 0, total = index.Items.Where(e => !e.IsDirectory).Sum(e => e.Size) + incoming.Where(e => !e.Directory).Sum(e => new FileInfo(e.Source).Length);
                int done = 0, count = original.Entries.Count + incoming.Count;
                var buffer = new byte[128 * 1024];
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var source in original.Entries)
                {
                    control.Wait(token);
                    var key = Normalize(source.FullName);
                    if (toRemove.Contains(key) || ArchiveIndex.HasSelectedAncestor(key, toRemove) || incomingKeys.Contains(key)) continue;
                    var newKey = renamed is not null && (key == renameKey || key.StartsWith(renameKey + "/", StringComparison.Ordinal)) ? renamed + key[renameKey!.Length..] : key;
                    SafeOutputPath(Path.GetTempPath(), newKey);
                    if (!keys.Add(newKey)) throw new IOException("The update would produce duplicate archive entries.");
                    bool dir = source.FullName.EndsWith('/');
                    var target = output.CreateEntry(newKey + (dir ? "/" : ""), CompressionLevel.Optimal);
                    target.LastWriteTime = source.LastWriteTime; target.ExternalAttributes = source.ExternalAttributes;
                    if (!dir) { using var input = source.Open(); using var stream = target.Open(); Copy(input, stream, key, true); }
                    progress.Report(new(newKey, bytes, total, ++done, count));
                }
                foreach (var source in incoming)
                {
                    control.Wait(token);
                    SafeOutputPath(Path.GetTempPath(), source.Key);
                    if (!keys.Add(source.Key)) throw new IOException("The update would produce duplicate archive entries.");
                    var target = output.CreateEntry(source.Key + (source.Directory ? "/" : ""), CompressionLevel.Optimal);
                    var modified = File.GetLastWriteTime(source.Source); if (modified.Year is >= 1980 and <= 2107) target.LastWriteTime = modified;
                    target.ExternalAttributes = (int)File.GetAttributes(source.Source);
                    if (!source.Directory) { using var input = File.OpenRead(source.Source); using var stream = target.Open(); Copy(input, stream, source.Key); }
                    progress.Report(new(source.Key, bytes, total, ++done, count));
                }
                void Copy(Stream input, Stream outputStream, string name, bool validate = false)
                {
                    var checksum = new Crc32(); long actual = 0;
                    int read;
                    while ((read = input.Read(buffer)) > 0) { control.Wait(token); outputStream.Write(buffer, 0, read); if (validate) checksum.Append(buffer, read); actual += read; bytes += read; progress.Report(new(name, bytes, total, done, count)); }
                    if (validate) checksum.Validate(metadata.GetValueOrDefault(name), name, actual);
                }
            }
            control.Wait(token);
            WriteZipComment(temporary, ReadZipCommentBytes(index.FilePath));
            if (await ComputeHashesAsync(index.FilePath, token) != originalHash) throw new IOException("The archive changed outside AuraZip. Reopen it before saving.");
            File.Move(temporary, index.FilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, token);
}
