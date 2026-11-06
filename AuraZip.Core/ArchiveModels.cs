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
    public bool IsSizeKnown { get; }
    public ArchiveItem(string key, bool isDirectory, long size = 0, long packedSize = 0, DateTime? modified = null, bool isEncrypted = false, uint? expectedCrc = null, int? attributes = null, bool isLink = false, bool isSizeKnown = true)
    {
        Key = key; IsDirectory = isDirectory; Size = size; PackedSize = packedSize; Modified = modified; IsEncrypted = isEncrypted; ExpectedCrc = expectedCrc; Attributes = attributes; IsLink = isLink; IsSizeKnown = isSizeKnown;
        Name = LeafName(key); Parent = key.Contains('/') ? key[..key.LastIndexOf('/')] : "";
    }
    public override string ToString() => Name;
    public string Name { get; }
    public string Parent { get; }
    private static string LeafName(string key) { var trimmed = key.TrimEnd('/'); return trimmed[(trimmed.LastIndexOf('/') + 1)..]; }
    public string SizeText => IsDirectory ? "" : IsSizeKnown ? FormatSize(Size) : "—";
    public string PackedText => IsDirectory ? "" : FormatSize(PackedSize);
    public string ModifiedText => Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";
    public string TypeText => IsDirectory ? "文件夹" : Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext + " 文件" : "文件";
    public string Glyph => IsDirectory ? "\uE8B7" : "\uE8A5";
    public static string FormatSize(long n) => n < 1024 ? $"{n:N0} B" : n < 1048576 ? $"{n / 1024d:N1} KB" : n < 1073741824 ? $"{n / 1048576d:N1} MB" : $"{n / 1073741824d:N2} GB";
}

public sealed record ArchiveIndex(string FilePath, IReadOnlyList<ArchiveItem> Items, int? CodePage = null, bool IsSingleStream = false)
{
    // Cache by item collection so record copies with replacement Items cannot reuse
    // a stale directory index. Weak keys let closed archives release their lookup.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<ArchiveItem>, DirectoryLookup> Lookups = new();
    private DirectoryLookup Lookup => Lookups.GetValue(Items, static items => new(items));
    public string Name => Path.GetFileName(FilePath);
    public string Directory => Path.GetDirectoryName(FilePath)!;
    public IEnumerable<ArchiveItem> Children(string parent) => Lookup.Children[parent];
    public IReadOnlyList<ArchiveItem> Scope(IReadOnlyCollection<string>? selected)
    {
        if (selected is null) return Items;
        var keys = selected.ToHashSet(StringComparer.Ordinal);
        var lookup = Lookup;
        var result = new Dictionary<string, ArchiveItem>(StringComparer.Ordinal);
        foreach (var key in keys.Where(k => !HasSelectedAncestor(k, keys)))
        {
            if (lookup.ByKey.TryGetValue(key, out var item)) result.TryAdd(key, item);
            var pending = new Stack<string>(); if (key.Length > 0) pending.Push(key);
            while (pending.TryPop(out var folder))
                foreach (var child in lookup.Children[folder])
                {
                    if (!result.TryAdd(child.Key, child)) continue;
                    if (child.IsDirectory) pending.Push(child.Key);
                }
        }
        return result.Values.OrderBy(i => lookup.Order[i.Key]).ToList();
    }
    private sealed class DirectoryLookup(IReadOnlyList<ArchiveItem> items)
    {
        public ILookup<string, ArchiveItem> Children { get; } = items.ToLookup(i => i.Parent, StringComparer.Ordinal);
        public Dictionary<string, ArchiveItem> ByKey { get; } = items.ToDictionary(i => i.Key, StringComparer.Ordinal);
        public Dictionary<string, int> Order { get; } = items.Select((item, index) => (item.Key, index)).ToDictionary(i => i.Key, i => i.index, StringComparer.Ordinal);
    }
    internal static bool HasSelectedAncestor(string key, HashSet<string> selected)
    {
        int slash;
        while ((slash = key.LastIndexOf('/')) >= 0) { key = key[..slash]; if (selected.Contains(key)) return true; }
        return false;
    }
}

public enum ConflictPolicy { Fail, Overwrite, Skip, Rename, Newer }
public enum ArchiveOperationStage { Processing, Preparing, Compressing, Finalizing }
public sealed record ArchiveProgress(string FileName, long BytesDone, long TotalBytes, int FilesDone, int TotalFiles, ArchiveOperationStage Stage = ArchiveOperationStage.Processing);
public sealed record OperationResult(int FilesWritten, int FilesSkipped, string Destination);

public sealed class OperationControl : IDisposable
{
    private readonly ManualResetEventSlim _gate = new(true);
    public bool IsPaused { get; private set; }
    public void TogglePause() { IsPaused = !IsPaused; if (IsPaused) _gate.Reset(); else _gate.Set(); }
    public void Wait(CancellationToken token) { token.ThrowIfCancellationRequested(); _gate.Wait(token); }
    public void Dispose() => _gate.Dispose();
}
