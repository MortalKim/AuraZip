using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AuraZip.Core;

public sealed record FileHashes(string SHA256, string MD5);

public sealed partial class ArchiveService
{
    public static async Task<FileHashes> ComputeHashesAsync(string path, CancellationToken token = default, OperationControl? control = null)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        return await ComputeHashesAsync(stream, token, control);
    }
    private static async Task<FileHashes> ComputeHashesAsync(Stream stream, CancellationToken token, OperationControl? control)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[128 * 1024]; int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0) { control?.Wait(token); sha.AppendData(buffer, 0, read); md5.AppendData(buffer, 0, read); }
        return new(Convert.ToHexString(sha.GetHashAndReset()), Convert.ToHexString(md5.GetHashAndReset()));
    }

    private async Task EditSevenZipAsync(ArchiveIndex index, IReadOnlyList<string>? additions, IReadOnlyCollection<string>? remove,
        string? renameKey, string? renameTo, OperationControl control, IProgress<ArchiveProgress> progress, CancellationToken token,
        string? password, string additionRoot)
    {
        if (ArchiveFormats.Volumes(index.FilePath).Count != 1) throw new NotSupportedException("Split archives cannot be modified.");
        if (index.Items.Any(e => e.IsLink)) throw new NotSupportedException("Archives containing symbolic links cannot be modified safely.");
        if (index.Items.Any(e => e.IsEncrypted) && string.IsNullOrEmpty(password)) throw new IOException("A password is required to update this archive.");
        var originalHash = await ComputeHashesAsync(index.FilePath, token);
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Path, "content"); Directory.CreateDirectory(content);
        await ExtractAsync(index, content, null, ConflictPolicy.Fail, password, control, progress, token);
        await Task.Run(() =>
        {
            if (renameKey is not null)
            {
                if (string.IsNullOrWhiteSpace(renameTo) || renameTo.IndexOfAny(['/', '\\']) >= 0) throw new ArgumentException("Enter one file or folder name.");
                var parent = renameKey.Contains('/') ? renameKey[..renameKey.LastIndexOf('/')] + "/" : "";
                var target = SafeOutputPath(content, parent + renameTo);
                if (File.Exists(target) || Directory.Exists(target)) throw new IOException("An entry with that name already exists.");
                var source = SafeOutputPath(content, renameKey);
                control.Wait(token);
                if (Directory.Exists(source)) Directory.Move(source, target); else File.Move(source, target);
            }
            foreach (var key in remove ?? Array.Empty<string>())
            {
                control.Wait(token);
                var path = SafeOutputPath(content, key);
                if (Directory.Exists(path)) ClearDirectory(path);
                else if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
            }
            var incoming = additions is null ? [] : SourceEntries(additions, index.FilePath, token).ToList();
            if (incoming.Select(e => e.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != incoming.Count) throw new IOException("Selected items contain duplicate names.");
            foreach (var entry in incoming)
            {
                control.Wait(token);
                var target = SafeOutputPath(content, additionRoot.Length == 0 ? entry.Key : additionRoot + "/" + entry.Key);
                if (entry.Directory)
                {
                    if (File.Exists(target)) throw new IOException("A file and folder have the same name.");
                    Directory.CreateDirectory(target);
                }
                else
                {
                    if (Directory.Exists(target)) throw new IOException("A file and folder have the same name.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
                    using var source = File.OpenRead(entry.Source); using var output = File.Create(target);
                    var buffer = new byte[128 * 1024]; int read;
                    while ((read = source.Read(buffer)) > 0) { control.Wait(token); output.Write(buffer, 0, read); }
                    output.Close();
                    File.SetLastWriteTime(target, File.GetLastWriteTime(entry.Source));
                    File.SetAttributes(target, File.GetAttributes(entry.Source) & (FileAttributes.Archive | FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.System));
                }
            }
        }, token);
        var rebuilt = Path.Combine(workspace.Path, "updated.7z");
        await CreateArchiveAsync(rebuilt, Directory.GetFileSystemEntries(content), new("7z", Password: password), control, progress, token);
        await TestAsync(await OpenAsync(rebuilt, password, token), password, control, progress, token);
        var staged = index.FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = File.OpenRead(rebuilt))
            await using (var output = File.Create(staged)) { await input.CopyToAsync(output, token); }
            control.Wait(token);
            await ReplaceArchiveAsync(staged, index.FilePath, originalHash, token);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    private static void ClearDirectory(string path)
    {
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        foreach (var folder in Directory.GetDirectories(path, "*", SearchOption.AllDirectories)) File.SetAttributes(folder, FileAttributes.Directory);
        File.SetAttributes(path, FileAttributes.Directory);
        Directory.Delete(path, true);
    }

    private static (long Offset, byte[] Tail) ZipFooter(string path)
    {
        using var stream = File.OpenRead(path);
        var tail = new byte[(int)Math.Min(stream.Length, 22 + ushort.MaxValue)];
        stream.Position = stream.Length - tail.Length; stream.ReadExactly(tail);
        for (int i = tail.Length - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50 && i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) == tail.Length)
                return (stream.Length - tail.Length + i, tail[i..]);
        throw new InvalidDataException("The ZIP end record was not found.");
    }
    public static string ReadZipComment(string path)
    {
        var bytes = ReadZipCommentBytes(path);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(437).GetString(bytes); }
    }
    private static byte[] ReadZipCommentBytes(string path) => ZipFooter(path).Tail[22..];
    private static void WriteZipComment(string path, string comment)
        => WriteZipComment(path, Encoding.UTF8.GetBytes(comment));
    private static void WriteZipComment(string path, byte[] bytes)
    {
        if (bytes.Length > ushort.MaxValue) throw new ArgumentException("ZIP comments must not exceed 65535 UTF-8 bytes.");
        var footer = ZipFooter(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Position = footer.Offset + 20;
        Span<byte> length = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)bytes.Length);
        stream.Write(length); stream.Write(bytes); stream.SetLength(stream.Position);
    }
    public static async Task SetZipCommentAsync(string path, string comment, CancellationToken token = default)
    {
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || ArchiveFormats.Volumes(path).Count != 1) throw new NotSupportedException("Comments can only be edited in single-volume ZIP archives.");
        var original = await ComputeHashesAsync(path, token);
        var staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = File.OpenRead(path))
            await using (var output = File.Create(staged)) { await input.CopyToAsync(output, token); }
            WriteZipComment(staged, comment);
            token.ThrowIfCancellationRequested();
            await ReplaceArchiveAsync(staged, path, original, token);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    private static async Task ReplaceArchiveAsync(string staged, string destination, FileHashes expected, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (await ComputeHashesAsync(destination, token) != expected) throw new IOException("The archive changed outside AuraZip. Reopen it before saving.");
            try { File.Replace(staged, destination, null); return; }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException
                && (ex.HResult & 0xffff) is 5 or 32 or 33 or 1175 && File.Exists(staged) && File.Exists(destination))
            {
                // Filesystem filters can briefly retain a newly written archive. Retry
                // only while both files exist, checking the original hash each time.
                await Task.Delay(40 << attempt, token);
            }
        }
    }
}
