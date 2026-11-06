using System.Diagnostics;
using System.IO.Compression;
using System.Formats.Tar;
using System.Text;
using AuraZip.Core;

var root = Path.Combine(Path.GetTempPath(), "AuraZip-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var service = new ArchiveService(Path.GetFullPath("AuraZip/Engine/x64/7z.exe"));
var progress = new DirectProgress();
int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED: " + name); Console.WriteLine("PASS " + name); checks++; }
async Task Reject(Func<Task> action, string name) { try { await action(); } catch { Console.WriteLine("PASS " + name); checks++; return; } throw new Exception("FAILED: " + name); }
string CreateZip(string name, params (string Name, string Content)[] entries)
{
    var path = Path.Combine(root, name);
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var entry in entries) { var item = archive.CreateEntry(entry.Name, CompressionLevel.NoCompression); using var writer = new StreamWriter(item.Open(), new UTF8Encoding(false)); writer.Write(entry.Content); }
    return path;
}

try
{
    var shellFile = Path.Combine(root, "含 空格.zip");
    var shell = ShellRequest.Parse(new[] { "--shell", "smart", "--", shellFile, shellFile.ToUpperInvariant() });
    Check(shell?.Action == "smart" && shell.Paths.Count == 1 && shell.Paths[0] == shellFile, "shell requests preserve Unicode/spaces and deduplicate paths");
    Check(ShellRequest.Parse(new[] { "--shell", "erase", "--", shellFile }) is null, "shell actions are allowlisted");
    Check(ShellRequest.Parse(new[] { "--shell", "smart", "--", Path.Combine(root, "ordinary.txt") }) is null, "shell rejects unsupported extensions");
    Check(ShellRequest.Parse(new[] { "--shell", "smart", "--", "bad\0.zip" }) is null, "shell rejects invalid paths without crashing");
    Check(ShellRequest.Parse(new[] { "--shell", "smart", shellFile }) is null, "shell requires an explicit argument separator");
    var path = CreateZip("资料.zip", ("docs/readme.txt", "真实解压内容"), ("root.txt", "root"));
    var index = await service.OpenAsync(path);
    Check(index.Items.Any(e => e.Key == "docs" && e.IsDirectory), "implicit directories are indexed");
    Check(index.Directory == root, "archive parent directory is available");
    Check(ArchiveService.ResolveDestination(index, root, true, false) == Path.Combine(root, "资料"), "smart extraction wraps multiple root items");
    Check(ArchiveService.ResolveDestination(new ArchiveIndex(Path.Combine(root, "...zip"), index.Items), root, true, false) == Path.Combine(root, "Extracted"), "archive basename cannot redirect smart extraction to a parent directory");
    var single = await service.OpenAsync(CreateZip("单目录.zip", ("docs/file.txt", "one")));
    Check(ArchiveService.ResolveDestination(single, root, true, false) == root, "smart extraction preserves a single root folder");
    Check(ArchiveService.ResolveDestination(index, root, true, false, new[] { "root.txt" }) == root, "smart extraction of one selected file has no wrapper");
    Check(Encoding.UTF8.GetString(await service.ReadPreviewAsync(path, "docs/readme.txt", null)) == "真实解压内容", "preview reads real content");
    using var control = new OperationControl();
    var output = Path.Combine(root, "output");
    var result = await service.ExtractAsync(index, output, null, ConflictPolicy.Fail, null, control, progress, default);
    Check(result.FilesWritten == 2 && File.ReadAllText(Path.Combine(output, "docs/readme.txt")) == "真实解压内容", "real ZIP extraction");
    var partial = Path.Combine(root, "selected-output");
    await service.ExtractAsync(index, partial, new[] { "docs/readme.txt" }, ConflictPolicy.Fail, null, control, progress, default, "docs");
    Check(File.ReadAllText(Path.Combine(partial, "readme.txt")) == "真实解压内容" && !Directory.Exists(Path.Combine(partial, "docs")), "selection extraction is relative to the browsed folder");
    File.WriteAllText(Path.Combine(output, "root.txt"), "keep");
    result = await service.ExtractAsync(index, output, new[] { "root.txt" }, ConflictPolicy.Skip, null, control, progress, default);
    Check(result.FilesSkipped == 1 && File.ReadAllText(Path.Combine(output, "root.txt")) == "keep", "skip preserves existing data");
    await service.ExtractAsync(index, output, new[] { "root.txt" }, ConflictPolicy.Rename, null, control, progress, default);
    Check(File.ReadAllText(Path.Combine(output, "root (2).txt")) == "root", "rename preserves existing data");
    await service.ExtractAsync(index, output, new[] { "root.txt" }, ConflictPolicy.Overwrite, null, control, progress, default);
    Check(File.ReadAllText(Path.Combine(output, "root.txt")) == "root", "overwrite replaces only completed files");
    foreach (var key in new[] { "../escape.txt", "/absolute.txt", "C:/evil.txt", "safe/../../evil.txt", "safe:stream", "NUL.txt", "a/../b", "a./b" })
        await Reject(() => Task.FromResult(ArchiveService.SafeOutputPath(root, key)), "reject unsafe path " + key);
    var malicious = await service.OpenAsync(CreateZip("unsafe.zip", ("good.txt", "good"), ("../escape.txt", "bad")));
    var unsafeOutput = Path.Combine(root, "unsafe-output");
    await Reject(() => service.ExtractAsync(malicious, unsafeOutput, null, ConflictPolicy.Fail, null, control, progress, default), "validate paths before writing any output");
    Check(!Directory.Exists(unsafeOutput), "unsafe archive creates no output directory");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    await Reject(() => service.ExtractAsync(index, Path.Combine(root, "cancelled"), null, ConflictPolicy.Fail, null, control, progress, cancelled.Token), "cancellation stops extraction");
    var source = Path.Combine(root, "input.txt"); File.WriteAllText(source, "zip roundtrip");
    var created = Path.Combine(root, "created.zip");
    await service.CreateZipAsync(created, new[] { source }, control, progress, default);
    var roundtrip = await service.OpenAsync(created);
    Check(Encoding.UTF8.GetString(await service.ReadPreviewAsync(created, "input.txt", null)) == "zip roundtrip", "created ZIP roundtrips");
    await service.TestAsync(roundtrip, null, control, progress, default);
    Check(progress.Last?.FilesDone == 1, "integrity test drains every file");
    var corruptPath = CreateZip("corrupt.zip", ("bad.txt", "CRC-test-data"));
    var corruptBytes = File.ReadAllBytes(corruptPath);
    var contentOffset = corruptBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("CRC-test-data"));
    corruptBytes[contentOffset] ^= 0x01; File.WriteAllBytes(corruptPath, corruptBytes);
    var corruptIndex = await service.OpenAsync(corruptPath);
    await Reject(() => service.TestAsync(corruptIndex, null, control, progress, default), "integrity test rejects a CRC mismatch");
    var corruptOutput = Path.Combine(root, "corrupt-output");
    await Reject(() => service.ExtractAsync(corruptIndex, corruptOutput, null, ConflictPolicy.Fail, null, control, progress, default), "corrupt data is rejected before committing the output file");
    Check(!File.Exists(Path.Combine(corruptOutput, "bad.txt")) && !Directory.EnumerateFiles(corruptOutput).Any(), "failed extraction removes its temporary file");
    var fixtureFolder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/Fixtures"));
    await SystematicChecks.Run(service, root, Check, Reject);
    await EncryptedReadChecks.Run(service, root, fixtureFolder, Check, Reject);
    foreach (var fixture in Directory.EnumerateFiles(fixtureFolder, "*.rar").Where(p => string.Equals(ArchiveFormats.FirstVolume(p), p, StringComparison.OrdinalIgnoreCase)))
    {
        var rar = await service.OpenAsync(fixture);
        var rarOutput = Path.Combine(root, Path.GetFileName(fixture));
        var extracted = await service.ExtractAsync(rar, rarOutput, null, ConflictPolicy.Fail, null, control, progress, default);
        await service.TestAsync(rar, null, control, progress, default);
        Check(extracted.FilesWritten > 0 && Directory.EnumerateFiles(rarOutput, "*", SearchOption.AllDirectories).Count() == extracted.FilesWritten, "RAR extraction and checksums: " + Path.GetFileName(fixture));
    }
    // A TAR+GZip fixture exercises compressed-container detection, not just ZIP.
    var tarPath = Path.Combine(root, "source.tar");
    using (var writer = new TarWriter(File.Create(tarPath))) writer.WriteEntry(source, "input.txt");
    var tgzPath = tarPath + ".gz";
    using (var input = File.OpenRead(tarPath)) using (var gzip = new GZipStream(File.Create(tgzPath), CompressionLevel.Optimal)) input.CopyTo(gzip);
    var tarIndex = await service.OpenAsync(tgzPath);
    Check(tarIndex.Items.Any(i => i.Key == "input.txt"), "TAR.GZ opens as the contained TAR directory");
    var sevenZip = @"C:\Program Files\7-Zip\7z.exe";
    if (File.Exists(sevenZip))
    {
        var sevenPath = Path.Combine(root, "sample.7z");
        var start = new ProcessStartInfo(sevenZip) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "a", "-t7z", sevenPath, source }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync();
        Check(process.ExitCode == 0, "7Z fixture creation");
        var sevenIndex = await service.OpenAsync(sevenPath);
        await service.ExtractAsync(sevenIndex, Path.Combine(root, "seven-output"), null, ConflictPolicy.Fail, null, control, progress, default);
        Check(File.ReadAllText(Path.Combine(root, "seven-output", "input.txt")) == "zip roundtrip", "7Z opens and extracts real data");
        foreach (var format in new[] { "7z", "zip" })
        {
            var encryptedPath = Path.Combine(root, "encrypted." + format);
            var encryptedStart = new ProcessStartInfo(sevenZip) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "a", "-t" + format, "-pfixture-password", format == "7z" ? "-mhe=on" : "-mem=AES256", encryptedPath, source }) encryptedStart.ArgumentList.Add(arg);
            using var encryptedProcess = Process.Start(encryptedStart)!; await encryptedProcess.StandardOutput.ReadToEndAsync(); await encryptedProcess.WaitForExitAsync();
            Check(encryptedProcess.ExitCode == 0, "encrypted fixture creation: " + format);
            var encryptedIndex = await service.OpenAsync(encryptedPath, "fixture-password");
            var encryptedOutput = Path.Combine(root, "encrypted-output-" + format);
            await service.ExtractAsync(encryptedIndex, encryptedOutput, null, ConflictPolicy.Fail, "fixture-password", control, progress, default);
            Check(File.ReadAllText(Path.Combine(encryptedOutput, "input.txt")) == "zip roundtrip", "password-protected extraction: " + format);
        }
    }
    var engine = new ArchiveService(Path.GetFullPath("AuraZip/Engine/x64/7z.exe"));
    var manySource = Path.Combine(root, "many-input"); Directory.CreateDirectory(manySource);
    for (int i = 0; i < 115; i++) File.WriteAllText(Path.Combine(manySource, $"文件-{i:D3}.txt"), new string((char)('a' + i % 26), 8192));
    bool destinationWorkspaceObserved = false, directInputObserved = false;
    var observed = new RecordingProgress(p =>
    {
        var staging = Directory.GetDirectories(root, ".aurazip-*.tmp").SingleOrDefault();
        destinationWorkspaceObserved |= staging is not null;
        if (staging is not null && p.Stage == ArchiveOperationStage.Compressing)
            directInputObserved |= !Directory.Exists(Path.Combine(staging, "input"));
    });
    var manyTarget = Path.Combine(root, "many-output.zip");
    await engine.CreateArchiveAsync(manyTarget, new[] { manySource }, new("zip"), control, observed, default);
    Check(observed.Values.Any(p => p.Stage == ArchiveOperationStage.Compressing && p.FilesDone > 0 && p.FilesDone < 115), "multi-file compression reports intermediate completed file counts");
    Check(observed.Values.Last() is { FilesDone: 115, TotalFiles: 115 } && observed.Values.Zip(observed.Values.Skip(1)).All(pair => pair.First.BytesDone <= pair.Second.BytesDone && pair.First.FilesDone <= pair.Second.FilesDone), "compression progress and file counts are monotonic and finish at the actual totals");
    Check((await engine.OpenAsync(manyTarget)).Items.Count(i => !i.IsDirectory) == 115 && !Directory.GetDirectories(root, ".aurazip-*.tmp").Any(), "115-file compression commits all entries and removes destination staging");
    Check(destinationWorkspaceObserved && directInputObserved, "compression stages beside the destination and reads ordinary selections directly");
    using (var liveCancel = new CancellationTokenSource())
    {
        var cancelledTarget = Path.Combine(root, "live-cancel.zip");
        var originalBytes = new byte[] { 1, 2, 3, 4 }; File.WriteAllBytes(cancelledTarget, originalBytes);
        var cancelProgress = new RecordingProgress(p => { if (p.Stage == ArchiveOperationStage.Compressing && p.FilesDone > 0) liveCancel.Cancel(); });
        await Reject(() => engine.CreateArchiveAsync(cancelledTarget, new[] { manySource }, new("zip"), control, cancelProgress, liveCancel.Token, overwrite: true), "cancellation during compression stops the engine");
        Check(File.ReadAllBytes(cancelledTarget).SequenceEqual(originalBytes) && !Directory.GetDirectories(root, ".aurazip-*.tmp").Any(), "engine cancellation preserves an existing destination and cleans staged output");
    }
    var nestedTarget = Path.Combine(manySource, "inside.zip");
    await engine.CreateArchiveAsync(nestedTarget, new[] { manySource }, new("zip"), control, progress, default);
    var nestedCreated = await engine.OpenAsync(nestedTarget);
    Check(nestedCreated.Items.Count(i => !i.IsDirectory) == 115 && !nestedCreated.Items.Any(i => i.Key.Contains(".aurazip-") || i.Key.EndsWith("inside.zip")), "output inside the source folder excludes temporary and output archives");
    var mixedA = Path.Combine(root, "mixed-a"); var mixedB = Path.Combine(root, "mixed-b");
    Directory.CreateDirectory(mixedA); Directory.CreateDirectory(mixedB);
    File.WriteAllText(Path.Combine(mixedA, "a.txt"), "a"); File.WriteAllText(Path.Combine(mixedB, "b.txt"), "b");
    var mixedTarget = Path.Combine(root, "mixed-output.zip");
    await engine.CreateArchiveAsync(mixedTarget, new[] { Path.Combine(mixedA, "a.txt"), Path.Combine(mixedB, "b.txt") }, new("zip"), control, progress, default);
    Check((await engine.OpenAsync(mixedTarget)).Items.Select(i => i.Key).Order().SequenceEqual(new[] { "a.txt", "b.txt" }), "mixed-parent selections preserve entry names");
    var folderSource = Path.Combine(root, "folder"); Directory.CreateDirectory(Path.Combine(folderSource, "empty", "nested"));
    File.WriteAllText(Path.Combine(folderSource, "file.txt"), "mixed input");
    var emptyZip = Path.Combine(root, "empty-folder.zip");
    await service.CreateZipAsync(emptyZip, new[] { folderSource, source }, control, progress, default);
    var emptyIndex = await service.OpenAsync(emptyZip);
    await service.ExtractAsync(emptyIndex, Path.Combine(root, "empty-roundtrip"), null, ConflictPolicy.Fail, null, control, progress, default);
    Check(Directory.Exists(Path.Combine(root, "empty-roundtrip/folder/empty/nested")) && emptyIndex.Items.Any(i => i.Key == "input.txt"), "mixed files and folders retain nested empty directories");
    foreach (var format in new[] { "zip", "7z" })
    {
        var enginePassword = format == "zip" ? "pass-test" : "pass-中文";
        var target = Path.Combine(root, "engine." + format);
        var packed = await engine.CreateArchiveAsync(target, new[] { folderSource, source }, new(format, Password: enginePassword), control, progress, default);
        Check(packed.Files.Count == 1 && packed.PackedBytes > 0, "bundled engine creates encrypted " + format);
        var packedIndex = await service.OpenAsync(target, enginePassword);
        Check(packedIndex.Items.Any(i => i.IsEncrypted), "encryption detected for engine " + format);
        var unpacked = Path.Combine(root, "engine-out-" + format);
        await service.ExtractAsync(packedIndex, unpacked, null, ConflictPolicy.Fail, enginePassword, control, progress, default);
        Check(File.ReadAllText(Path.Combine(unpacked, "input.txt")) == "zip roundtrip" && Directory.Exists(Path.Combine(unpacked, "folder/empty/nested")), "encrypted engine roundtrip including empty folders " + format);
        if (format == "zip")
        {
            await engine.EditZipAsync(packedIndex, null, new[] { "input.txt" }, "folder/file.txt", "renamed.txt", control, progress, default, enginePassword);
            var updated = await service.OpenAsync(target, enginePassword);
            Check(updated.Items.Any(i => i.Key == "folder/renamed.txt" && i.IsEncrypted) && !updated.Items.Any(i => i.Key == "input.txt"), "encrypted ZIP update preserves encryption and applies rename/delete");
            await service.TestAsync(updated, enginePassword, control, progress, default);
        }
    }
    var random = Path.Combine(root, "random.bin"); File.WriteAllBytes(random, System.Security.Cryptography.RandomNumberGenerator.GetBytes(70000));
    var volumes = await engine.CreateArchiveAsync(Path.Combine(root, "split.7z"), new[] { random }, new("7z", VolumeBytes: 16000), control, progress, default);
    Check(volumes.Files.Count > 2, "engine produces real split volumes");
    var splitIndex = await service.OpenAsync(volumes.Files[1]);
    await service.ExtractAsync(splitIndex, Path.Combine(root, "split-out"), null, ConflictPolicy.Fail, null, control, progress, default);
    Check(File.ReadAllBytes(Path.Combine(root, "split-out/random.bin")).SequenceEqual(File.ReadAllBytes(random)), "split archive opens from a later volume and extracts correctly");
    File.Move(volumes.Files[1], volumes.Files[1] + ".missing");
    await Reject(() => service.OpenAsync(volumes.Files[0]), "missing middle volume is rejected");
    var beforeCancel = File.ReadAllBytes(emptyZip);
    await Reject(() => service.EditZipAsync(emptyIndex, new[] { random }, null, null, null, control, progress, cancelled.Token), "cancelled update stops without replacing the original archive");
    Check(File.ReadAllBytes(emptyZip).SequenceEqual(beforeCancel), "cancelled update preserves original ZIP bytes");
    await service.EditZipAsync(emptyIndex, new[] { source }, new[] { "input.txt" }, "folder/empty", "renamed-empty", control, progress, default, additionRoot: "folder");
    var edited = await service.OpenAsync(emptyZip);
    Check(edited.Items.Any(i => i.Key == "folder/renamed-empty/nested") && edited.Items.Any(i => i.Key == "folder/input.txt") && !edited.Items.Any(i => i.Key == "input.txt"), "ZIP update supports folder rename, delete and adding inside current directory");
    await service.TestAsync(edited, null, control, progress, default);
    var gbkZip = Path.Combine(root, "legacy.zip");
    using (var legacy = ZipFile.Open(gbkZip, ZipArchiveMode.Create, Encoding.GetEncoding(936))) { var e = legacy.CreateEntry("中文.txt"); using var writer = new StreamWriter(e.Open()); writer.Write("GBK filename"); }
    var gbkIndex = await service.OpenAsync(gbkZip, codePage: 936);
    Check(gbkIndex.Items.Any(i => i.Key == "中文.txt"), "forced GBK filename decoding");
    await service.ExtractAsync(gbkIndex, Path.Combine(root, "gbk-out"), null, ConflictPolicy.Fail, null, control, progress, default);
    Check(File.ReadAllText(Path.Combine(root, "gbk-out/中文.txt")) == "GBK filename", "forced filename encoding is used during extraction");
    await service.EditZipAsync(gbkIndex, null, null, "中文.txt", "改名.txt", control, progress, default);
    Check((await service.OpenAsync(gbkZip)).Items.Any(i => i.Key == "改名.txt"), "legacy ZIP update preserves decoded filenames in UTF-8");
    File.SetLastWriteTime(Path.Combine(output, "root.txt"), DateTime.Now.AddDays(2)); File.WriteAllText(Path.Combine(output, "root.txt"), "newer"); File.SetLastWriteTime(Path.Combine(output, "root.txt"), DateTime.Now.AddDays(2));
    var newer = await service.ExtractAsync(index, output, new[] { "root.txt" }, ConflictPolicy.Newer, null, control, progress, default);
    Check(newer.FilesSkipped == 1 && File.ReadAllText(Path.Combine(output, "root.txt")) == "newer", "newer-only policy preserves a newer destination file");
    Check(ShellRequest.Parse(new[] { "--shell", "compress", "--", folderSource, source })?.Paths.Count == 2, "shell compression accepts mixed folders and ordinary files");
    var splitZip = await engine.CreateArchiveAsync(Path.Combine(root, "split.zip"), new[] { random }, new("zip", VolumeBytes: 16000), control, progress, default);
    var splitZipIndex = await service.OpenAsync(splitZip.Files[0]);
    await service.ExtractAsync(splitZipIndex, Path.Combine(root, "split-zip-out"), null, ConflictPolicy.Fail, null, control, progress, default);
    Check(File.ReadAllBytes(Path.Combine(root, "split-zip-out/random.bin")).SequenceEqual(File.ReadAllBytes(random)), "split ZIP volumes roundtrip");
    using (var pausedControl = new OperationControl())
    using (var pauseCancel = new CancellationTokenSource(200))
    {
        pausedControl.TogglePause();
        var pausedPath = Path.Combine(root, "paused.zip");
        await Reject(() => engine.CreateArchiveAsync(pausedPath, new[] { random }, new(), pausedControl, progress, pauseCancel.Token), "paused compression can be cancelled");
        Check(!File.Exists(pausedPath), "cancelled compression commits no output archive");
    }
    var readonlySource = Path.Combine(root, "readonly.txt"); File.WriteAllText(readonlySource, "read only"); File.SetAttributes(readonlySource, FileAttributes.ReadOnly);
    var modifiedTime = new DateTime(2022, 3, 4, 5, 6, 8); Directory.SetLastWriteTime(Path.Combine(folderSource, "empty/nested"), modifiedTime);
    var meta = await engine.CreateArchiveAsync(Path.Combine(root, "metadata.7z"), new[] { readonlySource, folderSource }, new("7z"), control, progress, default);
    var metaIndex = await service.OpenAsync(meta.Files[0]);
    var metaOut = Path.Combine(root, "metadata-out"); await service.ExtractAsync(metaIndex, metaOut, null, ConflictPolicy.Fail, null, control, progress, default);
    Check((File.GetAttributes(Path.Combine(metaOut, "readonly.txt")) & FileAttributes.ReadOnly) != 0 && Math.Abs((Directory.GetLastWriteTime(Path.Combine(metaOut, "folder/empty/nested")) - modifiedTime).TotalSeconds) < 2, "file attributes and empty-folder timestamps survive compression and extraction");
    var partBase = Path.Combine(root, "missing.part01.rar"); File.WriteAllBytes(partBase, [0]); File.WriteAllBytes(Path.Combine(root, "missing.part03.rar"), [0]);
    await Reject(() => Task.FromResult(ArchiveFormats.Volumes(partBase)), "RAR numbered volume gaps are rejected before decoding");
    var corruptBefore = File.ReadAllBytes(corruptPath);
    await Reject(() => service.EditZipAsync(corruptIndex, null, null, "bad.txt", "renamed.txt", control, progress, default), "ZIP update rejects corrupt retained entries");
    Check(File.ReadAllBytes(corruptPath).SequenceEqual(corruptBefore), "failed ZIP update leaves the original archive unchanged");
    for (int i = 1; i <= 10; i++) File.WriteAllBytes(Path.Combine(root, $"series.part{i}.rar"), [0]);
    var rarFirst = Path.Combine(root, "series.part1.rar");
    Check(ArchiveFormats.Volumes(rarFirst).Count == 10, "RAR volume numbering can grow from one digit to two");
    Check(ArchiveFormats.FirstVolume(Path.Combine(root, "series.part10.rar")) == rarFirst, "opening an unpadded later RAR volume resolves part1");
    var numericFirst = Path.Combine(root, "many.7z.001"); File.WriteAllBytes(numericFirst, [0]);
    Check(ArchiveFormats.FirstVolume(Path.Combine(root, "many.7z.1000")) == numericFirst, "opening a four-digit continuation resolves the original 001 volume");
    foreach (var format in new[] { "tar", "tar.gz", "tar.bz2", "tar.xz" })
    {
        var tarCreated = await engine.CreateArchiveAsync(Path.Combine(root, "created." + format), new[] { source, folderSource }, new(format), control, progress, default);
        var createdIndex = await service.OpenAsync(tarCreated.Files[0]);
        var createdOut = Path.Combine(root, "created-out-" + format);
        await service.ExtractAsync(createdIndex, createdOut, null, ConflictPolicy.Fail, null, control, progress, default);
        Check(File.ReadAllBytes(Path.Combine(createdOut, "input.txt")).SequenceEqual(File.ReadAllBytes(source)) && Directory.Exists(Path.Combine(createdOut, "folder/empty/nested")), format + " creation preserves files and empty folders");
        var splitTar = await engine.CreateArchiveAsync(Path.Combine(root, "split-created." + format), new[] { random }, new(format, VolumeBytes: 16000), control, progress, default);
        var splitTarOut = Path.Combine(root, "split-created-out-" + format);
        await service.ExtractAsync(await service.OpenAsync(splitTar.Files[0]), splitTarOut, null, ConflictPolicy.Fail, null, control, progress, default);
        Check(splitTar.Files.Count > 1 && File.ReadAllBytes(Path.Combine(splitTarOut, "random.bin")).SequenceEqual(File.ReadAllBytes(random)), format + " custom split volumes roundtrip");
    }
    foreach (var password in new string?[] { null, "中文密码" })
    {
        var seven = Path.Combine(root, password is null ? "editable.7z" : "editable-encrypted.7z");
        await engine.CreateArchiveAsync(seven, new[] { source, folderSource }, new("7z", Password: password), control, progress, default);
        var sevenIndex = await service.OpenAsync(seven, password);
        var before = File.ReadAllBytes(seven);
        await Reject(() => engine.EditZipAsync(sevenIndex, new[] { random }, null, null, null, control, progress, cancelled.Token, password), "cancelled 7Z modification aborts");
        Check(File.ReadAllBytes(seven).SequenceEqual(before), "cancelled 7Z modification preserves original bytes");
        await engine.EditZipAsync(sevenIndex, new[] { random }, new[] { "input.txt" }, "folder/empty", "renamed", control, progress, default, password, "folder");
        sevenIndex = await service.OpenAsync(seven, password);
        Check(sevenIndex.Items.Any(i => i.Key == "folder/random.bin") && sevenIndex.Items.Any(i => i.Key == "folder/renamed/nested") && !sevenIndex.Items.Any(i => i.Key == "input.txt"), "7Z add/delete/rename including encrypted headers");
        await service.TestAsync(sevenIndex, password, control, progress, default);
        if (password is not null) await Reject(() => service.OpenAsync(seven), "encrypted 7Z update retains header encryption");
        await engine.EditZipAsync(sevenIndex, null, sevenIndex.Items.Select(i => i.Key).ToArray(), null, null, control, progress, default, password);
        Check((await service.OpenAsync(seven, password)).Items.Count == 0, "7Z delete all entries yields a valid empty archive");
    }
    var commentZip = CreateZip("comment.zip", ("hello.txt", "hello"));
    await ArchiveService.SetZipCommentAsync(commentZip, "中文注释\nAuraZip");
    Check(ArchiveService.ReadZipComment(commentZip) == "中文注释\nAuraZip", "ZIP comments roundtrip as UTF-8");
    await service.EditZipAsync(await service.OpenAsync(commentZip), null, null, "hello.txt", "renamed.txt", control, progress, default);
    Check(ArchiveService.ReadZipComment(commentZip) == "中文注释\nAuraZip", "ZIP modifications preserve archive comments");
    var beforeLongComment = File.ReadAllBytes(commentZip);
    await Reject(() => ArchiveService.SetZipCommentAsync(commentZip, new string('中', 30000)), "oversized ZIP comment is rejected");
    Check(File.ReadAllBytes(commentZip).SequenceEqual(beforeLongComment), "invalid comment preserves original archive");
    await ArchiveService.SetZipCommentAsync(commentZip, "");
    Check(ArchiveService.ReadZipComment(commentZip) == "", "ZIP comment can be cleared");
    var encryptedCommentZip = Path.Combine(root, "encrypted-comment.zip");
    await engine.CreateArchiveAsync(encryptedCommentZip, new[] { source }, new("zip", Password: "comment-password"), control, progress, default);
    await ArchiveService.SetZipCommentAsync(encryptedCommentZip, "Encrypted archive comment");
    await engine.EditZipAsync(await service.OpenAsync(encryptedCommentZip, "comment-password"), null, null, "input.txt", "updated.txt", control, progress, default, "comment-password");
    Check(ArchiveService.ReadZipComment(encryptedCommentZip) == "Encrypted archive comment", "encrypted ZIP rebuild preserves archive comments");
    var hashFile = Path.Combine(root, "hash.txt"); File.WriteAllText(hashFile, "abc", new UTF8Encoding(false));
    var hashes = await ArchiveService.ComputeHashesAsync(hashFile);
    Check(hashes.SHA256 == "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD" && hashes.MD5 == "900150983CD24FB0D6963F7D28E17F72", "SHA-256 and MD5 match published abc test vectors");
    await Reject(() => ArchiveService.ComputeHashesAsync(hashFile, cancelled.Token), "hash calculation supports cancellation");
    byte[] OfficeDocument(params (string Name, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var file in files) { using var writer = new StreamWriter(zip.CreateEntry(file.Name).Open()); writer.Write(file.Content); }
        return stream.ToArray();
    }
    Check(OfficePreview.Read(OfficeDocument(("word/document.xml", "<document><p><t>中文</t><t> document</t></p></document>")), ".docx").Contains("中文 document"), "Word preview reads document text");
    Check(OfficePreview.Read(OfficeDocument(("xl/sharedStrings.xml", "<sst><si><t>shared</t></si></sst>"), ("xl/worksheets/sheet1.xml", "<worksheet><row><c t='s'><v>0</v></c><c><v>42</v></c></row></worksheet>")), ".xlsx").Contains("shared\t42"), "Excel preview resolves shared strings and cached values");
    Check(OfficePreview.Read(OfficeDocument(("ppt/slides/slide1.xml", "<slide><p><t>slide text</t></p></slide>")), ".pptx").Contains("slide text"), "PowerPoint preview reads slide text");
    await Reject(() => Task.FromResult(OfficePreview.Read(OfficeDocument(("word/document.xml", "<!DOCTYPE a [<!ENTITY b SYSTEM 'file:///C:/Windows/win.ini'>]><document><p><t>&b;</t></p></document>")), ".docx")), "Office preview rejects external XML entities");
    Console.WriteLine($"{checks} checks passed. Fixtures: {root}");
}
catch (Exception ex) { Console.Error.WriteLine(ex); Console.Error.WriteLine("Fixtures retained: " + root); Environment.ExitCode = 1; }

sealed class DirectProgress : IProgress<ArchiveProgress>
{
    public ArchiveProgress? Last { get; private set; }
    public void Report(ArchiveProgress value) => Last = value;
}

sealed class RecordingProgress(Action<ArchiveProgress>? callback = null) : IProgress<ArchiveProgress>
{
    public List<ArchiveProgress> Values { get; } = new();
    public void Report(ArchiveProgress value) { Values.Add(value); callback?.Invoke(value); }
}
