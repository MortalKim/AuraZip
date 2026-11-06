using AuraZip.Core;
using System.Text;
using System.Security.Cryptography;

internal static class RarWritingChecks
{
    private sealed class Sink : IProgress<ArchiveProgress>
    {
        public List<ArchiveProgress> Values { get; } = [];
        public Action<ArchiveProgress>? Reported { get; set; }
        public void Report(ArchiveProgress value) { Values.Add(value); Reported?.Invoke(value); }
    }
    public static async Task Run(string root, Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        var encoder = Environment.GetEnvironmentVariable("AURAZIP_TEST_RAR_ENCODER");
        if (string.IsNullOrEmpty(encoder)) throw new InvalidOperationException("Set AURAZIP_TEST_RAR_ENCODER to a separately installed Rar.exe for RAR writing tests.");
        var service = new ArchiveService(Path.GetFullPath("AuraZip/Engine/x64/7z.exe"), encoder);
        check(service.CanCreateRar && RarEncoder.IsEncoder(encoder), "configured official RAR encoder is available");
        check(!RarEncoder.IsEncoder(Path.GetFullPath("AuraZip/Engine/x64/7z.exe")), "7-Zip cannot be selected as a RAR encoder");
        var missing = new ArchiveService(Path.GetFullPath("AuraZip/Engine/x64/7z.exe"), Path.Combine(root, "missing", "Rar.exe"));
        check(!missing.CanCreateRar, "a missing configured RAR encoder does not silently select another executable");
        var directory = Path.Combine(root, "rar-writing"); Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source"); Directory.CreateDirectory(Path.Combine(source, "目录", "empty"));
        var text = Path.Combine(source, "资料.txt"); var payload = Encoding.UTF8.GetBytes("中文 RAR write roundtrip\n"); await File.WriteAllBytesAsync(text, payload);
        var nested = Path.Combine(source, "目录", "note.txt"); await File.WriteAllTextAsync(nested, "nested note");
        var random = Path.Combine(source, "random.bin"); await File.WriteAllBytesAsync(random, RandomNumberGenerator.GetBytes(200000));
        using var control = new OperationControl(); var progress = new Sink();
        await reject(() => missing.CreateArchiveAsync(Path.Combine(directory, "missing.rar"), [text], new("rar"), control, progress, default), "RAR creation requires an encoder");
        foreach (var encryptNames in new[] { false, true })
        foreach (var password in new string?[] { null, "中文 password *?" })
        {
            var label = (password is null ? "plain" : "encrypted") + "-headers-" + encryptNames;
            var path = Path.Combine(directory, label + ".rar"); progress.Values.Clear();
            var created = await service.CreateArchiveAsync(path, [text, Path.Combine(source, "目录"), random], new("rar", Password: password, EncryptNames: encryptNames), control, progress, default);
            var index = await service.OpenAsync(path, password);
            check(password is null ? index.Items.All(i => !i.IsEncrypted) : index.Items.Where(i => !i.IsDirectory).All(i => i.IsEncrypted), "RAR encryption matches the user's explicit choice: " + label);
            check(created.Files.Count == 1 && index.Items.Any(i => i.Key == "目录/empty" && i.IsDirectory) && index.Items.Any(i => i.Key == "目录/note.txt"), "RAR creation preserves folders and empty directories: " + label);
            check(created.OriginalBytes == payload.Length + 200000 + new FileInfo(nested).Length && created.PackedBytes == new FileInfo(path).Length, "RAR result reports real input and output sizes: " + label);
            check((await service.ReadPreviewAsync(path, "资料.txt", password)).SequenceEqual(payload), "RAR payload previews correctly: " + label);
            check(progress.Values.Last().FilesDone == 3 && progress.Values.Last().TotalFiles == 3 && progress.Values.Last().BytesDone == created.OriginalBytes, "RAR completion reports the final file count: " + label);
            var output = Path.Combine(directory, label + "-out"); await service.ExtractAsync(index, output, null, ConflictPolicy.Fail, password, control, progress, default);
            check(File.ReadAllBytes(Path.Combine(output, "random.bin")).SequenceEqual(File.ReadAllBytes(random)) && Directory.Exists(Path.Combine(output, "目录/empty")), "RAR roundtrip extraction matches original bytes: " + label);
            if (password is not null)
            {
                if (encryptNames) await reject(() => service.OpenAsync(path, "wrong"), "RAR encrypted headers reject an incorrect password: " + label);
                await reject(() => service.TestAsync(index, "wrong", control, progress, default), "RAR writing retains password protection: " + label);
                var before = await ArchiveService.ComputeHashesAsync(path);
                await reject(() => service.EditZipAsync(index, null, null, "资料.txt", "bad.txt", control, progress, default, "wrong"), "RAR editing rejects a wrong password: " + label);
                check(await ArchiveService.ComputeHashesAsync(path) == before, "wrong-password RAR edit preserves the original: " + label);
            }
            await service.EditZipAsync(index, null, null, "资料.txt", "重命名.txt", control, progress, default, password);
            index = await service.OpenAsync(path, password);
            check(index.Items.Any(i => i.Key == "重命名.txt") && !index.Items.Any(i => i.Key == "资料.txt"), "RAR file rename persists: " + label);
            await service.EditZipAsync(index, null, null, "目录", "新目录", control, progress, default, password);
            index = await service.OpenAsync(path, password);
            check(index.Items.Any(i => i.Key == "新目录/note.txt") && index.Items.Any(i => i.Key == "新目录/empty") && !index.Items.Any(i => i.Key.StartsWith("目录")), "RAR folder rename preserves descendants: " + label);
            var addition = Path.Combine(directory, "addition.txt"); await File.WriteAllTextAsync(addition, "added content");
            await service.EditZipAsync(index, [addition], null, null, null, control, progress, default, password, "新目录");
            index = await service.OpenAsync(path, password);
            check(Encoding.UTF8.GetString(await service.ReadPreviewAsync(path, "新目录/addition.txt", password)) == "added content", "RAR addition uses the current folder: " + label);
            await File.WriteAllTextAsync(addition, "updated content");
            await service.EditZipAsync(index, [addition], null, null, null, control, progress, default, password, "新目录");
            index = await service.OpenAsync(path, password);
            check(Encoding.UTF8.GetString(await service.ReadPreviewAsync(path, "新目录/addition.txt", password)) == "updated content", "RAR addition replaces a matching entry: " + label);
            await service.EditZipAsync(index, null, ["新目录"], null, null, control, progress, default, password);
            index = await service.OpenAsync(path, password);
            check(!index.Items.Any(i => i.Key.StartsWith("新目录")) && index.Items.Any(i => i.Key == "重命名.txt"), "RAR deletion removes a folder and its descendants: " + label);
            var unchanged = await ArchiveService.ComputeHashesAsync(path);
            await reject(() => service.EditZipAsync(index, null, null, "重命名.txt", "random.bin", control, progress, default, password), "RAR duplicate rename is rejected: " + label);
            await reject(() => service.EditZipAsync(index, null, index.Items.Select(i => i.Key).ToArray(), null, null, control, progress, default, password), "RAR last-entry deletion cannot remove the archive: " + label);
            check(await ArchiveService.ComputeHashesAsync(path) == unchanged, "invalid RAR edits preserve original bytes: " + label);
            await service.TestAsync(index, password, control, progress, default);
            if (password is not null) check(index.Items.Where(i => !i.IsDirectory).All(i => i.IsEncrypted), "updated RAR files retain encryption: " + label);
            if (password is not null && encryptNames) await reject(() => service.OpenAsync(path, "wrong"), "RAR editing preserves encrypted archive headers: " + label);
        }
        var split = Path.Combine(directory, "split.rar");
        var splitResult = await service.CreateArchiveAsync(split, [random], new("rar", Level: 0, VolumeBytes: 32768, Password: "split-password"), control, progress, default);
        check(splitResult.Files.Count > 2 && splitResult.Files[0].EndsWith(".part1.rar") && splitResult.Files.All(File.Exists), "RAR splitting returns its first volume before later volumes");
        var splitIndex = await service.OpenAsync(splitResult.Files[0], "split-password"); await service.TestAsync(splitIndex, "split-password", control, progress, default);
        var splitOutput = Path.Combine(directory, "split-out"); await service.ExtractAsync(splitIndex, splitOutput, null, ConflictPolicy.Fail, "split-password", control, progress, default);
        check(File.ReadAllBytes(Path.Combine(splitOutput, "random.bin")).SequenceEqual(File.ReadAllBytes(random)), "encrypted RAR split creation roundtrips exactly");
        check(!service.SupportsEditing(splitIndex), "RAR split archives remain read-only");
        await reject(() => service.CreateArchiveAsync(split, [text], new("rar"), control, progress, default), "existing RAR volumes block a new output with the same base name");
        await reject(() => service.CreateArchiveAsync(text, [text], new("rar"), control, progress, default, true), "RAR output cannot overwrite its source");
        await reject(() => service.CreateArchiveAsync(Path.Combine(directory, "long-password.rar"), [text], new("rar", Password: new string('a', 128)), control, progress, default), "RAR passwords cannot be silently truncated");
        var legacy = Path.Combine(directory, "legacy.rar"); File.Copy("tests/Fixtures/Rar.rar", legacy);
        var legacyIndex = await service.OpenAsync(legacy); var legacyItem = legacyIndex.Items.First(i => !i.IsDirectory);
        var legacyBytes = await service.ReadPreviewAsync(legacy, legacyItem.Key, null);
        await service.EditZipAsync(legacyIndex, null, null, legacyItem.Key, "legacy-renamed" + Path.GetExtension(legacyItem.Key), control, progress, default);
        var legacyNewKey = (legacyItem.Parent.Length == 0 ? "" : legacyItem.Parent + "/") + "legacy-renamed" + Path.GetExtension(legacyItem.Key);
        check((await service.ReadPreviewAsync(legacy, legacyNewKey, null)).SequenceEqual(legacyBytes), "RAR4 native editing preserves original payload bytes");
        check(File.ReadAllBytes(legacy).Take(7).SequenceEqual(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0x00 }), "RAR4 editing preserves the archive format generation");
        await service.EditZipAsync(await service.OpenAsync(legacy), [text], null, null, null, control, progress, default, additionRoot: "added-folder");
        check((await service.ReadPreviewAsync(legacy, "added-folder/资料.txt", null)).SequenceEqual(payload), "RAR4 accepts new entries without converting the archive");
        check(File.ReadAllBytes(legacy).Take(7).SequenceEqual(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0x00 }), "RAR4 addition preserves the original format generation");
        var legacyHash = await ArchiveService.ComputeHashesAsync(legacy);
        using (var cancelled = new CancellationTokenSource())
        {
            var cancellingProgress = new Sink { Reported = p => { if (p.Stage == ArchiveOperationStage.Preparing) cancelled.Cancel(); } };
            await reject(() => service.EditZipAsync(awaitedLegacy(), null, null, legacyNewKey, "cancelled.txt", control, cancellingProgress, cancelled.Token), "RAR editing supports cancellation during source staging");
            check(await ArchiveService.ComputeHashesAsync(legacy) == legacyHash, "cancelled RAR editing preserves original bytes");
        }
        ArchiveIndex awaitedLegacy() => service.OpenAsync(legacy).GetAwaiter().GetResult();
        var conflictIndex = await service.OpenAsync(legacy); int changed = 0;
        var conflictProgress = new Sink { Reported = p =>
        {
            if (p.Stage == ArchiveOperationStage.Processing && Interlocked.CompareExchange(ref changed, 1, 0) == 0)
                File.AppendAllText(legacy, "independent modification");
        } };
        await reject(() => service.EditZipAsync(conflictIndex, null, null, legacyNewKey, "conflict.txt", control, conflictProgress, default), "RAR editing detects an independent change before publication");
        check(changed == 1 && File.ReadAllText(legacy).EndsWith("independent modification"), "RAR conflict handling preserves the independent change");
        foreach (var cancelWhilePaused in new[] { false, true })
        {
            using var pausedControl = new OperationControl(); using var cancelled = new CancellationTokenSource();
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); int signalled = 0;
            var pausedProgress = new Sink { Reported = p =>
            {
                if (p.Stage == ArchiveOperationStage.Processing && Interlocked.CompareExchange(ref signalled, 1, 0) == 0)
                { pausedControl.TogglePause(); signal.TrySetResult(true); }
            } };
            var target = Path.Combine(directory, "paused-" + cancelWhilePaused + ".rar");
            var pending = service.CreateArchiveAsync(target, [random, text], new("rar", Level: 0), pausedControl, pausedProgress, cancelled.Token);
            try
            {
                check(await Task.WhenAny(signal.Task, Task.Delay(10000)) == signal.Task, "RAR compression reports pausable progress: cancel=" + cancelWhilePaused);
                await Task.Delay(150);
                check(!pending.IsCompleted && !File.Exists(target), "paused RAR creation does not publish incomplete output: cancel=" + cancelWhilePaused);
                if (cancelWhilePaused)
                {
                    cancelled.Cancel(); await reject(async () => await pending, "cancellation wakes a paused RAR operation");
                    check(!File.Exists(target), "cancelling RAR creation leaves no final output");
                }
                else { pausedControl.TogglePause(); await pending; check(File.Exists(target), "RAR creation resumes and finishes after pausing"); }
            }
            finally { if (pausedControl.IsPaused) pausedControl.TogglePause(); }
        }
        var intermediate = Path.Combine(directory, "progress.rar"); progress.Values.Clear();
        await service.CreateArchiveAsync(intermediate, [random, text, nested], new("rar", Level: 0), control, progress, default);
        check(progress.Values.Any(p => p.FilesDone > 0 && p.FilesDone < p.TotalFiles), "RAR compression advances the completed-file count before completion");
        var outputHash = await ArchiveService.ComputeHashesAsync(intermediate);
        await reject(() => service.CreateArchiveAsync(intermediate, [text], new("rar"), control, progress, default), "RAR creation refuses an existing output without overwrite authorization");
        check(await ArchiveService.ComputeHashesAsync(intermediate) == outputHash, "RAR output conflict preserves the existing archive");
        await service.CreateArchiveAsync(intermediate, [text], new("rar"), control, progress, default, true);
        check((await service.OpenAsync(intermediate)).Items.Count(i => !i.IsDirectory) == 1, "explicit single-volume RAR overwrite replaces the completed archive");
        var mixedParent = Path.Combine(directory, "other-parent"); Directory.CreateDirectory(mixedParent);
        var odd = new List<string>();
        foreach (var name in new[] { "-leading.txt", "@leading.txt", "括号 [a] #.txt" })
        { var file = Path.Combine(mixedParent, name); await File.WriteAllTextAsync(file, name); odd.Add(file); }
        var mixed = Path.Combine(directory, "mixed-parent.rar");
        await service.CreateArchiveAsync(mixed, odd.Append(text).ToArray(), new("rar"), control, progress, default);
        var mixedIndex = await service.OpenAsync(mixed);
        check(odd.All(p => mixedIndex.Items.Any(i => i.Key == Path.GetFileName(p))) && mixedIndex.Items.Any(i => i.Key == "资料.txt"), "mixed-parent RAR selection stages Unicode and option-like filenames literally");
        foreach (var file in odd) check(Encoding.UTF8.GetString(await service.ReadPreviewAsync(mixed, Path.GetFileName(file), null)) == Path.GetFileName(file), "option-like RAR filename payload is preserved: " + Path.GetFileName(file));
        var inside = Path.Combine(source, "inside.rar");
        await service.CreateArchiveAsync(inside, [source], new("rar"), control, progress, default);
        var insideIndex = await service.OpenAsync(inside);
        check(!insideIndex.Items.Any(i => i.Key.EndsWith("inside.rar") || i.Key.Contains(".aurazip-")) && insideIndex.Items.Any(i => i.Key == "source/资料.txt"), "RAR creation inside the source folder excludes its output and temporary workspace");
        var lockedArchive = Path.Combine(directory, "locked.rar"); File.Copy(intermediate, lockedArchive);
        var start = new System.Diagnostics.ProcessStartInfo(encoder) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var arg in new[] { "k", "-cfg-", "-scf", "-idq", lockedArchive }) start.ArgumentList.Add(arg);
        using (var encoderProcess = System.Diagnostics.Process.Start(start)!)
        {
            encoderProcess.StandardInput.Close(); var stdout = encoderProcess.StandardOutput.ReadToEndAsync(); var stderr = encoderProcess.StandardError.ReadToEndAsync();
            await encoderProcess.WaitForExitAsync(); await stdout; await stderr;
            check(encoderProcess.ExitCode == 0, "official RAR encoder can produce a locked test archive");
        }
        var lockedHash = await ArchiveService.ComputeHashesAsync(lockedArchive); var lockedIndex = await service.OpenAsync(lockedArchive);
        try
        {
            await service.EditZipAsync(lockedIndex, null, null, "资料.txt", "cannot-rename.txt", control, progress, default);
            check(false, "locked RAR modification must fail");
        }
        catch (RarEncoderException ex) { check(ex.ExitCode == 4, "a locked RAR returns a specific encoder failure code"); }
        check(await ArchiveService.ComputeHashesAsync(lockedArchive) == lockedHash, "locked RAR modification preserves all original bytes");
        var readOnlyIndex = await service.OpenAsync(intermediate); var readOnlyHash = await ArchiveService.ComputeHashesAsync(intermediate);
        File.SetAttributes(intermediate, FileAttributes.ReadOnly);
        try { await reject(() => service.EditZipAsync(readOnlyIndex, null, null, "资料.txt", "read-only.txt", control, progress, default), "read-only RAR replacement is refused"); }
        finally { File.SetAttributes(intermediate, FileAttributes.Normal); }
        check(await ArchiveService.ComputeHashesAsync(intermediate) == readOnlyHash, "read-only RAR protection preserves the original");
        check(!Directory.GetDirectories(directory, ".aurazip-*.tmp").Any(), "RAR success and failure remove destination staging directories");
    }
}
