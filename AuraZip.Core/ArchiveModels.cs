namespace AuraZip.Core;

public sealed record ArchiveItem
{
    public string Key { get; }
    public bool IsDirectory { get; }
    public long Size { get; }
    public long PackedSize { get; }
    public DateTime? Modified { get; }
    public bool IsEncrypted { get; }
    public uint? ExpectedCrc { get; }
    public int? Attributes { get; }
    public bool IsLink { get; }
    public ArchiveItem(string key, bool isDirectory, long size = 0, long packedSize = 0, DateTime? modified = null, bool isEncrypted = false, uint? expectedCrc = null, int? attributes = null, bool isLink = false)
    { Key = key; IsDirectory = isDirectory; Size = size; PackedSize = packedSize; Modified = modified; IsEncrypted = isEncrypted; ExpectedCrc = expectedCrc; Attributes = attributes; IsLink = isLink; }
    public override string ToString() => Name;
    public string Name => Key.TrimEnd('/').Split('/').Last();
    public string Parent => Key.Contains('/') ? Key[..Key.LastIndexOf('/')] : "";
    public string SizeText => IsDirectory ? "" : FormatSize(Size);
    public string PackedText => IsDirectory ? "" : FormatSize(PackedSize);
    public string ModifiedText => Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";
    public string TypeText => IsDirectory ? "文件夹" : Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext + " 文件" : "文件";
    public string Glyph => IsDirectory ? "\uE8B7" : "\uE8A5";
    public static string FormatSize(long n) => n < 1024 ? $"{n:N0} B" : n < 1048576 ? $"{n / 1024d:N1} KB" : n < 1073741824 ? $"{n / 1048576d:N1} MB" : $"{n / 1073741824d:N2} GB";
}

public sealed record ArchiveIndex(string FilePath, IReadOnlyList<ArchiveItem> Items, int? CodePage = null)
{
    public string Name => Path.GetFileName(FilePath);
    public string Directory => Path.GetDirectoryName(FilePath)!;
    public IEnumerable<ArchiveItem> Children(string parent) => Items.Where(i => i.Parent == parent);
    public IReadOnlyList<ArchiveItem> Scope(IReadOnlyCollection<string>? selected)
    {
        if (selected is null) return Items;
        var keys = selected.ToHashSet(StringComparer.Ordinal);
        return Items.Where(i => keys.Contains(i.Key) || HasSelectedAncestor(i.Key, keys)).ToList();
    }
    internal static bool HasSelectedAncestor(string key, HashSet<string> selected)
    {
        int slash;
        while ((slash = key.LastIndexOf('/')) >= 0) { key = key[..slash]; if (selected.Contains(key)) return true; }
        return false;
    }
}

public enum ConflictPolicy { Fail, Overwrite, Skip, Rename, Newer }
public sealed record ArchiveProgress(string FileName, long BytesDone, long TotalBytes, int FilesDone, int TotalFiles);
public sealed record OperationResult(int FilesWritten, int FilesSkipped, string Destination);

public sealed class OperationControl : IDisposable
{
    private readonly ManualResetEventSlim _gate = new(true);
    public bool IsPaused { get; private set; }
    public void TogglePause() { IsPaused = !IsPaused; if (IsPaused) _gate.Reset(); else _gate.Set(); }
    public void Wait(CancellationToken token) { token.ThrowIfCancellationRequested(); _gate.Wait(token); }
    public void Dispose() => _gate.Dispose();
}
