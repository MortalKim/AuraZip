using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AuraZip.Core;

public static class OfficePreview
{
    // Read only document text and cached cell values. Never activate Office, macros or external links.
    public static string Read(byte[] data, string extension, CancellationToken token = default)
    {
        using var archive = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        var result = new StringBuilder();
        const int limit = 128 * 1024;
        XDocument? Load(string key)
        {
            token.ThrowIfCancellationRequested();
            var entry = archive.GetEntry(key);
            if (entry is null) return null;
            if (entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("The Office document exceeds the preview limit.");
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
            foreach (var sheet in archive.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal)).OrderBy(e => e.FullName).Take(32))
            {
                Append(Path.GetFileNameWithoutExtension(sheet.FullName));
                foreach (var row in Load(sheet.FullName)!.Descendants().Where(e => e.Name.LocalName == "row"))
                {
                    Append(string.Join("\t", row.Elements().Where(e => e.Name.LocalName == "c").Select(cell =>
                    {
                        var value = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? string.Concat(cell.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
                        return (string?)cell.Attribute("t") == "s" && int.TryParse(value, out int index) && index >= 0 && index < strings.Length ? strings[index] : value;
                    })));
                    if (result.Length >= limit) break;
                }
                if (result.Length >= limit) break;
            }
        }
        else if (extension is ".pptx" or ".pptm")
        {
            var slides = archive.Entries.Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal)).OrderBy(e => int.TryParse(Path.GetFileNameWithoutExtension(e.FullName)[5..], out int n) ? n : int.MaxValue).Take(128);
            foreach (var slide in slides)
            {
                Append(Path.GetFileNameWithoutExtension(slide.FullName));
                foreach (var paragraph in Load(slide.FullName)!.Descendants().Where(e => e.Name.LocalName == "p"))
                { Append(string.Concat(paragraph.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value))); if (result.Length >= limit) break; }
                if (result.Length >= limit) break;
            }
        }
        else throw new NotSupportedException("This Office document format cannot be previewed.");
        return result.ToString();
    }
}
