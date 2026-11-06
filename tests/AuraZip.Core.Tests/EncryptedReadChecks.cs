using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using AuraZip.Core;

internal static class EncryptedReadChecks
{
    public static async Task Run(ArchiveService service, string root, string fixtures, Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        using var control = new OperationControl();
        var progress = new Sink();
        var hashes = new Dictionary<string, string>
        {
            ["тест.txt"] = "4D581D93D369F6E1C9B295FF38D82DABD577F927DFAF0C35818C015C85E322D9",
            ["exe/test.exe"] = "8557928804F57ECC340B3BB38B095A3607474EC8DEB0076F316FCFE02B562106",
            ["jpg/test.jpg"] = "B251C7501FB0F55DD4A92FEABE0A6F5733BC40A02679498155FAE9B30138FC53"
        };
        foreach (var path in Directory.GetFiles(Path.Combine(fixtures, "EncryptedRar"), "*.rar").Where(p => ArchiveFormats.FirstVolume(p) == p))
        {
            var name = Path.GetFileName(path);
            var index = await service.OpenAsync(path, "test");
            await service.TestAsync(index, "test", control, progress, default);
            var output = Path.Combine(root, "encrypted-" + name);
            var result = await service.ExtractAsync(index, output, null, ConflictPolicy.Fail, "test", control, progress, default);
            check(result.FilesWritten == 3 && hashes.All(h => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(output, h.Key)))) == h.Value), "encrypted RAR matches NanaZip reference hashes: " + name);
            var preview = await service.ReadPreviewAsync(path, "тест.txt", "test");
            check(Convert.ToHexString(SHA256.HashData(preview)) == hashes["тест.txt"], "encrypted RAR preview validates file data: " + name);
            var selectedOutput = Path.Combine(root, "selected-" + name);
            await service.ExtractAsync(index, selectedOutput, new[] { "exe/test.exe" }, ConflictPolicy.Fail, "test", control, progress, default, "exe");
            check(File.Exists(Path.Combine(selectedOutput, "test.exe")) && Directory.GetFiles(selectedOutput, "*", SearchOption.AllDirectories).Length == 1, "encrypted selected extraction respects current folder: " + name);
            await reject(() => service.TestAsync(index, "wrong", control, progress, default), "encrypted RAR rejects a wrong password: " + name);
            var original = File.ReadAllBytes(Path.Combine(output, "тест.txt"));
            await reject(() => service.ExtractAsync(index, output, null, ConflictPolicy.Overwrite, "wrong", control, progress, default), "wrong RAR password cannot overwrite existing files: " + name);
            check(File.ReadAllBytes(Path.Combine(output, "тест.txt")).SequenceEqual(original) && !Directory.GetDirectories(output, ".aurazip-*.tmp").Any(), "password failure preserves destination and cleans staging: " + name);
            var skipped = await service.ExtractAsync(index, output, null, ConflictPolicy.Skip, "test", control, progress, default);
            check(skipped.FilesWritten == 0 && skipped.FilesSkipped == 3, "encrypted RAR skip conflicts: " + name);
            var renamed = await service.ExtractAsync(index, output, new[] { "тест.txt" }, ConflictPolicy.Rename, "test", control, progress, default);
            check(renamed.FilesWritten == 1 && File.ReadAllBytes(Path.Combine(output, "тест (2).txt")).SequenceEqual(original), "encrypted RAR rename conflicts: " + name);
            try { await service.OpenAsync(path, "wrong"); }
            catch (Exception ex) { check(ArchiveService.IsPasswordError(ex), "encrypted header errors allow password retry: " + name); }
        }

        var encryptedRar = await service.OpenAsync(Path.Combine(fixtures, "EncryptedRar/Rar5.encrypted_filesOnly.rar"), "test");
        var corruptRarPath = Path.Combine(root, "corrupt-encrypted.rar");
        var rarBytes = File.ReadAllBytes(encryptedRar.FilePath); rarBytes[rarBytes.Length / 2] ^= 0x40; File.WriteAllBytes(corruptRarPath, rarBytes);
        var corruptRar = new ArchiveIndex(corruptRarPath, encryptedRar.Items);
        await reject(() => service.TestAsync(corruptRar, "test", control, progress, default), "keyed RAR checksum detects corrupted encrypted data");
        var corruptOutput = Path.Combine(root, "corrupt-encrypted-output");
        await reject(() => service.ExtractAsync(corruptRar, corruptOutput, null, ConflictPolicy.Fail, "test", control, progress, default), "damaged encrypted RAR is not published");
        check(!Directory.GetFiles(corruptOutput, "*", SearchOption.AllDirectories).Any(), "damaged encrypted RAR leaves no completed or temporary files");
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelOutput = Path.Combine(root, "encrypted-read-cancel");
            var cancelProgress = new Sink(_ => cancellation.Cancel());
            await reject(() => service.ExtractAsync(encryptedRar, cancelOutput, null, ConflictPolicy.Fail, "test", control, cancelProgress, cancellation.Token), "encrypted extraction supports cancellation during engine work");
            check(!Directory.GetFiles(cancelOutput, "*", SearchOption.AllDirectories).Any() && !Directory.GetDirectories(cancelOutput, ".aurazip-*.tmp").Any(), "cancelled encrypted extraction cleans its staging directory");
        }

        var source = Path.Combine(root, "auth-payload.txt"); File.WriteAllText(source, "Authenticated archive payload 中文");
        foreach (var method in new[] { "ZipCrypto", "AES128", "AES256" })
        {
            var path = Path.Combine(root, method + ".zip");
            var start = new ProcessStartInfo(Path.GetFullPath("AuraZip/Engine/x64/7z.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "a", "-tzip", "-mem=" + method, "-ptest", path, source }) start.ArgumentList.Add(arg);
            using (var process = Process.Start(start)!)
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(); await stdout; await stderr;
                check(process.ExitCode == 0, "password matrix fixture created: " + method);
            }
            var index = await service.OpenAsync(path);
            var entry = index.Items.Single(i => !i.IsDirectory);
            await service.TestAsync(index, "test", control, progress, default);
            check((await service.ReadPreviewAsync(path, entry.Key, "test")).SequenceEqual(File.ReadAllBytes(source)), "encrypted ZIP preview roundtrip: " + method);
            var output = Path.Combine(root, method + "-output");
            await service.ExtractAsync(index, output, null, ConflictPolicy.Fail, "test", control, progress, default);
            check(File.ReadAllBytes(Path.Combine(output, entry.Key)).SequenceEqual(File.ReadAllBytes(source)), "encrypted ZIP extraction roundtrip: " + method);
            await reject(() => service.TestAsync(index, "wrong", control, progress, default), "wrong ZIP password is rejected: " + method);
            if (method == "ZipCrypto") continue;
            long compressed;
            using (var zip = ZipFile.OpenRead(path)) compressed = zip.Entries[0].CompressedLength;
            var data = File.ReadAllBytes(path);
            int dataOffset = 30 + BitConverter.ToUInt16(data, 26) + BitConverter.ToUInt16(data, 28);
            data[dataOffset + (int)compressed - 1] ^= 0x40;
            var damaged = Path.Combine(root, method + "-bad-auth.zip"); File.WriteAllBytes(damaged, data);
            var badIndex = await service.OpenAsync(damaged);
            await reject(() => service.TestAsync(badIndex, "test", control, progress, default), "ZIP AES authentication tampering is detected: " + method);
            await reject(() => service.ReadPreviewAsync(damaged, entry.Key, "test"), "ZIP AES preview rejects invalid authentication: " + method);
            var original = File.ReadAllBytes(Path.Combine(output, entry.Key));
            await reject(() => service.ExtractAsync(badIndex, output, null, ConflictPolicy.Overwrite, "test", control, progress, default), "ZIP AES authentication failure prevents overwrite: " + method);
            check(File.ReadAllBytes(Path.Combine(output, entry.Key)).SequenceEqual(original) && !Directory.GetDirectories(output, ".aurazip-*.tmp").Any(), "ZIP AES failure preserves destination and cleans staging: " + method);
        }

        foreach (var encryptNames in new[] { false, true })
        {
            var path = Path.Combine(root, "password-headers-" + encryptNames + ".7z");
            const string password = "测试 pass word";
            await service.CreateArchiveAsync(path, new[] { source }, new("7z", Password: password, EncryptNames: encryptNames), control, progress, default);
            var index = await service.OpenAsync(path, password);
            await service.TestAsync(index, password, control, progress, default);
            check((await service.ReadPreviewAsync(path, Path.GetFileName(source), password)).SequenceEqual(File.ReadAllBytes(source)), "7Z Unicode and spaced password roundtrip; encrypted names=" + encryptNames);
            try
            {
                if (encryptNames) await service.OpenAsync(path, "wrong");
                else await service.TestAsync(index, "wrong", control, progress, default);
                check(false, "wrong 7Z password must fail");
            }
            catch (Exception ex) { check(ArchiveService.IsPasswordError(ex), "7Z wrong password is classified for retry; encrypted names=" + encryptNames); }
        }

        var mixed = Path.Combine(root, "mixed-encryption.zip");
        var plain = Path.Combine(root, "plain.txt");File.WriteAllText(plain, "public entry");
        await service.CreateArchiveAsync(mixed, new[] { source }, new("zip", Password: "test"), control, progress, default);
        var update = new ProcessStartInfo(Path.GetFullPath("AuraZip/Engine/x64/7z.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach(var arg in new[] { "a", mixed, plain }) update.ArgumentList.Add(arg);
        using(var process = Process.Start(update)!){await process.StandardOutput.ReadToEndAsync();await process.WaitForExitAsync();check(process.ExitCode == 0, "mixed encryption ZIP fixture is created");}
        var mixedIndex = await service.OpenAsync(mixed);
        check(mixedIndex.Items.Any(i => i.IsEncrypted) && mixedIndex.Items.Any(i => !i.IsDirectory && !i.IsEncrypted), "ZIP has both plain and encrypted entries");
        var mixedOutput = Path.Combine(root, "mixed-encryption-output");
        await service.ExtractAsync(mixedIndex, mixedOutput, new[] { "plain.txt" }, ConflictPolicy.Fail, null, control, progress, default);
        check(File.ReadAllText(Path.Combine(mixedOutput, "plain.txt")) == "public entry", "plain ZIP selections do not require the password of unselected encrypted entries");

        var encodedSource = Path.Combine(root, "中文.txt");File.WriteAllText(encodedSource, "legacy filename encoding");
        var encodedPath = Path.Combine(root, "encoded-encrypted.zip");
        var encodedStart = new ProcessStartInfo(Path.GetFullPath("AuraZip/Engine/x64/7z.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach(var arg in new[] { "a", "-tzip", "-ptest", "-mem=AES256", "-mcp=936", "-mcu=off", "-mcl=on", encodedPath, encodedSource }) encodedStart.ArgumentList.Add(arg);
        using(var process = Process.Start(encodedStart)!){await process.StandardOutput.ReadToEndAsync();await process.WaitForExitAsync();check(process.ExitCode == 0, "legacy GBK encrypted ZIP fixture is created");}
        var encodedIndex = await service.OpenAsync(encodedPath, codePage: 936);
        var encodedOutput = Path.Combine(root, "encoded-output");
        await service.ExtractAsync(encodedIndex, encodedOutput, null, ConflictPolicy.Fail, "test", control, progress, default);
        check(File.ReadAllText(Path.Combine(encodedOutput, "中文.txt")) == "legacy filename encoding", "encrypted ZIP extraction honors the selected GBK encoding");
        check((await service.ReadPreviewAsync(encodedPath, "中文.txt", "test", codePage: 936)).SequenceEqual(File.ReadAllBytes(encodedSource)), "encrypted ZIP preview honors the selected GBK encoding");

        var lower = Path.Combine(root, "a.txt");var upper = Path.Combine(root, "b.txt");
        File.WriteAllText(lower, "alpha");File.WriteAllText(upper, "beta");
        var caseZip = Path.Combine(root, "case-encrypted.zip");
        await service.CreateArchiveAsync(caseZip, new[] { lower, upper }, new("zip", Password: "test"), control, progress, default);
        var caseBytes = File.ReadAllBytes(caseZip);
        // Names are outside the AES authenticated payload; update local and central names.
        for(int p = 0; p < caseBytes.Length - 51; p++)
        {
            if(caseBytes[p] != 'P' || caseBytes[p+1] != 'K') continue;
            int nameOffset = caseBytes[p+2] == 3 && caseBytes[p+3] == 4 ? p+30 : caseBytes[p+2] == 1 && caseBytes[p+3] == 2 ? p+46 : -1;
            if(nameOffset > 0 && caseBytes.AsSpan(nameOffset,5).SequenceEqual("b.txt"u8)) caseBytes[nameOffset] = (byte)'A';
        }
        File.WriteAllBytes(caseZip, caseBytes);
        var caseIndex = await service.OpenAsync(caseZip);await service.TestAsync(caseIndex, "test", control, progress, default);
        var caseOutput = Path.Combine(root, "case-output");
        await service.ExtractAsync(caseIndex, caseOutput, null, ConflictPolicy.Rename, "test", control, progress, default);
        check(Directory.GetFiles(caseOutput).Select(File.ReadAllText).Order().SequenceEqual(new[] { "alpha", "beta" }), "encrypted case-colliding entries retain both payloads when renamed");
        check(System.Text.Encoding.UTF8.GetString(await service.ReadPreviewAsync(caseZip, "A.txt", "test")) == "beta", "encrypted preview selects exact filename case");
    }

    private sealed class Sink(Action<ArchiveProgress>? callback = null) : IProgress<ArchiveProgress>
    { public void Report(ArchiveProgress value) => callback?.Invoke(value); }
}
