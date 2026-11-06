using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AuraZip.Core;

public sealed record OfficePreviewRun(string Text, bool Bold = false, bool Italic = false, double FontSize = 14);
public sealed record OfficePreviewBlock(string Kind, IReadOnlyList<OfficePreviewRun>? Runs = null, IReadOnlyList<IReadOnlyList<string>>? Rows = null, string Alignment = "left");
public sealed record OfficePreviewSection(string Title, IReadOnlyList<OfficePreviewBlock> Blocks);
public sealed record OfficePreviewDocument(string Text, IReadOnlyList<OfficePreviewSection> Sections, bool IsTruncated = false);

public static partial class OfficePreview
{
    public static OfficePreviewDocument ReadDocument(byte[] data, string extension, CancellationToken token = default, string? password = null)
    {
        token.ThrowIfCancellationRequested(); extension = extension.ToLowerInvariant();
        if (data.Length > InputLimit) throw new InvalidDataException("The Office document exceeds the preview limit.");
        if (extension == ".xls") return ReadLegacyWorkbookDocument(data, password, token);
        if (extension is ".doc" or ".ppt")
        {
            var text = Read(data, extension, token, password);
            return TextLayout(text, extension, token);
        }
        if (!Supported(extension)) throw new NotSupportedException("This Office document format cannot be previewed.");
        if (data.AsSpan().StartsWith(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 })) data = DecryptPackage(data, password, token);
        var plain = ReadOpenXml(data, extension, token);
        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        long expanded = 0;
        XDocument? Load(string name)
        {
            token.ThrowIfCancellationRequested(); var parts = zip.Entries.Where(e => e.FullName == name).Take(2).ToArray();
            if (parts.Length > 1) throw new InvalidDataException("The Office document contains duplicate parts.");
            var entry = parts.FirstOrDefault(); if (entry is null) return null;
            expanded += entry.Length;
            if (entry.Length > 4 * 1024 * 1024 || expanded > 24 * 1024 * 1024) throw new InvalidDataException("The Office document exceeds the preview limit.");
            using var stream = entry.Open(); using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
            return XDocument.Load(reader);
        }
        var budget = new LayoutBudget(token); var sections = new List<OfficePreviewSection>();
        if (extension is ".docx" or ".docm")
        {
            var document = Load("word/document.xml")!; var body = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "body") ?? document.Root!;
            var blocks = new List<OfficePreviewBlock>();
            foreach (var element in body.Elements())
            {
                if (budget.Full) break;
                if (element.Name.LocalName == "p") blocks.Add(budget.Paragraph(element));
                else if (element.Name.LocalName == "tbl")
                {
                    budget.Block();
                    if (element.Elements().Count(e => e.Name.LocalName == "tr") > 256) budget.LimitReached = true;
                    var rows = new List<IReadOnlyList<string>>();
                    foreach (var row in element.Elements().Where(e => e.Name.LocalName == "tr").Take(256))
                    {
                        if (row.Elements().Count(e => e.Name.LocalName == "tc") > 32) budget.LimitReached = true;
                        var cells = new List<string>();
                        foreach (var cell in row.Elements().Where(e => e.Name.LocalName == "tc").Take(32))
                        { if (!budget.Cell()) break; cells.Add(budget.Take(string.Join('\n', cell.Descendants().Where(e => e.Name.LocalName == "p").Select(ParagraphText)))); }
                        rows.Add(cells); if (budget.Full) break;
                    }
                    blocks.Add(new("table", Rows: rows));
                }
            }
            sections.Add(new("", blocks));
        }
        else if (extension is ".xlsx" or ".xlsm")
        {
            var shared = Load("xl/sharedStrings.xml")?.Descendants().Where(e => e.Name.LocalName == "si").Select(e => string.Concat(e.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value))).ToArray() ?? [];
            var workbook = Load("xl/workbook.xml"); var links = PartLinks(Load("xl/_rels/workbook.xml.rels"), "xl");
            var sheets = workbook?.Descendants().Where(e => e.Name.LocalName == "sheet")
                .Select(e => ((string?)e.Attribute("name") ?? "", links.GetValueOrDefault(e.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value ?? "", ""))).Where(e => e.Item2.Length > 0).ToArray()
                ?? zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"^xl/worksheets/sheet\d+\.xml$"))
                    .OrderBy(e => PartNumber(e.FullName, "sheet")).Select(e => (Path.GetFileNameWithoutExtension(e.FullName), e.FullName)).ToArray();
            foreach (var (title, name) in sheets.Take(32))
            {
                var sheet = Load(name) ?? throw new InvalidDataException("A worksheet part is missing."); var rows = new List<IReadOnlyList<string>>();
                if (sheet.Descendants().Count(e => e.Name.LocalName == "row") > 256) budget.LimitReached = true;
                foreach (var row in sheet.Descendants().Where(e => e.Name.LocalName == "row").Take(256))
                {
                    if (row.Elements().Count(e => e.Name.LocalName == "c") > 32) budget.LimitReached = true;
                    var cells = new List<string>();
                    foreach (var cell in row.Elements().Where(e => e.Name.LocalName == "c").Take(32))
                    {
                        int column = Column((string?)cell.Attribute("r"));
                        while (column > cells.Count && cells.Count < 32 && budget.Cell()) cells.Add("");
                        if (cells.Count >= 32 || !budget.Cell()) break;
                        var value = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? string.Concat(cell.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
                        if ((string?)cell.Attribute("t") == "s") value = int.TryParse(value, out int n) && n >= 0 && n < shared.Length ? shared[n] : "";
                        if ((string?)cell.Attribute("t") == "b") value = value == "1" ? "TRUE" : "FALSE";
                        cells.Add(budget.Take(value));
                    }
                    rows.Add(cells); if (budget.Full) break;
                }
                sections.Add(new(title, [new("table", Rows: rows)])); if (budget.Full) break;
            }
            if (sheets.Length > 32) budget.LimitReached = true;
        }
        else
        {
            var presentation = Load("ppt/presentation.xml"); var links = PartLinks(Load("ppt/_rels/presentation.xml.rels"), "ppt");
            var slides = presentation?.Descendants().Where(e => e.Name.LocalName == "sldId")
                .Select(e => links.GetValueOrDefault(e.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value ?? "", "")).Where(e => e.Length > 0).ToArray()
                ?? zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$"))
                    .OrderBy(e => PartNumber(e.FullName, "slide")).Select(e => e.FullName).ToArray();
            foreach (var name in slides.Take(128))
            {
                var slide = Load(name) ?? throw new InvalidDataException("A slide part is missing."); var blocks = new List<OfficePreviewBlock>();
                foreach (var paragraph in slide.Descendants().Where(e => e.Name.LocalName == "p")) { if (budget.Full) break; blocks.Add(budget.Paragraph(paragraph)); }
                sections.Add(new(Path.GetFileNameWithoutExtension(name), blocks)); if (budget.Full) break;
            }
            if (slides.Length > 128) budget.LimitReached = true;
        }
        if (sections.Count == 0) return TextLayout(plain, extension, token);
        return new(plain, sections, budget.Truncated || budget.LimitReached || plain.Length >= 128 * 1024);
    }
    private static OfficePreviewDocument TextLayout(string text, string extension, CancellationToken token)
    {
        var sections = new List<OfficePreviewSection>(); var blocks = new List<OfficePreviewBlock>(); string title = ""; var budget = new LayoutBudget(token);
        foreach (var line in text.Split('\n'))
        {
            if (budget.Full) break;
            if (extension == ".ppt" && Regex.IsMatch(line.TrimEnd('\r'), @"^slide\d+$"))
            { if (blocks.Count > 0) sections.Add(new(title, blocks)); title = line.TrimEnd('\r'); blocks = []; continue; }
            if (line.Contains('\t')) blocks.Add(new("table", Rows: [line.TrimEnd('\r').Split('\t').Take(32).TakeWhile(_ => budget.Cell()).Select(budget.Take).ToArray()]));
            else blocks.Add(new("paragraph", [new(budget.Take(line.TrimEnd('\r')))]));
            budget.Block();
        }
        if (blocks.Count > 0 || sections.Count == 0) sections.Add(new(title, blocks));
        return new(text, sections.Take(128).ToArray(), budget.Truncated || text.Length >= 128 * 1024 || sections.Count > 128 || (extension == ".ppt" && sections.Count >= 128));
    }
    private static string ParagraphText(XElement p) => string.Concat(p.Descendants().Where(e => e.Name.LocalName is "t" or "tab" or "br").Select(e => e.Name.LocalName == "t" ? e.Value : e.Name.LocalName == "tab" ? "\t" : "\n"));
    private static int PartNumber(string name, string prefix) => int.TryParse(Path.GetFileNameWithoutExtension(name)[prefix.Length..], out int n) ? n : int.MaxValue;
    private static int Column(string? value) { int n = 0; foreach (var c in value ?? "") { if (c is < 'A' or > 'Z' || n > 32) break; n = n * 26 + c - 'A' + 1; } return Math.Max(0, n - 1); }
    private static Dictionary<string, string> PartLinks(XDocument? xml, string folder)
    {
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in xml?.Root?.Elements() ?? [])
        {
            if ((string?)element.Attribute("TargetMode") == "External") continue;
            var id = (string?)element.Attribute("Id"); var target = (string?)element.Attribute("Target");
            if (id is null || target is null || target.IndexOfAny([':', '\\', '#', '%']) >= 0) continue;
            var parts = new List<string>(); bool valid = true;
            foreach (var part in (target.StartsWith('/') ? target[1..] : folder + "/" + target).Split('/'))
            { if (part == "..") { if (parts.Count == 0) { valid = false; break; } parts.RemoveAt(parts.Count - 1); } else if (part is not ("" or ".")) parts.Add(part); }
            if (valid) links.TryAdd(id, string.Join('/', parts));
        }
        return links;
    }
    private sealed class LayoutBudget(CancellationToken token)
    {
        private int _characters, _blocks, _cells, _runs;
        public bool Truncated;
        public bool LimitReached;
        public bool Full { get { token.ThrowIfCancellationRequested(); if (_characters >= 128 * 1024 || _blocks >= 512 || _cells >= 2048 || _runs >= 4096) Truncated = true; return Truncated; } }
        public void Block() { _blocks++; }
        public bool Cell() { if (Full) return false; _cells++; return true; }
        public string Take(string value) { token.ThrowIfCancellationRequested(); int length = Math.Min(value.Length, Math.Max(0, 128 * 1024 - _characters)); if (length < value.Length) Truncated = true; _characters += length; return value[..length]; }
        public OfficePreviewBlock Paragraph(XElement p)
        {
            Block(); var runs = new List<OfficePreviewRun>();
            var style = p.Elements().FirstOrDefault(e => e.Name.LocalName == "pPr");
            var heading = style?.Elements().FirstOrDefault(e => e.Name.LocalName == "pStyle")?.Attributes().FirstOrDefault(a => a.Name.LocalName == "val")?.Value.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true;
            var alignment = style?.Elements().FirstOrDefault(e => e.Name.LocalName == "jc")?.Attributes().FirstOrDefault(a => a.Name.LocalName == "val")?.Value
                ?? style?.Attribute("algn")?.Value ?? "left";
            alignment = alignment switch { "ctr" => "center", "r" => "right", "just" or "dist" => "both", _ => alignment };
            if (alignment is not ("left" or "right" or "center" or "both")) alignment = "left";
            foreach (var r in p.Descendants().Where(e => e.Name.LocalName == "r"))
            {
                if (Full) break; _runs++;
                var props = r.Elements().FirstOrDefault(e => e.Name.LocalName == "rPr");
                bool Enabled(string key) => props?.Elements().Any(e => e.Name.LocalName == key && !e.Attributes().Any(a => a.Name.LocalName == "val" && a.Value is "false" or "0")) == true || props?.Attribute(key)?.Value == "1";
                var size = props?.Elements().FirstOrDefault(e => e.Name.LocalName == "sz")?.Attributes().FirstOrDefault(a => a.Name.LocalName == "val")?.Value;
                bool drawingSize = size is null;
                size ??= props?.Attribute("sz")?.Value;
                double font = heading ? 22 : 14;
                if (double.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out var points) && double.IsFinite(points)) font = Math.Clamp(points * (drawingSize ? 1.0 / 75 : 2.0 / 3), 10, 48);
                runs.Add(new(Take(ParagraphText(r)), Enabled("b") || heading, Enabled("i"), font));
            }
            if (runs.Count == 0) runs.Add(new(Take(ParagraphText(p)), heading, FontSize: heading ? 22 : 14));
            return new("paragraph", runs, Alignment: alignment);
        }
    }
}
