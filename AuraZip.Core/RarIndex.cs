using System.Globalization;

namespace AuraZip.Core;

public sealed partial class ArchiveService
{
    private ArchiveIndex ReadRarIndex(string path, string? password, int? codePage, CancellationToken token)
    {
        var arguments = new List<string> { "l", "-slt", "-sccUTF-8", "-p" + (password ?? "-") };
        if (codePage is int cp) arguments.Add("-mcp=" + cp);
        arguments.Add("--"); arguments.Add(path);
        using var process = StartReadProcess(arguments);
        using var registration = token.Register(() => KillReadProcess(process));
        var errors = process.StandardError.ReadToEndAsync();
        var items = new Dictionary<string, ArchiveItem>(StringComparer.Ordinal);
        var record = new Dictionary<string, string>(StringComparer.Ordinal); bool entries = false;
        void AddRecord()
        {
            if (!record.TryGetValue("Path", out var name)) { record.Clear(); return; }
            var key = Normalize(name); if (key.Length == 0) { record.Clear(); return; }
            if (items.TryGetValue(key, out var existing) && !existing.IsDirectory) throw new InvalidDataException("The archive contains duplicate file entries.");
            var attributes = record.GetValueOrDefault("Attributes", "");
            bool directory = record.GetValueOrDefault("Folder") == "+" || attributes.StartsWith('D');
            bool encrypted = record.GetValueOrDefault("Encrypted") == "+";
            bool link = !string.IsNullOrEmpty(record.GetValueOrDefault("Symbolic Link")) || !string.IsNullOrEmpty(record.GetValueOrDefault("Hard Link")) || !string.IsNullOrEmpty(record.GetValueOrDefault("Copy Link"))
                || attributes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(a => a.StartsWith('l'));
            long size = long.TryParse(record.GetValueOrDefault("Size"), NumberStyles.None, CultureInfo.InvariantCulture, out var length) ? length : 0;
            long packed = long.TryParse(record.GetValueOrDefault("Packed Size"), NumberStyles.None, CultureInfo.InvariantCulture, out var compressed) ? compressed : 0;
            DateTime? modified = DateTime.TryParse(record.GetValueOrDefault("Modified"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
            uint? crc = !encrypted && uint.TryParse(record.GetValueOrDefault("CRC"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var checksum) ? checksum : null;
            FileAttributes fileAttributes = directory ? FileAttributes.Directory : FileAttributes.Normal;
            if (attributes.Contains('R')) fileAttributes |= FileAttributes.ReadOnly;
            if (attributes.Contains('H')) fileAttributes |= FileAttributes.Hidden;
            if (attributes.Contains('S')) fileAttributes |= FileAttributes.System;
            var pieces = key.Split('/');
            for (int i = 1; i < pieces.Length; i++) { var parent = string.Join('/', pieces.Take(i)); items.TryAdd(parent, new(parent, true)); }
            items[key] = new(key, directory, size, packed, modified, encrypted, crc, (int)fileAttributes, link);
            record.Clear();
        }
        try
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                token.ThrowIfCancellationRequested();
                if (!entries) { if (line == "----------") entries = true; continue; }
                if (line.Length == 0) { AddRecord(); continue; }
                int separator = line.IndexOf(" = ", StringComparison.Ordinal);
                if (separator > 0) record[line[..separator]] = line[(separator + 3)..];
                if (items.Count > 1000000 || line.Length > 32768) throw new InvalidDataException("The archive exceeds the supported listing limit.");
            }
            AddRecord(); process.WaitForExit(); var error = errors.GetAwaiter().GetResult(); token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) ThrowReadError(error, process.ExitCode);
            if (!entries) throw new InvalidDataException("The archive engine did not return an entry list.");
            return new(Path.GetFullPath(path), items.Values.ToList(), codePage);
        }
        catch { KillReadProcess(process); process.WaitForExit(); errors.GetAwaiter().GetResult(); throw; }
    }
}
