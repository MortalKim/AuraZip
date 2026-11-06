using AuraZip.Core;
using System.Buffers.Binary;
using System.Text;
using NPOI.POIFS.FileSystem;

internal static class LegacyOfficeChecks
{
    private static void U16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void U32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static byte[] Ole(params (string Name, byte[] Data)[] parts)
    {
        var fs = new NPOIFSFileSystem();
        try
        {
            foreach (var part in parts) fs.Root.CreateDocument(part.Name, new MemoryStream(part.Data));
            using var output = new MemoryStream(); fs.WriteFileSystem(output); return output.ToArray();
        }
        finally { fs.Close(); }
    }
    private static (byte[] Word, byte[] Table) Word(string first, string second)
    {
        var word = new byte[4096]; U16(word, 0, 0xa5ec); U16(word, 2, 0xc1); U16(word, 10, 0x200);
        U16(word, 32, 14); U16(word, 62, 22); U32(word, 76, (uint)(first.Length + second.Length)); U16(word, 152, 93);
        Encoding.Latin1.GetBytes(first).CopyTo(word, 1024); Encoding.Unicode.GetBytes(second).CopyTo(word, 2048);
        Encoding.Unicode.GetBytes("DELETED PRIVATE TEXT").CopyTo(word, 3072);
        var table = new byte[33]; table[0] = 2; U32(table, 1, 28);
        U32(table, 5, 0); U32(table, 9, (uint)first.Length); U32(table, 13, (uint)(first.Length + second.Length));
        U32(table, 19, 0x40000000 | 2048); U32(table, 27, 2048);
        U32(word, 418, 0); U32(word, 422, (uint)table.Length); return (word, table);
    }
    private static byte[] Record(ushort type, byte[] payload, int instance = 0, bool container = false)
    {
        var bytes = new byte[payload.Length + 8]; U16(bytes, 0, (ushort)((instance << 4) | (container ? 15 : 0))); U16(bytes, 2, type); U32(bytes, 4, (uint)payload.Length); payload.CopyTo(bytes, 8); return bytes;
    }
    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static (byte[] Document, byte[] User, int Edit, int Directory) PowerPoint()
    {
        byte[] Persist(uint id) { var payload = new byte[16]; U32(payload, 0, id); return Record(1011, payload); }
        byte[] Text(string text) => Record(4000, Encoding.Unicode.GetBytes(text));
        var list = Record(4080, Join(Persist(2), Text("first 中文\rrepeat\rrepeat"), Persist(3), Text("second current")), container: true);
        var notes = Record(4080, Text("PRIVATE NOTES TEXT"), instance: 2, container: true);
        var document = Record(1000, Join(list, notes), container: true);
        var drawing = Record(1036, Record(0xf004, Record(0xf00d, Text("box text")), container: true));
        var slide1 = Record(1006, drawing, container: true); var slide2 = Record(1006, [], container: true);
        var oldSlide = Record(1006, Text("DELETED SLIDE TEXT"), container: true);
        int directoryOffset = document.Length + slide1.Length + slide2.Length + oldSlide.Length;
        var entries = new byte[16]; U32(entries, 0, (3u << 20) | 1u); U32(entries, 4, 0); U32(entries, 8, (uint)document.Length); U32(entries, 12, (uint)(document.Length + slide1.Length));
        var directory = Record(6002, entries); int editOffset = directoryOffset + directory.Length;
        var edit = new byte[28]; U32(edit, 12, (uint)directoryOffset); U32(edit, 16, 1);
        var stream = Join(document, slide1, slide2, oldSlide, directory, Record(4085, edit));
        var user = new byte[20]; U32(user, 0, 20); U32(user, 4, 0xe391c05f); U32(user, 8, (uint)editOffset);
        return (stream, Record(4086, user), editOffset, directoryOffset);
    }
    public static async Task Run(Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        var reader = new OfficePreviewProcess();
        var fixtures = new[]
        {
            ("document/simple.doc", "simple file created with Word 97-SR2"),
            ("document/simple-table.doc", "Cell 2,3"),
            ("document/HeaderFooterUnicode.doc", "Molière"),
            ("document/test-fields.doc", "19/11/2010"),
            ("document/SimpleMacro.doc", "macro word processing document"),
            ("slideshow/basic_test_ppt_file.ppt", "This is the title on page 2"),
            ("slideshow/SampleShow.ppt", "second slide"),
            ("slideshow/with_textbox.ppt", "poor boy"),
            ("slideshow/SimpleMacro.ppt", "macro slideshow")
        };
        foreach (var (name, expected) in fixtures)
        {
            var bytes = File.ReadAllBytes(Path.Combine("tests/Fixtures/Office", name)); var snapshot = bytes.ToArray();
            var text = OfficePreview.Read(bytes, Path.GetExtension(name));
            check(text.Contains(expected), "real legacy Office sample has expected current text: " + name);
            check(bytes.SequenceEqual(snapshot), "legacy Office parsing never modifies source bytes: " + name);
            check((await reader.ReadAsync(bytes, Path.GetExtension(name))).Contains(expected), "real legacy Office document works inside the resource-limited worker: " + name);
        }
        var word = Word("first ASCII\r", "第二段\r\x13HYPERLINK secret-url\x14visible result\x15\r");
        var doc = Ole(("WordDocument", word.Word), ("1Table", word.Table));
        var result = OfficePreview.Read(doc, ".doc");
        check(result.StartsWith("first ASCII\n第二段"), "DOC piece table preserves mixed compressed and Unicode text order");
        check(result.Contains("visible result") && !result.Contains("HYPERLINK") && !result.Contains("secret-url"), "DOC preview uses cached field text and skips field instructions");
        check(!result.Contains("DELETED PRIVATE TEXT"), "DOC preview never scans unused/deleted document bytes");
        var bodyOnly = Word("body only", "PRIVATE HEADER STORY"); U32(bodyOnly.Word, 76, 9);
        check(OfficePreview.Read(Ole(("WordDocument", bodyOnly.Word), ("1Table", bodyOnly.Table)), ".doc") == "body only", "DOC preview stops at the main story's character count");
        var invalidWord = word.Word.ToArray(); U32(invalidWord, 418, uint.MaxValue);
        await reject(() => reader.ReadAsync(Ole(("WordDocument", invalidWord), ("1Table", word.Table)), ".doc"), "DOC rejects an invalid CLX offset in isolation");
        var invalidPieces = word.Table.ToArray(); U32(invalidPieces, 9, 9999999);
        await reject(() => reader.ReadAsync(Ole(("WordDocument", word.Word), ("1Table", invalidPieces)), ".doc"), "DOC rejects a non-monotonic piece table");
        var encrypted = word.Word.ToArray(); U16(encrypted, 10, 0x300);
        await reject(() => reader.ReadAsync(Ole(("WordDocument", encrypted), ("1Table", word.Table)), ".doc"), "legacy encrypted DOC reports unsupported encryption without parsing ciphertext");
        var old = word.Word.ToArray(); U16(old, 2, 0x65);
        await reject(() => reader.ReadAsync(Ole(("WordDocument", old)), ".doc"), "pre-Word97 DOC is rejected explicitly");
        var ppt = PowerPoint(); var bytesPpt = Ole(("PowerPoint Document", ppt.Document), ("Current User", ppt.User));
        var pptText = OfficePreview.Read(bytesPpt, ".ppt");
        check(pptText.Contains("first 中文") && pptText.Contains("second current") && pptText.Contains("box text"), "PPT follows current slide references and drawing text boxes");
        check(pptText.IndexOf("first 中文", StringComparison.Ordinal) < pptText.IndexOf("second current", StringComparison.Ordinal), "PPT preview preserves current presentation order");
        check(pptText.Split("repeat", StringSplitOptions.None).Length == 3, "PPT preview preserves intentional repeated outline text");
        check(!pptText.Contains("DELETED") && !pptText.Contains("PRIVATE NOTES"), "PPT preview excludes unreferenced old slides and notes");
        var oldEntries = new byte[16]; U32(oldEntries, 0, (3u << 20) | 1u); U32(oldEntries, 4, 0);
        uint slide1Offset = BinaryPrimitives.ReadUInt32LittleEndian(ppt.Document.AsSpan(ppt.Directory + 16));
        uint slide2Offset = BinaryPrimitives.ReadUInt32LittleEndian(ppt.Document.AsSpan(ppt.Directory + 20));
        U32(oldEntries, 8, slide1Offset); U32(oldEntries, 12, slide2Offset + 8);
        var oldDirectory = Record(6002, oldEntries); int oldEditOffset = ppt.Directory + oldDirectory.Length;
        var oldEdit = new byte[28]; U32(oldEdit, 12, (uint)ppt.Directory); U32(oldEdit, 16, 1);
        var recentEntries = new byte[8]; U32(recentEntries, 0, (1u << 20) | 3u); U32(recentEntries, 4, slide2Offset);
        var recentDirectory = Record(6002, recentEntries); int recentDirectoryOffset = oldEditOffset + 36; int recentEditOffset = recentDirectoryOffset + recentDirectory.Length;
        var recentEdit = new byte[28]; U32(recentEdit, 8, (uint)oldEditOffset); U32(recentEdit, 12, (uint)recentDirectoryOffset); U32(recentEdit, 16, 1);
        var history = Join(ppt.Document[..ppt.Directory], oldDirectory, Record(4085, oldEdit), recentDirectory, Record(4085, recentEdit));
        var recentUser = ppt.User.ToArray(); U32(recentUser, 16, (uint)recentEditOffset);
        var latestText = OfficePreview.Read(Ole(("PowerPoint Document", history), ("Current User", recentUser)), ".ppt");
        check(latestText.Contains("second current") && !latestText.Contains("DELETED"), "newer PPT persist entries override older revisions of the same slide");
        var invalidCurrent = history.ToArray(); U32(invalidCurrent, recentEditOffset + 24, 0);
        await reject(() => reader.ReadAsync(Ole(("PowerPoint Document", invalidCurrent), ("Current User", recentUser)), ".ppt"), "invalid current PPT document references never fall back to an older edit");
        var cycle = ppt.Document.ToArray(); U32(cycle, ppt.Edit + 16, (uint)ppt.Edit);
        await reject(() => reader.ReadAsync(Ole(("PowerPoint Document", cycle), ("Current User", ppt.User)), ".ppt"), "PPT rejects a cyclic user-edit history");
        var badDirectory = ppt.Document.ToArray(); U32(badDirectory, ppt.Edit + 20, uint.MaxValue);
        await reject(() => reader.ReadAsync(Ole(("PowerPoint Document", badDirectory), ("Current User", ppt.User)), ".ppt"), "PPT rejects an invalid persist-directory offset");
        var badRecord = ppt.Document.ToArray(); U32(badRecord, 4, uint.MaxValue);
        await reject(() => reader.ReadAsync(Ole(("PowerPoint Document", badRecord), ("Current User", ppt.User)), ".ppt"), "PPT rejects an overflowing record length");
        var badPersistRange = ppt.Document.ToArray(); U32(badPersistRange, ppt.Directory + 8, (3u << 20) | 0xfffffu);
        await reject(() => reader.ReadAsync(Ole(("PowerPoint Document", badPersistRange), ("Current User", ppt.User)), ".ppt"), "PPT rejects a persist identifier range that exceeds its 20-bit field");
        var encryptedUser = ppt.User.ToArray(); U32(encryptedUser, 12, 0xf3d1c4df);
        await reject(() => reader.ReadAsync(Ole(("PowerPoint Document", ppt.Document), ("Current User", encryptedUser)), ".ppt"), "legacy encrypted PPT is rejected before processing its encrypted records");
        check((await reader.ReadAsync(doc, ".doc")).Contains("visible result"), "a good legacy document still works after malformed legacy worker requests");
    }
}
