using System.Buffers.Binary;
using System.Text;
using NPOI.POIFS.FileSystem;

namespace AuraZip.Core;

public static partial class OfficePreview
{
    // Read-only implementations of the MS-DOC piece table and MS-PPT current-edit
    // persist directory. No macro, object, link or field instruction is activated.
    private static string ReadLegacyText(byte[] data, string extension, CancellationToken token)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var fs = new NPOIFSFileSystem(new MemoryStream(data));
        try
        {
            byte[] Part(string name)
            {
                if (!fs.Root.HasEntry(name)) throw new InvalidDataException("A required legacy Office stream is missing.");
                using var input = fs.Root.CreateDocumentInputStream(name); return ReadBounded(input, InputLimit, token);
            }
            if (extension == ".doc")
            {
                var word = Part("WordDocument");
                if (U16(word, 0) != 0xa5ec || U16(word, 2) < 0xc1) throw new NotSupportedException("This legacy Word document version is not supported.");
                var flags = U16(word, 10);
                if ((flags & 0x8100) != 0) throw new NotSupportedException("Encrypted legacy DOC files must be opened with their document application.");
                int shortCount = U16(word, 32); int longPosition = checked(34 + shortCount * 2);
                int longCount = U16(word, longPosition); int longStart = longPosition + 2;
                if (shortCount > 1024 || longCount is < 4 or > 1024) throw BadLegacy();
                int characterCount = CheckedInt(U32(word, longStart + 12));
                int pairs = checked(longStart + longCount * 4); int pairCount = U16(word, pairs); int pairStart = pairs + 2;
                if (pairCount < 34 || pairCount > 1024) throw BadLegacy();
                int clx = CheckedInt(U32(word, pairStart + 33 * 8)); int clxLength = CheckedInt(U32(word, pairStart + 33 * 8 + 4));
                if (clxLength == 0)
                {
                    int offset = CheckedInt(U32(word, 24)); bool unicode = (flags & 0x1000) != 0;
                    int length = checked(characterCount * (unicode ? 2 : 1)); Bounds(word, offset, length);
                    return CleanLegacy((unicode ? Encoding.Unicode : Encoding.GetEncoding(1252)).GetString(word, offset, Math.Min(length, 256 * 1024)), token);
                }
                var table = Part((flags & 0x200) == 0 ? "0Table" : "1Table"); Bounds(table, clx, clxLength);
                int position = clx, end = clx + clxLength, groups = 0;
                while (position < end && table[position] == 1)
                { token.ThrowIfCancellationRequested(); if (++groups > 1024) throw BadLegacy(); Bounds(table, position, 3); position = checked(position + 3 + U16(table, position + 1)); }
                if (position >= end || table[position] != 2) throw BadLegacy();
                int pieceLength = CheckedInt(U32(table, position + 1)); int pieceStart = position + 5;
                if (pieceLength < 4 || (pieceLength - 4) % 12 != 0 || pieceStart > end - pieceLength) throw BadLegacy();
                int count = (pieceLength - 4) / 12;
                if (count > 100000) throw BadLegacy();
                var text = new StringBuilder(); int previousEnd = 0;
                for (int i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested(); int start = CheckedInt(U32(table, pieceStart + i * 4)); int finish = CheckedInt(U32(table, pieceStart + (i + 1) * 4));
                    if (start != previousEnd || finish < start) throw BadLegacy(); previousEnd = finish;
                    if (start >= characterCount || text.Length >= 256 * 1024) continue;
                    uint fc = U32(table, pieceStart + (count + 1) * 4 + i * 8 + 2); bool compressed = (fc & 0x40000000) != 0;
                    if ((fc & 0x80000000) != 0) throw BadLegacy();
                    int offset = CheckedInt(fc & 0x3fffffff); if (compressed) { if ((offset & 1) != 0) throw BadLegacy(); offset /= 2; }
                    int characters = Math.Min(finish, characterCount) - start;
                    int length = checked(characters * (compressed ? 1 : 2)); Bounds(word, offset, length);
                    int take = Math.Min(characters, 256 * 1024 - text.Length);
                    text.Append((compressed ? Encoding.GetEncoding(1252) : Encoding.Unicode).GetString(word, offset, take * (compressed ? 1 : 2)));
                }
                if (previousEnd < characterCount) throw BadLegacy();
                return CleanLegacy(text.ToString(), token);
            }
            return ReadPowerPoint(Part("PowerPoint Document"), Part("Current User"), token);
        }
        catch (OverflowException ex) { throw new InvalidDataException("The legacy Office document contains invalid offsets.", ex); }
        finally { fs.Close(); }
    }

    private readonly record struct PptRecord(int Start, int Data, int End, ushort Type, int Instance, bool Container);
    private static PptRecord PptAt(byte[] bytes, int offset, int end)
    {
        Bounds(bytes, offset, 8); int length = CheckedInt(U32(bytes, offset + 4));
        int data = checked(offset + 8); if (data > end - length) throw BadLegacy();
        var options = U16(bytes, offset);
        return new(offset, data, data + length, U16(bytes, offset + 2), options >> 4, (options & 15) == 15);
    }
    private static string ReadPowerPoint(byte[] bytes, byte[] currentUser, CancellationToken token)
    {
        var current = PptAt(currentUser, 0, currentUser.Length);
        if (current.Type != 4086 || current.End < 28) throw BadLegacy();
        var marker = U32(currentUser, 12);
        if (marker == 0xf3d1c4df) throw new NotSupportedException("Encrypted legacy PPT files must be opened with their document application.");
        if (marker != 0xe391c05f) throw BadLegacy();
        int editOffset = CheckedInt(U32(currentUser, 16)), docRef = 0;
        var visited = new HashSet<int>(); var persist = new Dictionary<int, int>();
        while (editOffset != 0)
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(editOffset) || visited.Count > 4096) throw BadLegacy();
            var edit = PptAt(bytes, editOffset, bytes.Length);
            if (edit.Type != 4085 || edit.End - edit.Data < 28) throw BadLegacy();
            if (visited.Count == 1) { docRef = CheckedInt(U32(bytes, edit.Data + 16)); if (docRef == 0) throw BadLegacy(); }
            var directory = PptAt(bytes, CheckedInt(U32(bytes, edit.Data + 12)), bytes.Length);
            if (directory.Type is not (6001 or 6002)) throw BadLegacy();
            int position = directory.Data; var directoryIds = new HashSet<int>();
            while (position < directory.End)
            {
                uint descriptor = U32(bytes, position); position += 4;
                int first = (int)(descriptor & 0xfffff), count = (int)(descriptor >> 20);
                if (count == 0 || first == 0 || first > 0xfffff - count + 1 || position > directory.End - count * 4) throw BadLegacy();
                for (int i = 0; i < count; i++)
                { if (!directoryIds.Add(first + i)) throw BadLegacy(); persist.TryAdd(first + i, CheckedInt(U32(bytes, position + i * 4))); }
                position += count * 4; if (persist.Count > 100000) throw BadLegacy();
            }
            editOffset = CheckedInt(U32(bytes, edit.Data + 8));
        }
        if (!persist.TryGetValue(docRef, out int docOffset)) throw BadLegacy();
        var document = PptAt(bytes, docOffset, bytes.Length);
        if (document.Type != 1000 || !document.Container) throw BadLegacy();
        var slides = new List<(int Id, StringBuilder Outline)>();
        foreach (var list in Children(document).Where(r => r.Type == 4080 && r.Instance == 0))
        {
            foreach (var record in Children(list))
            {
                if (record.Type == 1011)
                { if (record.End - record.Data < 16 || slides.Count >= 10000) throw BadLegacy(); slides.Add((CheckedInt(U32(bytes, record.Data)), new())); }
                else if (slides.Count > 0 && record.Type is 4000 or 4008) AppendText(slides[^1].Outline, record);
            }
        }
        var result = new StringBuilder(); int number = 0;
        foreach (var slide in slides.Take(128))
        {
            token.ThrowIfCancellationRequested(); result.Append("slide").Append(++number).AppendLine();
            if (!persist.TryGetValue(slide.Id, out int offset)) throw BadLegacy();
            var record = PptAt(bytes, offset, bytes.Length); if (record.Type != 1006 || !record.Container) throw BadLegacy();
            var shapes = new StringBuilder(); Walk(record, shapes, 0);
            // Slide-list text contains the presentation's current outline. Text stored
            // only inside drawings (for example text boxes) is appended without repeats.
            result.Append(slide.Outline);
            var known = slide.Outline.ToString().Split('\n').ToHashSet(StringComparer.Ordinal);
            foreach (var line in shapes.ToString().Split('\n'))
                if (line.Length > 0 && !known.Contains(line)) result.AppendLine(line);
            if (result.Length >= 128 * 1024) return result.ToString()[..(128 * 1024)];
        }
        return result.ToString();

        IEnumerable<PptRecord> Children(PptRecord parent)
        {
            int position = parent.Data;
            while (position < parent.End) { token.ThrowIfCancellationRequested(); var record = PptAt(bytes, position, parent.End); yield return record; position = record.End; }
        }
        void Walk(PptRecord parent, StringBuilder text, int depth)
        {
            if (depth > 64) throw BadLegacy();
            foreach (var record in Children(parent))
            {
                if (record.Type is 4000 or 4008) AppendText(text, record);
                else if (record.Container || record.Type is 1036 or 0xf00d) Walk(record, text, depth + 1);
                if (text.Length >= 128 * 1024) return;
            }
        }
        void AppendText(StringBuilder text, PptRecord record)
        {
            if (text.Length >= 128 * 1024) return;
            int size = record.End - record.Data;
            if (record.Type == 4000 && size % 2 != 0) throw BadLegacy();
            int bytesToRead = Math.Min(size, (128 * 1024 - text.Length) * (record.Type == 4000 ? 2 : 1));
            var value = (record.Type == 4000 ? Encoding.Unicode : Encoding.GetEncoding(1252)).GetString(bytes, record.Data, bytesToRead);
            value = CleanLegacy(value, token); int remaining = 128 * 1024 - text.Length;
            text.AppendLine(value[..Math.Min(value.Length, remaining)]);
        }
    }
    private static string CleanLegacy(string text, CancellationToken token)
    {
        var result = new StringBuilder(); var fields = new Stack<bool>();
        foreach (var c in text)
        {
            token.ThrowIfCancellationRequested();
            if (c == '\x13') { if (fields.Count > 128) throw BadLegacy(); fields.Push(false); continue; }
            if (c == '\x14') { if (fields.Count > 0) { fields.Pop(); fields.Push(true); } continue; }
            if (c == '\x15') { if (fields.Count > 0) fields.Pop(); continue; }
            if (fields.Any(show => !show)) continue;
            if (c is '\r' or '\v' or '\f') result.Append('\n');
            else if (c == '\x07') result.Append('\t');
            else if (!char.IsControl(c) || c is '\t' or '\n') result.Append(c);
            if (result.Length >= 128 * 1024) break;
        }
        return result.ToString();
    }
    private static int CheckedInt(uint value) => value <= int.MaxValue ? (int)value : throw BadLegacy();
    private static void Bounds(byte[] data, int offset, int length) { if (offset < 0 || length < 0 || offset > data.Length - length) throw BadLegacy(); }
    private static ushort U16(byte[] data, int offset) { Bounds(data, offset, 2); return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2)); }
    private static uint U32(byte[] data, int offset) { Bounds(data, offset, 4); return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)); }
    private static InvalidDataException BadLegacy() => new("The legacy Office document contains invalid offsets or records.");
}
