using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AuraZip.Core;

public static partial class OfficePreview
{
    // Read only document text and cached cell values. Never activate Office, macros or external links.
    private static string ReadOpenXml(byte[] data, string extension, CancellationToken token)
    {
        using var archive = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        if (archive.Entries.Count > 4096) throw new InvalidDataException("The Office document exceeds the preview limit.");
        var result = new StringBuilder();
        long expandedXml = 0;
        const int limit = 128 * 1024;
        XDocument? Load(string key)
        {
            token.ThrowIfCancellationRequested();
            var entry = archive.GetEntry(key);
            if (entry is null) return null;
            if (archive.Entries.Count(e => e.FullName == key) > 1) throw new InvalidDataException("The Office document contains duplicate parts.");
            expandedXml += entry.Length;
            if (entry.Length > 4 * 1024 * 1024 || expandedXml > 24 * 1024 * 1024) throw new InvalidDataException("The Office document exceeds the preview limit.");
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
            return XDocument.Load(reader);
        }
        void Append(string text)
        {
            token.ThrowIfCancellationRequested();
            if (result.Length < limit) result.AppendLine(text[..Math.Min(text.Length, limit - result.Length)]);
        }
        if (extension is ".docx" or ".docm")
        {
            var document = Load("word/document.xml");
            if (document is null) throw new InvalidDataException("The Word document body is missing.");
            foreach (var paragraph in document.Descendants().Where(e => e.Name.LocalName == "p"))
            { Append(string.Concat(paragraph.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value))); if (result.Length >= limit) break; }
        }
        else if (extension is ".xlsx" or ".xlsm")
        {
            var strings = Load("xl/sharedStrings.xml")?.Descendants().Where(e => e.Name.LocalName == "si").Select(e => string.Concat(e.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value))).ToArray() ?? [];
            var workbook = Load("xl/workbook.xml"); var links = PartLinks(Load("xl/_rels/workbook.xml.rels"), "xl");
            var sheets = workbook?.Descendants().Where(e => e.Name.LocalName == "sheet")
                .Select(e => ((string?)e.Attribute("name") ?? "", links.GetValueOrDefault(e.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value ?? "", ""))).Where(e => e.Item2.Length > 0).ToArray()
                ?? archive.Entries.Where(e => System.Text.RegularExpressions.Regex.IsMatch(e.FullName, @"^xl/worksheets/sheet\d+\.xml$"))
                    .OrderBy(e => PartNumber(e.FullName, "sheet")).Select(e => (Path.GetFileNameWithoutExtension(e.FullName), e.FullName)).ToArray();
            foreach (var (title, key) in sheets.Take(32))
            {
                Append(title);
                foreach (var row in (Load(key) ?? throw new InvalidDataException("A worksheet part is missing.")).Descendants().Where(e => e.Name.LocalName == "row"))
                {
                    var cells = new List<string>();
                    foreach (var cell in row.Elements().Where(e => e.Name.LocalName == "c").Take(64))
                    {
                        int column = Column((string?)cell.Attribute("r")); while (cells.Count < column && cells.Count < 64) cells.Add("");
                        if (cells.Count >= 64) break;
                        var value = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? string.Concat(cell.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
                        var type = (string?)cell.Attribute("t");
                        if (type == "s") value = int.TryParse(value, out int index) && index >= 0 && index < strings.Length ? strings[index] : "";
                        if (type == "b") value = value == "1" ? "TRUE" : "FALSE";
                        cells.Add(value);
                    }
                    Append(string.Join('\t', cells));
                    if (result.Length >= limit) break;
                }
                if (result.Length >= limit) break;
            }
        }
        else if (extension is ".pptx" or ".pptm")
        {
            var presentation = Load("ppt/presentation.xml"); var links = PartLinks(Load("ppt/_rels/presentation.xml.rels"), "ppt");
            var slides = presentation?.Descendants().Where(e => e.Name.LocalName == "sldId")
                .Select(e => links.GetValueOrDefault(e.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value ?? "", "")).Where(e => e.Length > 0).ToArray()
                ?? archive.Entries.Where(e => System.Text.RegularExpressions.Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$"))
                    .OrderBy(e => PartNumber(e.FullName, "slide")).Select(e => e.FullName).ToArray();
            foreach (var key in slides.Take(128))
            {
                Append(Path.GetFileNameWithoutExtension(key));
                foreach (var paragraph in (Load(key) ?? throw new InvalidDataException("A slide part is missing.")).Descendants().Where(e => e.Name.LocalName == "p"))
                { Append(string.Concat(paragraph.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value))); if (result.Length >= limit) break; }
                if (result.Length >= limit) break;
            }
        }
        else throw new NotSupportedException("This Office document format cannot be previewed.");
        return result.ToString();
    }
}
