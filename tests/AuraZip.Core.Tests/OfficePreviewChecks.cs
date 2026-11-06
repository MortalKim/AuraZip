using AuraZip.Core;
using System.IO.Compression;
using System.Text;
using NPOI.HSSF.Record.Crypto;
using NPOI.HSSF.UserModel;
using NPOI.POIFS.Crypt;
using NPOI.POIFS.FileSystem;

internal static class OfficePreviewChecks
{
    public static byte[] Package(params (string Name, string Content)[] parts)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var part in parts) { using var writer = new StreamWriter(zip.CreateEntry(part.Name).Open(), new UTF8Encoding(false)); writer.Write(part.Content); }
        return output.ToArray();
    }
    public static byte[] Encrypt(byte[] package, string password, EncryptionMode mode)
    {
        System.Reflection.Assembly.Load("NPOI.OOXML");
        var fs = new NPOIFSFileSystem();
        try
        {
            var info = new EncryptionInfo(mode); info.Encryptor.ConfirmPassword(password);
            using (var encrypted = info.Encryptor.GetDataStream(fs)) encrypted.Write(package, 0, package.Length);
            using var output = new MemoryStream(); fs.WriteFileSystem(output); return output.ToArray();
        }
        finally { fs.Close(); }
    }
    public static async Task Run(string root, Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        var directory = Path.Combine(root, "office-preview"); Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "layout.docx"), Package(("word/document.xml", "<document><body><p><pPr><pStyle val='Heading1'/></pPr><r><rPr><b/><i/></rPr><t>Layout heading</t></r></p><tbl><tr><tc><p><r><t>Cell A1</t></r></p></tc><tc><p><r><t>Cell B1</t></r></p></tc></tr><tr><tc><p><r><t>Cell A2</t></r></p></tc><tc><p><r><t>Cell B2</t></r></p></tc></tr></tbl></body></document>")));
        var documents = new[]
        {
            (Extension: ".docx", Expected: "中文 document", Data: Package(("word/document.xml", "<document><p><r><t>中文 document</t></r></p></document>"))),
            (Extension: ".xlsx", Expected: "中文 cell", Data: Package(("xl/worksheets/sheet1.xml", "<worksheet><row><c t='inlineStr'><is><t>中文 cell</t></is></c><c><f>1+1</f><v>2</v></c></row></worksheet>"))),
            (Extension: ".pptx", Expected: "中文 slide", Data: Package(("ppt/slides/slide1.xml", "<slide><p><r><t>中文 slide</t></r></p></slide>")))
        };
        foreach (var document in documents)
        {
            check(OfficePreview.Read(document.Data, document.Extension.ToUpperInvariant()).Contains(document.Expected), "Office preview handles case-insensitive extensions: " + document.Extension);
            foreach (var mode in new[] { EncryptionMode.Agile, EncryptionMode.Standard })
            foreach (var password in new[] { "ASCII-password", "中文 password" })
            {
                var modeName = mode == EncryptionMode.Agile ? "Agile" : "Standard";
                var encrypted = Encrypt(document.Data, password, mode);
                File.WriteAllBytes(Path.Combine(directory, document.Extension[1..] + "-" + modeName + "-" + (password[0] == '中') + ".bin"), encrypted);
                var snapshot = encrypted.ToArray();
                try { OfficePreview.Read(encrypted, document.Extension); check(false, "encrypted document requires a password"); }
                catch (OfficePasswordException ex) { check(!ex.Retry, "encrypted Office document requests its own password: " + document.Extension + " " + mode); }
                try { OfficePreview.Read(encrypted, document.Extension, password: "wrong"); check(false, "wrong document password must fail"); }
                catch (OfficePasswordException ex) { check(ex.Retry, "incorrect Office document password can be retried: " + document.Extension + " " + mode); }
                check(OfficePreview.Read(encrypted, document.Extension, password: password).Contains(document.Expected), "encrypted Office document previews correctly: " + document.Extension + " " + mode + " Unicode=" + (password[0] == '中'));
                check(encrypted.SequenceEqual(snapshot), "Office decryption never modifies the input bytes: " + document.Extension + " " + mode);
            }
        }
        foreach (var password in new string?[] { null, "xls-password", "中文密码" })
        {
            var previous = Biff8EncryptionKey.CurrentUserPassword; byte[] bytes;
            try
            {
                Biff8EncryptionKey.CurrentUserPassword = password;
                using var workbook = new HSSFWorkbook(); var sheet = workbook.CreateSheet("工作表"); var row = sheet.CreateRow(0);
                row.CreateCell(0).SetCellValue("旧版 Excel"); row.CreateCell(1).SetCellValue(123.5); row.CreateCell(3).SetCellValue(true);
                var formula = row.CreateCell(4); formula.SetCellFormula("1+1"); formula.SetCellValue(2);
                workbook.CreateSheet("第二页").CreateRow(0).CreateCell(0).SetCellValue("Second worksheet page");
                using var output = new MemoryStream(); workbook.Write(output, true); bytes = output.ToArray();
            }
            finally { Biff8EncryptionKey.CurrentUserPassword = previous; }
            File.WriteAllBytes(Path.Combine(directory, password is null ? "plain.xls" : password[0] == '中' ? "unicode.xls" : "encrypted.xls"), bytes);
            var text = OfficePreview.Read(bytes, ".xls", password: password);
            check(text.Contains("工作表") && text.Contains("旧版 Excel") && text.Contains("123.5\t\tTRUE\t2"), "legacy XLS preserves cached values and empty columns: encrypted=" + (password is not null));
            check(!text.Contains("1+1"), "legacy XLS previews cached formula results without evaluation");
            if (password is not null) await reject(() => Task.FromResult(OfficePreview.Read(bytes, ".xls", password: "wrong")), "encrypted legacy XLS rejects an incorrect document password");
            check(Biff8EncryptionKey.CurrentUserPassword == previous, "legacy XLS reading restores the thread-local password");
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await reject(() => Task.FromResult(OfficePreview.Read(documents[0].Data, ".docx", cancelled.Token)), "Office parsing observes cancellation before reading");
        await reject(() => Task.FromResult(OfficePreview.Read(new byte[OfficePreview.InputLimit + 1], ".docx")), "Office parsing rejects oversized input");
        await reject(() => Task.FromResult(OfficePreview.Read(Package(("word/document.xml", "<!DOCTYPE a [<!ENTITY b SYSTEM 'file:///C:/Windows/win.ini'>]><document><p><t>&b;</t></p></document>")), ".docx")), "Office parsing rejects external XML entities");
        await reject(() => Task.FromResult(OfficePreview.Read(Package(("word/document.xml", "<document/>"), ("word/document.xml", "<document/>")), ".docx")), "Office parsing rejects duplicate main document parts");
        var agile = Encrypt(documents[0].Data, "integrity", EncryptionMode.Agile);
        static byte[] ChangePart(byte[] bytes, string name, Func<byte[], byte[]> change)
        {
            var fs = new NPOIFSFileSystem(new MemoryStream(bytes));
            try
            {
                byte[] original;
                using (var input = fs.Root.CreateDocumentInputStream(name)) { using var copy = new MemoryStream(); input.CopyTo(copy); original = copy.ToArray(); }
                fs.Root.GetEntry(name).Delete(); fs.Root.CreateDocument(name, new MemoryStream(change(original)));
                using var output = new MemoryStream(); fs.WriteFileSystem(output); return output.ToArray();
            }
            finally { fs.Close(); }
        }
        var tampered = ChangePart(agile, "EncryptedPackage", bytes => { bytes[^1] ^= 1; return bytes; });
        File.WriteAllBytes(Path.Combine(directory, "corrupt.docx"), tampered);
        try { OfficePreview.Read(tampered, ".docx", password: "integrity"); check(false, "tampered document must fail integrity validation"); }
        catch (InvalidDataException ex) { check(ex.Message.Contains("integrity"), "agile Office authentication rejects ciphertext tampering before parsing"); }
        var oversized = ChangePart(agile, "EncryptedPackage", bytes => { System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes, long.MaxValue); return bytes; });
        await reject(() => Task.FromResult(OfficePreview.Read(oversized, ".docx", password: "integrity")), "an encrypted size header cannot force an oversized preview allocation");
        var expensive = ChangePart(agile, "EncryptionInfo", bytes => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("spinCount=\"100000\"", "spinCount=\"2000000000\"")));
        await reject(() => Task.FromResult(OfficePreview.Read(expensive, ".docx", password: "integrity")), "Office preview refuses an excessive password derivation cost");
    }
}
