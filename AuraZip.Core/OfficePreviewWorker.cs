using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuraZip.Core;

internal sealed record OfficeWorkerRequest(string Extension, string? Password, string Culture);
internal sealed record OfficeWorkerResponse(OfficePreviewDocument? Document, string? Error, string? Message, bool Retry = false);
[JsonSerializable(typeof(OfficeWorkerRequest))]
[JsonSerializable(typeof(OfficeWorkerResponse))]
internal partial class OfficeWorkerJson : JsonSerializerContext;

public static class OfficePreviewWorker
{
    public const string Argument = "--office-preview-worker";
    internal const int HeaderLimit = 16 * 1024, ResponseLimit = 2 * 1024 * 1024;

    // Framed input avoids temporary plaintext files. Passwords are never arguments.
    public static async Task<int> RunAsync(Stream input, Stream output)
    {
        OfficeWorkerResponse response;
        try
        {
            var header = await ReadFrameAsync(input, HeaderLimit, default);
            var request = JsonSerializer.Deserialize(header, OfficeWorkerJson.Default.OfficeWorkerRequest)
                ?? throw new InvalidDataException("Invalid Office preview request.");
            if (request.Password?.Length > 4096 || !OfficePreview.Supported(request.Extension)) throw new InvalidDataException("Invalid Office preview request.");
            var culture = CultureInfo.GetCultureInfo(request.Culture);
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
            var data = await ReadFrameAsync(input, OfficePreview.InputLimit, default);
            var document = OfficePreview.ReadDocument(data, request.Extension, password: request.Password);
            response = new(document, null, null);
        }
        catch (OfficePasswordException ex) { response = new(null, "password", ex.Message, ex.Retry); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { response = new(null, "document", ex.Message.Length <= 4096 ? ex.Message : "The Office document could not be previewed."); }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, OfficeWorkerJson.Default.OfficeWorkerResponse);
        await WriteFrameAsync(output, bytes, default); await output.FlushAsync();
        return 0;
    }

    internal static async Task<byte[]> ReadFrameAsync(Stream input, int limit, CancellationToken token)
    {
        var length = new byte[4]; await input.ReadExactlyAsync(length, token);
        int size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size < 0 || size > limit) throw new InvalidDataException("The Office preview response exceeds its limit.");
        var bytes = new byte[size]; await input.ReadExactlyAsync(bytes, token); return bytes;
    }
    internal static async Task WriteFrameAsync(Stream output, byte[] data, CancellationToken token)
    {
        var length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, data.Length);
        await output.WriteAsync(length, token); await output.WriteAsync(data, token);
    }
}
