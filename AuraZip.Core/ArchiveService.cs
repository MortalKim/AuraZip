using System.IO.Compression;
using SharpCompress.Readers;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace AuraZip.Core;

public sealed partial class ArchiveService
{
    private readonly string _enginePath;
    public ArchiveService(string? enginePath = null, string? rarEncoderPath = null)
    {
        _enginePath = enginePath ?? Path.Combine(AppContext.BaseDirectory, "Engine", "7z.exe");
        ConfigureRarEncoder(rarEncoderPath);
    }
    public string? RarEncoderPath { get; private set; }
    public bool CanCreateRar => RarEncoderPath is not null && File.Exists(RarEncoderPath);
    public void ConfigureRarEncoder(string? path) => RarEncoderPath = RarEncoder.Find(path);
    static ArchiveService() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    public Task<ArchiveIndex> OpenAsync(string path, string? password = null, CancellationToken token = default, int? codePage = null) => Task.Run(() =>
    {
        path = ArchiveFormats.FirstVolume(path);
        var volumes = ArchiveFormats.Volumes(path);
        var items = new Dictionary<string, ArchiveItem>(StringComparer.Ordinal);
        using var source = File.OpenRead(path);
        bool zip = IsZip(source), rar = IsRar(source), sevenZip = IsSevenZip(source), compressedStream = IsCompressedStream(source);
        if (rar) return ReadRarIndex(path, password, codePage, token);
        void AddEntry(IEntry e)
        {
            token.ThrowIfCancellationRequested();
            var key = Normalize(e.Key ?? "");
            if (key.Length == 0) return;
            var pieces = key.Split('/');
            for (int i = 1; i < pieces.Length; i++)
            {
                var parent = string.Join('/', pieces.Take(i));
                items.TryAdd(parent, new ArchiveItem(parent, true));
            }
            int? attributes = null;
            try { attributes = e.Attrib; } catch (NotImplementedException) { }
            // RAR5 can key its stored checksums to the password. Encrypted RAR data
            // is verified by the bundled engine rather than compared to a plain CRC.
            items[key] = new(key, e.IsDirectory, e.Size, e.CompressedSize, e.LastModifiedTime, e.IsEncrypted, !e.IsDirectory && !(rar && e.IsEncrypted) && (zip || e.Crc != 0) && !(e.IsEncrypted && e.Crc == 0) ? unchecked((uint)e.Crc) : null, attributes, !string.IsNullOrEmpty(e.LinkTarget));
        }
        try
        {
            if (zip || sevenZip || (volumes.Count > 1 && rar))
            {
                using var owned = new OwnedArchive(volumes, password, codePage);
                foreach (var entry in owned.Archive.Entries) AddEntry(entry);
            }
            else
            {
                using var combined = volumes.Count > 1 ? new VolumeReadStream(volumes) : null;
                using var reader = ReaderFactory.Open(combined ?? (Stream)source, ReadOptions(password, codePage));
                if (reader is SharpCompress.Readers.GZip.GZipReader) return ReadSingleStreamIndex(path, token, codePage);
                while (reader.MoveToNextEntry()) AddEntry(reader.Entry);
            }
        }
        catch (Exception ex) when (compressedStream && ex is InvalidFormatException or ArgumentNullException)
        { return ReadSingleStreamIndex(path, token, codePage); }
        catch (InvalidFormatException) when ((rar || sevenZip) && !string.IsNullOrEmpty(password))
        {
            // RAR4 and 7z can report bad header decryption as a format/CRC error.
            // Ask the engine to distinguish it from bad data.
            CheckHeaderPassword(path, password, token);
            throw;
        }
        return new ArchiveIndex(Path.GetFullPath(path), items.Values.ToList(), codePage);
    }, token);

