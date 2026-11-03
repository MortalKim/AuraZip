using System.Text.Json;
using System.Text.RegularExpressions;
namespace AuraZip.Core;

public static partial class ArchiveFormats
{
    public static IReadOnlyList<string> Extensions { get; } = Load();
    private static string[] Load()
    {
        using var stream = typeof(ArchiveFormats).Assembly.GetManifestResourceStream("AuraZip.ArchiveFormats.json")!;
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.GetProperty("extensions").EnumerateArray().Select(v => v.GetString()!).ToArray();
    }
    public static bool Supported(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public static string FirstVolume(string path)
    {
        path = Path.GetFullPath(path);
        if (Regex.Match(path, @"^(.*\.)(\d{3,})$", RegexOptions.IgnoreCase) is { Success: true } number)
        {
            var padded = number.Groups[1].Value + "1".PadLeft(number.Groups[2].Length, '0');
            var standard = number.Groups[1].Value + "001";
            return File.Exists(padded) || !File.Exists(standard) ? padded : standard;
        }
        if (Regex.Match(path, @"^(.*\.part)(\d+)(\.rar)$", RegexOptions.IgnoreCase) is { Success: true } rar)
        {
            var padded = rar.Groups[1].Value + "1".PadLeft(rar.Groups[2].Length, '0') + rar.Groups[3].Value;
            var plain = rar.Groups[1].Value + "1" + rar.Groups[3].Value;
            return File.Exists(padded) || !File.Exists(plain) ? padded : plain;
        }
        if (Regex.IsMatch(path, @"\.z\d+$", RegexOptions.IgnoreCase)) return Path.ChangeExtension(path, ".zip");
        return path;
    }
    public static IReadOnlyList<string> Volumes(string path)
    {
        path = FirstVolume(path);
        if (!File.Exists(path)) throw new FileNotFoundException("The first/final archive volume is missing.", path);
        var parts = SharpCompress.Archives.ArchiveFactory.GetFileParts(path).ToList();
        List<string> Numbered(string prefix, string suffix, int width, int first)
        {
            var filePrefix = Path.GetFileName(prefix);
            var numbers = Directory.EnumerateFiles(Path.GetDirectoryName(path)!, filePrefix + "*" + suffix)
                .Select(p => Path.GetFileName(p)).Where(n => n.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase) && n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .Select(n => n.Substring(filePrefix.Length, n.Length - filePrefix.Length - suffix.Length))
                .Where(n => n.Length >= width && n.All(char.IsAsciiDigit)).Select(n => int.TryParse(n, out int i) ? i : -1).Where(i => i >= first).ToList();
            if (numbers.Count == 0) return [];
            int last = numbers.Max();
            if (last > 100000) throw new InvalidDataException("The archive has too many numbered volumes.");
            return Enumerable.Range(first, last - first + 1).Select(i => prefix + i.ToString().PadLeft(width, '0') + suffix).ToList();
        }
        if (Regex.Match(path, @"^(.*\.)(\d{3,})$", RegexOptions.IgnoreCase) is { Success: true } number)
        {
            parts = Numbered(number.Groups[1].Value, "", number.Groups[2].Length, 1);
        }
        else if (Regex.Match(path, @"^(.*\.part)(\d+)(\.rar)$", RegexOptions.IgnoreCase) is { Success: true } rar)
            parts = Numbered(rar.Groups[1].Value, rar.Groups[3].Value, rar.Groups[2].Length, 1);
        else if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.ChangeExtension(path, ".z01")))
            parts = Numbered(Path.ChangeExtension(path, ".z"), "", 2, 1).Append(path).ToList();
        else if (path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.ChangeExtension(path, ".r00")))
        {
            var numbered = Numbered(Path.ChangeExtension(path, ".r"), "", 2, 0);
            foreach (var part in numbered) if (!parts.Contains(part, StringComparer.OrdinalIgnoreCase)) parts.Add(part);
        }
        foreach (var part in parts) if (!File.Exists(part)) throw new FileNotFoundException("An archive volume is missing.", part);
        return parts.Count == 0 ? [path] : parts;
    }
    public static string BaseName(string path)
    {
        var name = Path.GetFileName(path);
        name = Regex.Replace(name, @"\.\d{3,}$", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\.part\d+(?=\.rar$)", "", RegexOptions.IgnoreCase);
        return name;
    }
}
