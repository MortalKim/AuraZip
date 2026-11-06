using AuraZip.Core;
using System.Diagnostics;
using System.Buffers.Binary;
using NPOI.POIFS.Crypt;

internal static class OfficeProcessChecks
{
    public static async Task Run(Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        var document = OfficePreviewChecks.Package(("word/document.xml", "<document><p><t>isolated 中文 preview</t></p></document>"));
        var processes = new List<int>(); var reader = new OfficePreviewProcess(); reader.ProcessStarted += processes.Add;
        var text = await reader.ReadAsync(document, ".docx");
        check(text.Contains("isolated 中文 preview"), "isolated Office worker returns real document text");
        check(processes.Count == 1 && processes[0] != Environment.ProcessId && Gone(processes[0]), "Office parsing runs in a separate process which exits after success");
        var encrypted = OfficePreviewChecks.Encrypt(document, "worker password 中文", EncryptionMode.Agile);
        try { await reader.ReadAsync(encrypted, ".docx"); check(false, "isolated encrypted document needs a password"); }
        catch (OfficePasswordException ex) { check(!ex.Retry, "Office worker transports a distinct document-password requirement"); }
        try { await reader.ReadAsync(encrypted, ".docx", password: "wrong"); check(false, "isolated wrong password must fail"); }
        catch (OfficePasswordException ex) { check(ex.Retry, "Office worker transports incorrect-password retry state"); }
        check((await reader.ReadAsync(encrypted, ".docx", password: "worker password 中文")).Contains("isolated 中文 preview"), "Office worker decrypts with a password sent through its private pipe");
        check(processes.All(Gone), "all password-attempt workers exit without lingering processes");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); int before = processes.Count;
            await reject(() => reader.ReadAsync(document, ".docx", cancelled.Token), "cancelled Office requests do not start a worker");
            check(processes.Count == before, "pre-cancelled Office requests leave no process behind");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var cancellable = new OfficePreviewProcess(); int id = 0;
            cancellable.ProcessStarted += started => { id = started; cancellation.Cancel(); };
            var watch = Stopwatch.StartNew();
            try { await cancellable.ReadAsync(document, ".docx", cancellation.Token); check(false, "an active Office preview must cancel"); }
            catch (OperationCanceledException) { check(true, "cancellation interrupts an active isolated Office request"); }
            check(id > 0 && Gone(id) && watch.Elapsed < TimeSpan.FromSeconds(5), "cancelled Office parsing terminates its owned worker promptly");
        }
        var deadline = new OfficePreviewProcess(timeout: TimeSpan.FromMilliseconds(1)); int timedOutId = 0;
        deadline.ProcessStarted += id => timedOutId = id;
        try { await deadline.ReadAsync(encrypted, ".docx", password: "worker password 中文"); check(false, "Office deadline must expire"); }
        catch (TimeoutException) { check(true, "Office parsing enforces its overall time limit"); }
        check(timedOutId == 0 || Gone(timedOutId), "timed-out Office parsing leaves no worker behind");
        var lowMemory = new OfficePreviewProcess(memoryLimit: 16L * 1024 * 1024); int limitedId = 0;
        lowMemory.ProcessStarted += id => limitedId = id;
        await reject(() => lowMemory.ReadAsync(encrypted, ".docx", password: "worker password 中文"), "Office parsing enforces its worker memory limit");
        check(limitedId > 0 && Gone(limitedId), "memory-limited Office failure does not terminate or block the parent");
        await reject(() => reader.ReadAsync([1,2,3], ".docx"), "malformed Office input is isolated and reported");
        check(processes.All(Gone), "malformed Office parsing cleans up the worker");

        // Exercise the real worker protocol directly: it must reject a frame before
        // allocating the advertised oversized document/header.
        foreach (var length in new[] { -1, 16385, int.MaxValue })
        {
            using var input = new MemoryStream(); var bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, length); input.Write(bytes); input.Position = 0;
            using var output = new MemoryStream(); await OfficePreviewWorker.RunAsync(input, output);
            check(output.Length is > 4 and < 8192, "Office protocol rejects an invalid header length without allocating it: " + length);
        }
        using (var input = new MemoryStream([1, 2]))
        using (var output = new MemoryStream())
        {
            await OfficePreviewWorker.RunAsync(input, output);
            check(output.Length > 4, "truncated Office protocol input returns a bounded error response");
        }
        check(!Process.GetCurrentProcess().HasExited, "the main process remains responsive after isolated Office failures");
    }
    private static bool Gone(int id)
    {
        try { using var process = Process.GetProcessById(id); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }
}
