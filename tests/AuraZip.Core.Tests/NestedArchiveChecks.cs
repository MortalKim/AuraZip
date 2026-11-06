using AuraZip.Core;
using System.IO.Compression;
using System.Text;

internal static class NestedArchiveChecks
{
    private sealed class ProgressSink(Action<ArchiveProgress>? report = null) : IProgress<ArchiveProgress> { public void Report(ArchiveProgress value) => report?.Invoke(value); }
    private sealed record Fixture(ArchiveIndex Leaf, string? Password, NestedArchiveParent[] Parents);

    public static async Task Run(ArchiveService service, string root, Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        using var control = new OperationControl(); var progress = new ProgressSink();
        async Task<Fixture> Create(string name, string[] formats, bool encrypted = false)
        {
            var directory = Path.Combine(root, "nested", name); Directory.CreateDirectory(directory);
            var text = Path.Combine(directory, "original.txt"); await File.WriteAllTextAsync(text, "原始内容\nunchanged payload", new UTF8Encoding(false));
            string? Password(int n) => encrypted ? "Password-layer-" + n : null;
            var path = Path.Combine(directory, "leaf." + formats[0]);
            await service.CreateArchiveAsync(path, [text], new(formats[0], Password: Password(0)), control, progress, default);
            if (formats[0] == "zip") await ArchiveService.SetZipCommentAsync(path, "inner comment 中文");
            var leaf = await service.OpenAsync(path, Password(0)); var parents = new List<NestedArchiveParent>();
            for (int i = 1; i < formats.Length; i++)
            {
                var source = Path.Combine(directory, "input" + i); Directory.CreateDirectory(Path.Combine(source, "目录"));
                var entry = "目录/嵌套 renamed." + formats[i - 1];
                File.Copy(path, Path.Combine(source, entry));
                await File.WriteAllTextAsync(Path.Combine(source, "sibling.txt"), "keep sibling " + i);
                path = Path.Combine(directory, "parent" + i + "." + formats[i]);
                await service.CreateArchiveAsync(path, Directory.GetFileSystemEntries(source), new(formats[i], Password: Password(i)), control, progress, default);
                if (formats[i] == "zip") await ArchiveService.SetZipCommentAsync(path, "parent comment " + i);
                var index = await service.OpenAsync(path, Password(i));
                parents.Add(new(index, entry, Password(i), await ArchiveService.ComputeHashesAsync(path)));
            }
            return new(leaf, Password(0), parents.ToArray());
        }
        async Task<bool> Preserved(Fixture fixture, FileHashes[] before)
        {
            var paths = new[] { fixture.Leaf.FilePath }.Concat(fixture.Parents.Select(p => p.Index.FilePath)).ToArray();
            for (int i = 0; i < paths.Length; i++) if (await ArchiveService.ComputeHashesAsync(paths[i]) != before[i]) return false;
            return true;
        }
        async Task<FileHashes[]> Hashes(Fixture f)
        {
            var values = new List<FileHashes>();
            foreach (var path in new[] { f.Leaf.FilePath }.Concat(f.Parents.Select(p => p.Index.FilePath))) values.Add(await ArchiveService.ComputeHashesAsync(path));
            return values.ToArray();
        }
        Task Rename(ArchiveIndex candidate, string? password = null) => service.EditZipAsync(candidate, null, null, "original.txt", "updated.txt", control, progress, default, password);

        var chains = new List<string[]> { new[] { "zip", "zip", "zip" }, new[] { "zip", "7z", "zip" }, new[] { "7z", "zip", "7z" }, new[] { "7z", "7z", "7z" } };
        if (service.CanCreateRar) chains.AddRange([new[] { "zip", "rar", "7z" }, new[] { "rar", "zip", "rar" }]);
        foreach (var formats in chains)
        foreach (var encrypted in new[] { false, true })
        {
            var label = string.Join('-', formats) + (encrypted ? "-encrypted" : "-plain");
            var fixture = await Create(label, formats, encrypted);
            var saved = await service.UpdateNestedAsync(fixture.Leaf, fixture.Password, fixture.Parents, candidate => Rename(candidate, fixture.Password), control, progress, default);
            var result = saved.Archives;
            check(result.Count == 3 && result[0].Items.Any(i => i.Key == "updated.txt") && !result[0].Items.Any(i => i.Key == "original.txt"), "three-layer edit refreshes the current index: " + label);
            bool hashesMatch = saved.Hashes.Count == result.Count;
            for (int i = 0; i < result.Count; i++) hashesMatch &= await ArchiveService.ComputeHashesAsync(result[i].FilePath) == saved.Hashes[i];
            check(hashesMatch, "nested conflict snapshots match the committed file bytes: " + label);
            var current = result[^1];
            for (int i = fixture.Parents.Length - 1; i >= 0; i--)
            {
                check(current.Items.Any(e => e.Key == "sibling.txt"), "nested save preserves sibling entries at layer " + i + ": " + label);
                if (formats[i + 1] == "zip") check(ArchiveService.ReadZipComment(current.FilePath) == "parent comment " + (i + 1), "nested save preserves containing ZIP comments: " + label + " layer=" + i);
                var materialized = Path.Combine(root, "nested", label, "verify" + i); Directory.CreateDirectory(materialized);
                await service.ExtractAsync(current, materialized, [fixture.Parents[i].EntryKey], ConflictPolicy.Fail, fixture.Parents[i].Password, control, progress, default);
                current = await service.OpenAsync(Path.Combine(materialized, fixture.Parents[i].EntryKey), i == 0 ? fixture.Password : fixture.Parents[i - 1].Password);
            }
            check(current.Items.Any(e => e.Key == "updated.txt") && !current.Items.Any(e => e.Key == "original.txt"), "reopening the outermost file contains the edited leaf: " + label);
            check(Encoding.UTF8.GetString(await service.ReadPreviewAsync(current.FilePath, "updated.txt", fixture.Password)).Contains("原始内容"), "nested payload bytes survive every layer: " + label);
            if (formats[0] == "zip") check(ArchiveService.ReadZipComment(current.FilePath) == "inner comment 中文", "nested leaf ZIP comment survives: " + label);
            if (encrypted) check(result.All(i => i.Items.Any(e => e.IsEncrypted)), "all nested layers retain encryption: " + label);
            var refreshedParents = fixture.Parents.Select((p, i) => p with { Index = result[i + 1] }).ToArray();
            for (int i = 0; i < refreshedParents.Length; i++) refreshedParents[i] = refreshedParents[i] with { ExpectedHash = await ArchiveService.ComputeHashesAsync(refreshedParents[i].Index.FilePath) };
            if (formats[0] != "rar")
            {
                await service.UpdateNestedAsync(result[0], fixture.Password, refreshedParents,
                    candidate => service.EditZipAsync(candidate, null, ["updated.txt"], null, null, control, progress, default, fixture.Password), control, progress, default);
                check(!(await service.OpenAsync(fixture.Leaf.FilePath, fixture.Password)).Items.Any(e => !e.IsDirectory), "a second nested edit can remove the final file: " + label);
            }
            else
            {
                await service.UpdateNestedAsync(result[0], fixture.Password, refreshedParents,
                    candidate => service.EditZipAsync(candidate, null, null, "updated.txt", "final.txt", control, progress, default, fixture.Password), control, progress, default);
                check((await service.OpenAsync(fixture.Leaf.FilePath, fixture.Password)).Items.Any(e => e.Key == "final.txt"), "a second nested RAR edit can be saved: " + label);
            }
        }

        var failed = await Create("failure-protection", ["zip", "7z", "zip"], true); var before = await Hashes(failed);
        var wrongPassword = failed.Parents.ToArray(); wrongPassword[^1] = wrongPassword[^1] with { Password = "wrong-password" };
        await reject(() => service.UpdateNestedAsync(failed.Leaf, failed.Password, wrongPassword, c => Rename(c, failed.Password), control, progress, default), "a wrong outer password rejects the nested transaction");
        check(await Preserved(failed, before), "a late password failure preserves every original archive");
        await reject(() => service.UpdateNestedAsync(failed.Leaf, failed.Password, failed.Parents, async c => { await Rename(c, failed.Password); throw new IOException("injected failure"); }, control, progress, default), "an edit failure stops nested publication");
        check(await Preserved(failed, before), "an edit failure preserves every original archive");
        await reject(() => service.UpdateNestedAsync(failed.Leaf, failed.Password, failed.Parents, c => File.WriteAllBytesAsync(c.FilePath, [1, 2, 3]), control, progress, default), "a corrupt staged leaf fails integrity validation");
        check(await Preserved(failed, before), "corrupt staging never replaces any original archive");
        using (var cancellation = new CancellationTokenSource())
        {
            await reject(() => service.UpdateNestedAsync(failed.Leaf, failed.Password, failed.Parents, async c => { await Rename(c, failed.Password); cancellation.Cancel(); }, control, progress, cancellation.Token), "cancellation stops a nested edit before publication");
            check(await Preserved(failed, before), "nested cancellation preserves every original archive");
        }
        var missing = failed.Parents.ToArray(); missing[0] = missing[0] with { EntryKey = "missing.zip" };
        await reject(() => service.UpdateNestedAsync(failed.Leaf, failed.Password, missing, c => Rename(c, failed.Password), control, progress, default), "a missing nested entry is rejected before editing");
        check(await Preserved(failed, before), "missing-entry rejection preserves every original archive");
        var outer = failed.Parents[^1].Index.FilePath;
        File.SetAttributes(outer, File.GetAttributes(outer) | FileAttributes.ReadOnly);
        try { await reject(() => service.UpdateNestedAsync(failed.Leaf, failed.Password, failed.Parents, c => Rename(c, failed.Password), control, progress, default), "a read-only outer archive is rejected before editing"); }
        finally { File.SetAttributes(outer, FileAttributes.Normal); }
        check(await Preserved(failed, before), "read-only rejection preserves every original archive");
        using (var locked = new FileStream(outer, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            await reject(() => service.UpdateNestedAsync(failed.Leaf, failed.Password, failed.Parents, c => Rename(c, failed.Password), control, progress, default), "an outer archive locked against replacement cannot be published");
        check(await Preserved(failed, before), "a final replacement failure preserves all original archives");
        using (var locked = new FileStream(failed.Leaf.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            await reject(() => service.UpdateNestedAsync(failed.Leaf, failed.Password, failed.Parents, c => Rename(c, failed.Password), control, progress, default), "a locked inner browsing copy rejects publication after outer replacement");
        check(await Preserved(failed, before), "a late inner replacement failure rolls every containing archive back");
        var cyclic = failed.Parents.Concat([new NestedArchiveParent(failed.Leaf, "original.txt", failed.Password, before[0])]).ToArray();
        await reject(() => service.UpdateNestedAsync(failed.Leaf, failed.Password, cyclic, c => Rename(c, failed.Password), control, progress, default), "a repeated archive path cannot form a nested edit cycle");
        check(await Preserved(failed, before), "a cyclic chain rejection leaves all original files unchanged");
        var unsupported = await Create("unsupported-container", ["zip", "tar"]); var unsupportedBefore = await Hashes(unsupported);
        bool updateCalled = false;
        await reject(() => service.UpdateNestedAsync(unsupported.Leaf, null, unsupported.Parents, c => { updateCalled = true; return Rename(c); }, control, progress, default), "a read-only archive format in the parent chain rejects editing");
        check(!updateCalled && await Preserved(unsupported, unsupportedBefore), "an unsupported containing format rejects the edit before changing the leaf");
        await reject(() => service.UpdateNestedAsync(failed.Leaf, failed.Password, failed.Parents, async c => { await Rename(c, failed.Password); await ArchiveService.SetZipCommentAsync(outer, "external edit"); }, control, progress, default), "an external outer edit stops nested publication");
        check(ArchiveService.ReadZipComment(outer) == "external edit" && await ArchiveService.ComputeHashesAsync(failed.Leaf.FilePath) == before[0] && await ArchiveService.ComputeHashesAsync(failed.Parents[0].Index.FilePath) == before[1], "conflict detection preserves the external edit and unchanged inner archives");
        foreach (var cancelWhilePaused in new[] { false, true })
        {
            var pausedFixture = await Create("pause-" + cancelWhilePaused, ["zip", "zip"]);
            var pausedBefore = await Hashes(pausedFixture);
            using var pauseControl = new OperationControl(); using var pauseCancellation = new CancellationTokenSource();
            var pausedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); int triggered = 0;
            var pauseProgress = new ProgressSink(p =>
            {
                if (p.Stage == ArchiveOperationStage.Preparing && p.BytesDone > 0 && Interlocked.CompareExchange(ref triggered, 1, 0) == 0)
                { pauseControl.TogglePause(); pausedSignal.TrySetResult(true); }
            });
            var pending = service.UpdateNestedAsync(pausedFixture.Leaf, null, pausedFixture.Parents, c => Rename(c), pauseControl, pauseProgress, pauseCancellation.Token);
            try
            {
                check(await Task.WhenAny(pausedSignal.Task, Task.Delay(10000)) == pausedSignal.Task, "nested preparation reaches a pausable copy operation: cancel=" + cancelWhilePaused);
                await Task.Delay(100);
                check(!pending.IsCompleted && await Preserved(pausedFixture, pausedBefore), "pausing nested preparation prevents publication: cancel=" + cancelWhilePaused);
                if (cancelWhilePaused)
                {
                    pauseCancellation.Cancel();
                    await reject(async () => await pending, "cancellation wakes a paused nested copy");
                    check(await Preserved(pausedFixture, pausedBefore), "cancelling a paused nested copy preserves every original file");
                }
                else
                {
                    pauseControl.TogglePause(); var resumed = await pending;
                    check(resumed.Archives[0].Items.Any(e => e.Key == "updated.txt"), "a paused nested transaction resumes and saves successfully");
                }
            }
            finally { if (pauseControl.IsPaused) pauseControl.TogglePause(); }
        }
        check(!Directory.EnumerateDirectories(Path.Combine(root, "nested"), ".aurazip-*.tmp", SearchOption.AllDirectories).Any(), "successful and failed nested transactions clean their temporary workspaces");
    }
}
