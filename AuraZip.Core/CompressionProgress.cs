using System.Text.RegularExpressions;

namespace AuraZip.Core;

internal sealed class CompressionProgress(Action<ArchiveProgress> report) : IProgress<ArchiveProgress>
{
    public void Report(ArchiveProgress value) => report(value);
}

// Consume each engine record once; stdout records may span ReadAsync chunks.
internal sealed class CompressionProgressParser(IProgress<ArchiveProgress> progress, long total, int files, IReadOnlyDictionary<string, long>? sizes)
{
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private string _file = "";
    private long _bytes, _completedBytes;
    private int _done;

    public void Read(string record)
    {
        var line = record.Trim();
        if (line.StartsWith("+ ", StringComparison.Ordinal) || line.StartsWith("- ", StringComparison.Ordinal))
        {
            var name = line[2..].Replace('\\', '/');
            if (sizes is not null && sizes.ContainsKey(name) && _seen.Add(name))
            {
                if (sizes.TryGetValue(_file, out var previous)) _completedBytes += previous;
                _done = Math.Max(_done, _seen.Count - 1);
                _bytes = Math.Max(_bytes, _completedBytes);
                _file = name;
            }
            else if (sizes is null) _file = name;
            else return;
        }
        else
        {
            var match = Regex.Match(line, @"^(\d{1,3})%(?:\s+(\d+))?");
            if (!match.Success) return;
            var percent = Math.Min(int.Parse(match.Groups[1].Value), 100);
            _bytes = Math.Max(_bytes, (long)((double)total * percent / 100));
            if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var done)) _done = Math.Max(_done, Math.Min(done, files));
        }
        progress.Report(new(_file, Math.Min(_bytes, total), total, _done, files));
    }

    public void Complete() => progress.Report(new(_file, total, total, files, files));
}
