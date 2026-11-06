using AuraZip.Core;
using NPOI.POIFS.Crypt;

internal static class OfficeLayoutChecks
{
    public static async Task Run(Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        var word = OfficePreviewChecks.Package(("word/document.xml", "<document><body><p><pPr><jc val='center'/><pStyle val='Heading1'/></pPr><r><rPr><b/><i/><sz val='28'/></rPr><t>Styled 标题</t></r></p><tbl><tr><tc><p><r><t>A1</t></r></p></tc><tc><p><r><t>B1</t></r></p></tc></tr><tr><tc><p><r><t>A2</t></r></p></tc><tc><p><r><t>&lt;script&gt;literal text&lt;/script&gt;</t></r></p></tc></tr></tbl><p><r><t>last paragraph</t></r></p></body></document>"));
        var parsed = OfficePreview.ReadDocument(word, ".docx");
        check(parsed.Sections.Count == 1 && parsed.Sections[0].Blocks.Count == 3, "Word layout preserves paragraph/table/paragraph order");
        var paragraph = parsed.Sections[0].Blocks[0]; var run = paragraph.Runs!.Single();
        check(paragraph.Alignment == "center" && run.Bold && run.Italic && Math.Abs(run.FontSize - 18.6666667) < .001, "Word layout retains alignment, bold, italic and bounded font size");
        var table = parsed.Sections[0].Blocks[1].Rows!;
        check(table.Count == 2 && table[0].SequenceEqual(new[] { "A1", "B1" }) && table[1][0] == "A2", "Word layout retains real table rows and columns");
        check(table[1][1] == "<script>literal text</script>", "Office layout transports document text as inert strings, never browser markup");
        var client = new OfficePreviewProcess(); var isolated = await client.ReadDocumentAsync(word, ".docx");
        check(isolated.Text == parsed.Text && isolated.Sections[0].Blocks[0].Runs![0].Bold, "isolated Office protocol preserves layout data and text fallback");
        var encrypted = OfficePreviewChecks.Encrypt(word, "layout password", EncryptionMode.Agile);
        check((await client.ReadDocumentAsync(encrypted, ".docx", password: "layout password")).Sections[0].Blocks[1].Rows!.Count == 2, "encrypted documents retain their layout after authenticated decryption");
        var sheets = OfficePreviewChecks.Package(
            ("xl/workbook.xml", "<workbook xmlns:r='urn:r'><sheets><sheet name='Second tab first' r:id='b'/><sheet name='First tab second' r:id='a'/><sheet name='External' r:id='external'/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id='a' Target='worksheets/sheet1.xml'/><Relationship Id='b' Target='worksheets/sheet2.xml'/><Relationship Id='external' Target='https://example.invalid/never-fetch' TargetMode='External'/></Relationships>"),
            ("xl/worksheets/sheet1.xml", "<worksheet><row><c r='A1'><v>123</v></c></row></worksheet>"),
            ("xl/worksheets/sheet2.xml", "<worksheet><row><c r='A1' t='inlineStr'><is><t>column A</t></is></c><c r='C1'><f>WEBSERVICE(unsafe-link)</f><v>42</v></c><c r='D1' t='b'><v>1</v></c></row></worksheet>"));
        var workbook = OfficePreview.ReadDocument(sheets, ".xlsx");
        check(workbook.Sections.Select(s => s.Title).SequenceEqual(new[] { "Second tab first", "First tab second" }), "workbook layout follows relationship order and real sheet names");
        check(workbook.Text.StartsWith("Second tab first") && workbook.Text.Contains("column A\t\t42\tTRUE"), "spreadsheet text fallback matches real sheet order and sparse columns");
        check(workbook.Sections[0].Blocks.Single().Rows![0].SequenceEqual(new[] { "column A", "", "42", "TRUE" }), "spreadsheet layout preserves empty columns and cached formula values");
        check(!workbook.Sections.SelectMany(s => s.Blocks).SelectMany(b => b.Rows ?? []).SelectMany(r => r).Any(v => v.Contains("WEBSERVICE")), "spreadsheet layout never evaluates or presents external formula instructions as data");
        var presentation = OfficePreviewChecks.Package(
            ("ppt/presentation.xml", "<presentation xmlns:r='urn:r'><sldIdLst><sldId r:id='second'/><sldId r:id='first'/></sldIdLst></presentation>"),
            ("ppt/_rels/presentation.xml.rels", "<Relationships><Relationship Id='first' Target='slides/slide1.xml'/><Relationship Id='second' Target='slides/slide2.xml'/></Relationships>"),
            ("ppt/slides/slide1.xml", "<slide><p><r><t>one</t></r></p></slide>"),
            ("ppt/slides/slide2.xml", "<slide><p><pPr algn='ctr'/><r><rPr b='1' i='1' sz='2400'/><t>two</t></r></p></slide>"));
        var slides = OfficePreview.ReadDocument(presentation, ".pptx");
        check(slides.Sections.Count == 2 && slides.Sections[0].Blocks[0].Runs![0].Text == "two", "presentation layout follows actual slide relationship order");
        check(slides.Text.IndexOf("two", StringComparison.Ordinal) < slides.Text.IndexOf("one", StringComparison.Ordinal), "presentation text fallback follows the same current slide order");
        check(slides.Sections[0].Blocks[0].Runs![0] is { Bold: true, Italic: true }, "presentation layout retains basic run emphasis");
        check(slides.Sections[0].Blocks[0].Alignment == "center" && slides.Sections[0].Blocks[0].Runs![0].FontSize == 32, "presentation layout interprets DrawingML alignment and hundredth-point font sizes");
        using (var oldBook = new NPOI.HSSF.UserModel.HSSFWorkbook())
        using (var oldBytes = new MemoryStream())
        {
            var first = oldBook.CreateSheet("第一页 中文");
            var firstRow = first.CreateRow(0); firstRow.CreateCell(0).SetCellValue("left"); firstRow.CreateCell(2).SetCellValue(42);
            oldBook.CreateSheet("Second worksheet").CreateRow(0).CreateCell(0).SetCellValue("second page");
            oldBook.Write(oldBytes, true);
            var oldLayout = await client.ReadDocumentAsync(oldBytes.ToArray(), ".xls");
            check(oldLayout.Sections.Select(s => s.Title).SequenceEqual(new[] { "第一页 中文", "Second worksheet" }), "legacy XLS layout preserves worksheet names and individual pages");
            check(oldLayout.Sections[0].Blocks.Single().Rows![0].SequenceEqual(new[] { "left", "", "42" }) && oldLayout.Sections[1].Blocks.Single().Rows![0][0] == "second page", "legacy XLS tables preserve sparse columns and never mix adjacent sheets");
        }
        var many = OfficePreviewChecks.Package(("word/document.xml", "<document><body>" + string.Concat(Enumerable.Repeat("<p><r><t>paragraph</t></r></p>", 1000)) + "</body></document>"));
        var limited = OfficePreview.ReadDocument(many, ".docx");
        check(limited.IsTruncated && limited.Sections[0].Blocks.Count <= 512, "Office layout caps native control creation and reports truncation");
        var emptyTables = OfficePreviewChecks.Package(("word/document.xml", "<document><body>" + string.Concat(Enumerable.Repeat("<tbl/>", 2000)) + "</body></document>"));
        var boundedTables = OfficePreview.ReadDocument(emptyTables, ".docx");
        check(boundedTables.IsTruncated && boundedTables.Sections[0].Blocks.Count <= 512, "empty tables cannot bypass the Office layout block limit");
        var oddFont = OfficePreviewChecks.Package(("word/document.xml", "<document><p><r><rPr><sz val='NaN'/></rPr><t>safe font</t></r></p></document>"));
        check(double.IsFinite(OfficePreview.ReadDocument(oddFont, ".docx").Sections[0].Blocks[0].Runs![0].FontSize), "non-finite document font sizes fall back to a safe value");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await reject(() => client.ReadDocumentAsync(word, ".docx", cancelled.Token), "Office layout generation supports cancellation");
        var legacy = await client.ReadDocumentAsync(File.ReadAllBytes("tests/Fixtures/Office/slideshow/basic_test_ppt_file.ppt"), ".ppt");
        check(legacy.Sections.Count == 2 && legacy.Sections[1].Title == "slide2", "legacy PPT layout separates the current slides for page navigation");
    }
}