    private static bool IsSevenZip(Stream stream)
    {
        Span<byte> signature = stackalloc byte[6];
        var position = stream.Position;
        var count = stream.Read(signature); stream.Position = position;
        return count == 6 && signature.SequenceEqual(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c });
    }

    private static bool IsZip(Stream stream)
    {
        Span<byte> signature = stackalloc byte[4]; var position = stream.Position;
        var count = stream.Read(signature); stream.Position = position;
        return count == 4 && signature[0] == 0x50 && signature[1] == 0x4b &&
            ((signature[2] == 3 && signature[3] == 4) || (signature[2] == 5 && signature[3] == 6) || (signature[2] == 7 && signature[3] == 8));
    }

    private sealed class Crc32
    {
        private static readonly uint[] Table = CreateTable();
        private uint _value = uint.MaxValue;
        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++) { uint value = i; for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? (value >> 1) ^ 0xedb88320u : value >> 1; table[i] = value; }
            return table;
        }
        public void Append(byte[] bytes, int count) { for (int i = 0; i < count; i++) _value = Table[(_value ^ bytes[i]) & 0xff] ^ (_value >> 8); }
        public void Validate(ArchiveItem? entry, string key, long actualSize)
        {
            if (entry is null) return;
            if (entry.Size != actualSize) throw new InvalidDataException($"Uncompressed size mismatch: {key}");
            if (entry.ExpectedCrc is uint expected && expected != ~_value) throw new InvalidDataException($"CRC checksum mismatch: {key}");
        }
    }

    private static string RelativeKey(string key, string parent) => parent.Length > 0 && key.StartsWith(parent + "/", StringComparison.Ordinal) ? key[(parent.Length + 1)..] : key;

    private sealed class OwnedArchive : IDisposable
    {
        private readonly List<Stream> _streams = new();
        public IArchive Archive { get; }
        public OwnedArchive(IReadOnlyList<string> paths, string? password, int? codePage)
        {
            try
            {
                foreach (var path in paths) _streams.Add(File.OpenRead(path));
                var options = ReadOptions(password, codePage); options.LeaveStreamOpen = true;
                Archive = ArchiveFactory.Open(_streams, options);
            }
            catch { foreach (var stream in _streams) stream.Dispose(); throw; }
        }
        public void Dispose() { try { Archive.Dispose(); } finally { foreach (var stream in _streams) stream.Dispose(); } }
    }

    private static bool IsRar(Stream stream)
    {
        Span<byte> signature = stackalloc byte[4]; var position = stream.Position;
        var count = stream.Read(signature); stream.Position = position;
        return count == 4 && signature.SequenceEqual(new byte[] { 0x52, 0x61, 0x72, 0x21 });
    }
    private sealed class ReaderSession(IReader? reader = null, IArchive? archive = null, Stream? stream = null, IDisposable? owner = null, bool zip = false) : IDisposable
    {
        private readonly IEnumerator<IArchiveEntry>? _entries = reader is null ? archive!.Entries.GetEnumerator() : null;
        public ReaderSession Reader => this;
        public IEntry Entry => reader?.Entry ?? _entries!.Current;
        public bool HasPlainCrc => zip || Entry.Crc != 0;
        public bool MoveToNextEntry() => reader?.MoveToNextEntry() ?? _entries!.MoveNext();
        public Stream OpenEntryStream() => reader?.OpenEntryStream() ?? _entries!.Current.OpenEntryStream();
        public void Dispose() { try { _entries?.Dispose(); reader?.Dispose(); if (owner is null) archive?.Dispose(); } finally { owner?.Dispose(); stream?.Dispose(); } }
    }

    private static ReaderOptions ReadOptions(string? password, int? codePage) => new()
    {
        Password = password, LeaveStreamOpen = false,
        ArchiveEncoding = codePage is int cp ? new ArchiveEncoding { Forced = System.Text.Encoding.GetEncoding(cp) } : new ArchiveEncoding()
    };
    private static ReaderSession OpenSession(string path, string? password, int? codePage = null)
    {
        var parts = ArchiveFormats.Volumes(path);
        var source = File.OpenRead(path);
        try
        {
            if (!IsSevenZip(source) && !IsZip(source) && parts.Count == 1) return new ReaderSession(ReaderFactory.Open(source, ReadOptions(password, codePage)), stream: source);
            if (!IsSevenZip(source) && !IsZip(source) && !IsRar(source))
            {
                source.Dispose();
                var combined = new VolumeReadStream(parts);
                try { return new ReaderSession(ReaderFactory.Open(combined, ReadOptions(password, codePage)), stream: combined); }
                catch { combined.Dispose(); throw; }
            }
            source.Dispose();
            var owned = new OwnedArchive(parts, password, codePage);
            var archive = owned.Archive;
            try { return archive.IsSolid || archive.Type == ArchiveType.SevenZip ? new ReaderSession(archive.ExtractAllEntries(), archive, owner: owned) : new ReaderSession(archive: archive, owner: owned, zip: archive.Type == ArchiveType.Zip); }
            catch { owned.Dispose(); throw; }
        }
        catch { source.Dispose(); throw; }
    }

    public static string Normalize(string key) => key.Replace('\\', '/').TrimEnd('/');

    public static string ResolveDestination(ArchiveIndex archive, string baseDirectory, bool smart, bool createFolder, IReadOnlyCollection<string>? selected = null)
    {
        if (!Path.IsPathFullyQualified(baseDirectory)) throw new ArgumentException("Enter an absolute destination path.");
        var selectedKeys = selected?.ToHashSet(StringComparer.Ordinal);
        var scope = selected is null ? archive.Children("").Select(i => i.Key).ToList() :
            selected.Where(key => !ArchiveIndex.HasSelectedAncestor(key, selectedKeys!)).ToList();
        var wrap = smart ? scope.Count > 1 : createFolder;
        var name = ArchiveFormats.BaseName(archive.FilePath);
        foreach (var suffix in new[] { ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst", ".tgz", ".tbz2", ".tbz", ".txz", ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { name = name[..^suffix.Length]; break; }
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') || ReservedNames.Contains(name.Split('.')[0])) name = "Extracted";
        return Path.GetFullPath(wrap ? Path.Combine(baseDirectory, name) : baseDirectory);
    }

    public static string SafeOutputPath(string root, string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.StartsWith('/') || key.StartsWith('\\') || key.Contains(':'))
            throw new InvalidDataException($"Unsafe archive path: {key}");
        var pieces = key.Replace('\\', '/').Split('/');
        if (pieces.Any(p => p is "" or "." or ".." || p.EndsWith(' ') || p.EndsWith('.') ||
            p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || ReservedNames.Contains(p.Split('.')[0])))
            throw new InvalidDataException($"Unsafe archive path: {key}");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var output = Path.GetFullPath(Path.Combine(fullRoot, Path.Combine(pieces)));
        if (!output.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Archive path escapes the destination: {key}");
        return output;
    }

    private static void CheckReparsePoints(string path)
    {
        for (var p = new DirectoryInfo(path); p is not null; p = p.Parent)
            if (p.Exists && (p.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Extraction through symbolic links is not supported: {p.FullName}");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Refusing to overwrite a symbolic link: {path}");
    }

    public static IReadOnlyList<string> FindConflicts(ArchiveIndex index, string target, IReadOnlyCollection<string>? selected, string relativeRoot = "") =>
        index.Scope(selected).Where(e => !e.IsDirectory).Select(e => SafeOutputPath(target, RelativeKey(e.Key, relativeRoot))).Where(File.Exists).ToList();

    public Task<OperationResult> ExtractAsync(ArchiveIndex index, string target, IReadOnlyCollection<string>? selected,
        ConflictPolicy policy, string? password, OperationControl control, IProgress<ArchiveProgress> progress, CancellationToken token, string relativeRoot = "") => Task.Run(() =>
    {
        var scope = index.Scope(selected);
        var metadata = scope.ToDictionary(i => i.Key, StringComparer.Ordinal);
        var keys = scope.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        // Validate every path before creating the first output file.
        foreach (var item in scope)
        {
            SafeOutputPath(target, RelativeKey(item.Key, relativeRoot));
            if (item.IsLink) throw new InvalidDataException($"Symbolic-link entries are not supported: {item.Key}");
        }
        if (NeedsVerifiedRead(index)) return ExtractVerifiedArchive(index, target, scope, policy, password, control, progress, token, relativeRoot);
        EnsureSpace(target, scope.Where(e => !e.IsDirectory).Sum(e => e.Size));
        CheckReparsePoints(target);
        Directory.CreateDirectory(target);
        foreach (var dir in scope.Where(e => e.IsDirectory))
        {
            control.Wait(token);
            var output = SafeOutputPath(target, RelativeKey(dir.Key, relativeRoot));
            CheckReparsePoints(output);
            Directory.CreateDirectory(output);
        }
        long bytesDone = 0, total = scope.Where(e => !e.IsDirectory).Sum(e => e.Size);
        int done = 0, skipped = 0, totalFiles = scope.Count(e => !e.IsDirectory);
        using var session = OpenSession(index.FilePath, password, index.CodePage);
        var reader = session.Reader;
        var buffer = new byte[128 * 1024];
        while (reader.MoveToNextEntry())
        {
            control.Wait(token);
            var entry = reader.Entry;
            var key = Normalize(entry.Key ?? "");
            if (entry.IsDirectory || !keys.Contains(key)) continue;
            if (!string.IsNullOrEmpty(entry.LinkTarget)) throw new InvalidDataException($"Symbolic-link entries are not supported: {key}");
            var output = SafeOutputPath(target, RelativeKey(key, relativeRoot));
            CheckReparsePoints(output);
            if (string.Equals(output, index.FilePath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The destination would overwrite the open archive. Choose a different directory.");
            if (File.Exists(output))
            {
                if (policy == ConflictPolicy.Skip || (policy == ConflictPolicy.Newer && (entry.LastModifiedTime is not DateTime incoming || File.GetLastWriteTime(output) >= incoming))) { skipped++; done++; bytesDone += entry.Size; progress.Report(new(key, bytesDone, total, done, totalFiles)); continue; }
                if (policy == ConflictPolicy.Fail) throw new IOException($"The destination file already exists: {output}");
                if (policy == ConflictPolicy.Rename) output = UniquePath(output);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var temp = Path.Combine(Path.GetDirectoryName(output)!, $".aurazip-{Guid.NewGuid():N}.tmp");
            try
            {
                var checksum = new Crc32(); long actualSize = 0;
                using (var input = reader.OpenEntryStream())
                using (var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        control.Wait(token);
                        destination.Write(buffer, 0, count);
                        checksum.Append(buffer, count); actualSize += count;
                        bytesDone += count;
                        progress.Report(new(key, bytesDone, total, done, totalFiles));
                    }
                }
                checksum.Validate(metadata.GetValueOrDefault(key), key, actualSize);
                control.Wait(token);
                CheckReparsePoints(output);
                File.Move(temp, output, policy is ConflictPolicy.Overwrite or ConflictPolicy.Newer);
                if (entry.LastModifiedTime is DateTime modified) File.SetLastWriteTime(output, modified);
                if (metadata.GetValueOrDefault(key)?.Attributes is int attributes) File.SetAttributes(output, (FileAttributes)attributes & (FileAttributes.Archive | FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.System));
                done++;
                progress.Report(new(key, bytesDone, total, done, totalFiles));
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        foreach (var dir in scope.Where(e => e.IsDirectory).OrderByDescending(e => e.Key.Length))
        {
            var output = SafeOutputPath(target, RelativeKey(dir.Key, relativeRoot));
            if (dir.Modified is DateTime modified) Directory.SetLastWriteTime(output, modified);
            if (dir.Attributes is int attributes) File.SetAttributes(output, (FileAttributes)attributes & (FileAttributes.Archive | FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.System));
        }
        return new OperationResult(done - skipped, skipped, target);
    }, token);

    private static string UniquePath(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (int i = 2; ; i++) { var candidate = Path.Combine(folder, $"{name} ({i}){extension}"); if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate; }
    }

    public Task<byte[]> ReadPreviewAsync(string path, string key, string? password, int limit = 1024 * 1024, CancellationToken token = default, int? codePage = null) => Task.Run(() =>
    {
        if (NeedsVerifiedPreview(path, password)) return ReadVerifiedPreview(path, key, password, limit, token, codePage);
        using (var signature = File.OpenRead(ArchiveFormats.FirstVolume(path)))
            if (IsCompressedStream(signature))
            {
                var index = OpenAsync(path, password, token, codePage).GetAwaiter().GetResult();
                if (index.IsSingleStream) return ReadVerifiedPreview(path, key, password, limit, token, codePage, index);
            }
        using var session = OpenSession(path, password, codePage);
        var reader = session.Reader;
        while (reader.MoveToNextEntry())
        {
            token.ThrowIfCancellationRequested();
            if (Normalize(reader.Entry.Key ?? "") != key || reader.Entry.IsDirectory) continue;
            if (reader.Entry.Size > limit) throw new InvalidOperationException($"Preview is limited to {ArchiveItem.FormatSize(limit)}. Extract the file to view it.");
            using var input = reader.OpenEntryStream();
            using var output = new MemoryStream();
            var checksum = new Crc32();
            var buffer = new byte[8192];
            int count;
            while ((count = input.Read(buffer, 0, Math.Min(buffer.Length, limit + 1 - (int)output.Length))) > 0)
            {
                token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); checksum.Append(buffer, count);
                if (output.Length > limit) throw new InvalidOperationException("File exceeds the preview size limit.");
            }
            if (!reader.Entry.IsEncrypted && reader.HasPlainCrc)
                checksum.Validate(new ArchiveItem(key, false, reader.Entry.Size, expectedCrc: unchecked((uint)reader.Entry.Crc)), key, output.Length);
            return output.ToArray();
        }
        throw new FileNotFoundException("The archive entry is no longer available.", key);
    }, token);

    public Task TestAsync(ArchiveIndex index, string? password, OperationControl control, IProgress<ArchiveProgress> progress, CancellationToken token) => Task.Run(() =>
    {
        if (NeedsVerifiedRead(index))
        {
            RunReadEngine("t", index, password, control, progress, token);
            return;
        }
        var metadata = index.Items.ToDictionary(i => i.Key, StringComparer.Ordinal);
        using var session = OpenSession(index.FilePath, password, index.CodePage);
        var reader = session.Reader;
        long bytes = 0, total = index.Items.Where(e => !e.IsDirectory).Sum(e => e.Size);
        int done = 0, count = index.Items.Count(e => !e.IsDirectory);
        var buffer = new byte[128 * 1024];
        while (reader.MoveToNextEntry())
        {
            control.Wait(token);
            if (reader.Entry.IsDirectory) continue;
            using var input = reader.OpenEntryStream();
            var checksum = new Crc32(); long actualSize = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0) { control.Wait(token); checksum.Append(buffer, read); actualSize += read; bytes += read; progress.Report(new(reader.Entry.Key ?? "", bytes, total, done, count)); }
            checksum.Validate(metadata.GetValueOrDefault(Normalize(reader.Entry.Key ?? "")), reader.Entry.Key ?? "", actualSize);
            progress.Report(new(reader.Entry.Key ?? "", bytes, total, ++done, count));
        }
    }, token);

    public Task CreateZipAsync(string destination, IReadOnlyList<string> paths, OperationControl control, IProgress<ArchiveProgress> progress, CancellationToken token, bool overwriteExisting = false) => Task.Run(() =>
    {
        var entries = SourceEntries(paths, Path.GetFullPath(destination), token).ToList();
        if (entries.Select(e => e.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count) throw new IOException("Selected items have duplicate names.");
        long total = entries.Where(e => !e.Directory).Sum(e => new FileInfo(e.Source).Length), bytes = 0;
        EnsureSpace(destination, total + 1024 * 1024);
        int done = 0, count = entries.Count(e => !e.Directory);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var output = ZipFile.Open(temporary, ZipArchiveMode.Create, System.Text.Encoding.UTF8))
            foreach (var source in entries)
            {
                control.Wait(token);
                SafeOutputPath(Path.GetTempPath(), source.Key);
                var entry = output.CreateEntry(source.Key + (source.Directory ? "/" : ""), CompressionLevel.Optimal);
                var modified = File.GetLastWriteTime(source.Source);
                if (modified.Year is >= 1980 and <= 2107) entry.LastWriteTime = modified;
                entry.ExternalAttributes = (int)File.GetAttributes(source.Source);
                if (source.Directory) continue;
                using var input = File.OpenRead(source.Source); using var stream = entry.Open();
                var buffer = new byte[128 * 1024]; int read;
                while ((read = input.Read(buffer)) > 0) { control.Wait(token); stream.Write(buffer, 0, read); bytes += read; progress.Report(new(source.Key, bytes, total, done, count)); }
                progress.Report(new(source.Key, bytes, total, ++done, count));
            }
            control.Wait(token);
            File.Move(temporary, destination, overwriteExisting);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, token);
}
