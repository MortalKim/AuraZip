using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Security.Cryptography;
using System.Buffers.Binary;
using NPOI.HSSF.Record.Crypto;
using NPOI.HSSF.UserModel;
using NPOI.POIFS.Crypt;
using NPOI.POIFS.FileSystem;
using NPOI.SS.UserModel;

namespace AuraZip.Core;

public sealed class OfficePasswordException(bool retry = false) : IOException(retry
    ? "The document password is incorrect or its encrypted data is damaged."
    : "A document password is required.")
{
    public bool Retry { get; } = retry;
}

public static partial class OfficePreview
{
    public const int InputLimit = 32 * 1024 * 1024;
    public static bool Supported(string extension) => extension.ToLowerInvariant() is ".docx" or ".docm" or ".xlsx" or ".xlsm" or ".pptx" or ".pptm" or ".xls" or ".doc" or ".ppt";
    public static string Read(byte[] data, string extension, CancellationToken token = default, string? password = null)
    {
        token.ThrowIfCancellationRequested(); extension = extension.ToLowerInvariant();
        if (data.Length > InputLimit) throw new InvalidDataException("The Office document exceeds the preview limit.");
        if (!Supported(extension)) throw new NotSupportedException("This Office document format cannot be previewed.");
        if (extension == ".xls") return ReadLegacyWorkbook(data, password, token);
        if (extension is ".doc" or ".ppt") return ReadLegacyText(data, extension, token);
        if (data.AsSpan().StartsWith(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 })) data = DecryptPackage(data, password, token);
        return ReadOpenXml(data, extension, token);
    }
    private static byte[] DecryptPackage(byte[] data, string? password, CancellationToken token)
    {
        // NPOI discovers agile encryption builders among already loaded assemblies.
        System.Reflection.Assembly.Load("NPOI.OOXML");
        var fs = new NPOIFSFileSystem(new MemoryStream(data));
        try
        {
            if (!fs.Root.HasEntry("EncryptedPackage") || !fs.Root.HasEntry("EncryptionInfo")) throw new InvalidDataException("The document is not an encrypted Office package.");
            using (var stream = fs.Root.CreateDocumentInputStream("EncryptedPackage"))
            {
                var size = new byte[8]; int count = stream.Read(size, 0, size.Length);
                if (count != 8 || BinaryPrimitives.ReadInt64LittleEndian(size) is < 0 or > InputLimit) throw new InvalidDataException("The Office document exceeds the preview limit.");
            }
            using (var stream = fs.Root.CreateDocumentInputStream("EncryptionInfo"))
            {
                var header = ReadBounded(stream, 128 * 1024, token);
                if (header.Length < 8) throw new InvalidDataException("The document encryption header is invalid.");
                if (header[0] == 4 && header[2] == 4)
                {
                    using var xml = new MemoryStream(header, 8, header.Length - 8);
                    using var reader = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 128 * 1024 });
                    var document = XDocument.Load(reader);
                    foreach (var attr in document.Descendants().Attributes().Where(a => a.Name.LocalName == "spinCount"))
                        if (!int.TryParse(attr.Value, out int cost) || cost < 0 || cost > 1000000) throw new InvalidDataException("The document encryption cost exceeds the preview limit.");
                }
            }
            if (password is null) throw new OfficePasswordException();
            try
            {
                token.ThrowIfCancellationRequested(); var info = new EncryptionInfo(fs); var decryptor = info.Decryptor;
                if (!decryptor.VerifyPassword(password)) throw new OfficePasswordException(true);
                if (info.EncryptionMode == EncryptionMode.Agile)
                {
                    var algorithm = info.Header.HashAlgorithm.ecmaString.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
                    if (algorithm is not ("SHA1" or "SHA256" or "SHA384" or "SHA512")) throw new InvalidDataException("The document encryption method is not supported.");
                    using var mac = IncrementalHash.CreateHMAC(new HashAlgorithmName(algorithm), decryptor.GetIntegrityHmacKey());
                    using var encrypted = fs.Root.CreateDocumentInputStream("EncryptedPackage"); var buffer = new byte[32768]; int count;
                    while ((count = encrypted.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); mac.AppendData(buffer, 0, count); }
                    if (!CryptographicOperations.FixedTimeEquals(mac.GetHashAndReset(), decryptor.GetIntegrityHmacValue()))
                        throw new InvalidDataException("The encrypted document failed its integrity check.");
                }
                token.ThrowIfCancellationRequested(); using var decrypted = decryptor.GetDataStream(fs);
                return ReadBounded(decrypted, InputLimit, token);
            }
            catch (NPOI.EncryptedDocumentException ex) { throw new InvalidDataException("The document encryption method is not supported.", ex); }
        }
        finally { fs.Close(); }
    }
    private static byte[] ReadBounded(Stream input, int limit, CancellationToken token)
    {
        using var output = new MemoryStream(); var buffer = new byte[32768]; int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        { token.ThrowIfCancellationRequested(); if (output.Length + read > limit) throw new InvalidDataException("The Office document exceeds the preview limit."); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    private static string ReadLegacyWorkbook(byte[] data, string? password, CancellationToken token)
        => ReadLegacyWorkbookDocument(data, password, token).Text;
    private static OfficePreviewDocument ReadLegacyWorkbookDocument(byte[] data, string? password, CancellationToken token)
    {
        var previous = Biff8EncryptionKey.CurrentUserPassword;
        try
        {
            Biff8EncryptionKey.CurrentUserPassword = password;
            using var workbook = new HSSFWorkbook(new MemoryStream(data)); var result = new StringBuilder();
            var sections = new List<OfficePreviewSection>(); var budget = new LayoutBudget(token);
            if (workbook.NumberOfSheets > 32) budget.LimitReached = true;
            for (int i = 0; i < Math.Min(32, workbook.NumberOfSheets); i++)
            {
                var sheet = workbook.GetSheetAt(i); result.AppendLine(sheet.SheetName);
                var rows = new List<IReadOnlyList<string>>();
                bool includeSheet = !budget.Full;
                if (includeSheet) budget.Block();
                foreach (IRow row in sheet)
                {
                    token.ThrowIfCancellationRequested(); if (row.RowNum >= 2000) { budget.LimitReached = true; break; }
                    var cells = new List<string>(); bool includeRow = includeSheet && rows.Count < 256 && !budget.Full;
                    if (rows.Count >= 256 || row.LastCellNum > 32) budget.LimitReached = true;
                    for (int column = 0; column < Math.Min(64, (int)row.LastCellNum); column++)
                    {
                        if (column > 0) result.Append('\t');
                        var cell = row.GetCell(column); var type = cell?.CellType == CellType.Formula ? cell.CachedFormulaResultType : cell?.CellType;
                        var value = cell is null ? "" : type switch
                        {
                            CellType.String => cell.StringCellValue,
                            CellType.Boolean => cell.BooleanCellValue ? "TRUE" : "FALSE",
                            CellType.Numeric => cell.NumericCellValue.ToString("G15", CultureInfo.CurrentCulture),
                            CellType.Error => FormulaError.ForInt(cell.ErrorCellValue).String,
                            _ => ""
                        };
                        if (includeRow && column < 32 && budget.Cell()) cells.Add(budget.Take(value));
                        result.Append(value.AsSpan(0, Math.Min(value.Length, Math.Max(0, 128 * 1024 - result.Length))));
                        if (result.Length >= 128 * 1024) break;
                    }
                    if (includeRow) rows.Add(cells);
                    if (result.Length >= 128 * 1024) break;
                    result.AppendLine();
                }
                if (includeSheet) sections.Add(new(sheet.SheetName, [new("table", Rows: rows)]));
                if (result.Length >= 128 * 1024) break;
            }
            return new(result.ToString(), sections, budget.Truncated || budget.LimitReached || result.Length >= 128 * 1024);
        }
        catch (NPOI.EncryptedDocumentException) { throw new OfficePasswordException(password is not null); }
        finally { Biff8EncryptionKey.CurrentUserPassword = previous; }
    }
}
