using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace AuraZip.Core;

public sealed class OfficePreviewProcess
{
    private readonly string _executable;
    private readonly TimeSpan _timeout;
    private readonly long _memoryLimit;
    public event Action<int>? ProcessStarted;
    public OfficePreviewProcess(string? executable = null, TimeSpan? timeout = null, long memoryLimit = 512L * 1024 * 1024)
    {
        _executable = Path.GetFullPath(executable ?? Environment.ProcessPath ?? throw new InvalidOperationException("The Office preview host is unavailable."));
        _timeout = timeout ?? TimeSpan.FromSeconds(30); _memoryLimit = memoryLimit;
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(2) || memoryLimit < 16L * 1024 * 1024 || memoryLimit > 2L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }
    public async Task<string> ReadAsync(byte[] data, string extension, CancellationToken token = default, string? password = null)
        => (await ReadDocumentAsync(data, extension, token, password).ConfigureAwait(false)).Text;
    public async Task<OfficePreviewDocument> ReadDocumentAsync(byte[] data, string extension, CancellationToken token = default, string? password = null)
    {
        token.ThrowIfCancellationRequested();
        if (data.Length > OfficePreview.InputLimit || password?.Length > 4096) throw new InvalidDataException("The Office document exceeds the preview limit.");
        if (!OfficePreview.Supported(extension)) throw new NotSupportedException("This Office document format cannot be previewed.");
        using var deadline = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var active = linked.Token;
        var start = new ProcessStartInfo(_executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(OfficePreviewWorker.Argument);
        using var process = Process.Start(start) ?? throw new IOException("The Office preview process could not be started.");
        OfficePreviewJob? job = null;
        void Stop()
        {
            job?.Dispose();
            try { if (!process.HasExited) process.Kill(true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        using var cancellation = active.Register(Stop);
        var errors = DrainAsync(process.StandardError.BaseStream);
        try
        {
            if (OperatingSystem.IsWindows()) job = OfficePreviewJob.Attach(process.Handle, _memoryLimit);
            ProcessStarted?.Invoke(process.Id);
            var receiving = OfficePreviewWorker.ReadFrameAsync(process.StandardOutput.BaseStream, OfficePreviewWorker.ResponseLimit, active);
            var sending = SendAsync();
            await Task.WhenAll(sending, receiving);
            await process.WaitForExitAsync(active); await errors; active.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException("The Office preview process stopped before returning a result.");
            var response = JsonSerializer.Deserialize(await receiving, OfficeWorkerJson.Default.OfficeWorkerResponse)
                ?? throw new InvalidDataException("The Office preview process returned an invalid response.");
            if (response.Error == "password") throw new OfficePasswordException(response.Retry);
            if (response.Error is not null) throw new InvalidDataException(response.Message ?? "The Office document could not be previewed.");
            if (response.Document is null || response.Document.Text.Length > 256 * 1024 || response.Document.Sections.Count > 128) throw new InvalidDataException("The Office preview response exceeds its limit.");
            return response.Document;

            async Task SendAsync()
            {
                var header = JsonSerializer.SerializeToUtf8Bytes(new OfficeWorkerRequest(extension, password, CultureInfo.CurrentCulture.Name), OfficeWorkerJson.Default.OfficeWorkerRequest);
                if (header.Length > OfficePreviewWorker.HeaderLimit) throw new InvalidDataException("The Office document exceeds the preview limit.");
                try
                {
                    await OfficePreviewWorker.WriteFrameAsync(process.StandardInput.BaseStream, header, active);
                    await OfficePreviewWorker.WriteFrameAsync(process.StandardInput.BaseStream, data, active);
                    await process.StandardInput.BaseStream.FlushAsync(active);
                }
                finally { process.StandardInput.Close(); }
            }
        }
        catch (Exception) when (active.IsCancellationRequested)
        {
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("Office preview exceeded its time limit.");
        }
        catch (EndOfStreamException ex) { throw new IOException("The Office preview process stopped before returning a result. It may have exceeded its memory limit.", ex); }
        finally
        {
            Stop(); await process.WaitForExitAsync(); await errors; job?.Dispose();
        }
    }
    private static async Task DrainAsync(Stream stream)
    {
        var buffer = new byte[4096]; while (await stream.ReadAsync(buffer) > 0) { }
    }
}
