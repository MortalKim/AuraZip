namespace AuraZip.Core;

/// <summary>A containing archive and the entry from which its child was opened.</summary>
public sealed record NestedArchiveParent(ArchiveIndex Index, string EntryKey, string? Password, FileHashes ExpectedHash);
public sealed record NestedArchiveResult(IReadOnlyList<ArchiveIndex> Archives, IReadOnlyList<FileHashes> Hashes);
public sealed class NestedArchiveRecoveryException(IReadOnlyList<string> directories, Exception inner)
    : IOException("Nested archive recovery files were retained: " + string.Join(Environment.NewLine, directories), inner)
{
    public IReadOnlyList<string> RecoveryDirectories { get; } = directories;
}

public sealed partial class ArchiveService
{
    public bool SupportsEditing(ArchiveIndex index)
    {
        try
        {
            return (index.FilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || index.FilePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
                || (CanCreateRar && index.FilePath.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)))
                && ArchiveFormats.Volumes(index.FilePath).Count == 1 && !index.Items.Any(i => i.IsLink);
        }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// Stage and verify an edit from the current archive through each containing archive.
    /// Parents are ordered from the nearest container to the outermost file. No original
    /// file is replaced until every staged archive has passed its integrity test.
    /// </summary>
    public Task<NestedArchiveResult> UpdateNestedAsync(ArchiveIndex index, string? password,
        IReadOnlyList<NestedArchiveParent> parents, Func<ArchiveIndex, Task> update,
        OperationControl control, IProgress<ArchiveProgress> progress, CancellationToken token) => Task.Run(async () =>
    {
        if (parents.Count == 0) throw new ArgumentException("A containing archive is required.");
        var originals = new[] { index }.Concat(parents.Select(p => p.Index)).ToArray();
        if (originals.Select(i => Path.GetFullPath(i.FilePath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != originals.Length)
            throw new ArgumentException("The nested archive chain contains a repeated file.");
        if (originals.Any(i => !SupportsEditing(i))) throw new NotSupportedException("Every containing archive must use an editable single-volume format.");
        foreach (var parent in parents)
        {
            if (!parent.Index.Items.Any(i => i.Key == parent.EntryKey && !i.IsDirectory && !i.IsLink))
                throw new InvalidDataException("The nested archive entry no longer exists.");
            SafeOutputPath(Path.GetTempPath(), parent.EntryKey);
        }
        var hashes = new[] { await ComputeHashesAsync(index.FilePath, token, control) }.Concat(parents.Select(p => p.ExpectedHash)).ToArray();
        for (int i = 0; i < originals.Length; i++)
        {
            if ((File.GetAttributes(originals[i].FilePath) & FileAttributes.ReadOnly) != 0) throw new IOException("A containing archive is read-only.");
            if (await ComputeHashesAsync(originals[i].FilePath, token, control) != hashes[i])
                throw new IOException("The archive changed outside AuraZip. Reopen it before saving.");
        }
        var workspaces = new List<TemporaryWorkspace>();
        var staged = new List<ArchiveIndex>();
        var stagedHashes = new List<FileHashes>();
        var locks = new List<FileStream>();
        bool retainRecovery = false;
        try
        {
            for (int i = 0; i < originals.Length; i++)
            {
                control.Wait(token);
                var original = originals[i];
                var workspace = new TemporaryWorkspace(original.Directory); workspaces.Add(workspace);
                var path = Path.Combine(workspace.Path, original.Name);
                await CopyFileAsync(original.FilePath, path, control, progress, token);
                if (await ComputeHashesAsync(path, token, control) != hashes[i]) throw new IOException("The archive changed outside AuraZip. Reopen it before saving.");
                var candidate = original with { FilePath = path };
                if (i == 0) await update(candidate);
                else
                {
                    var parent = parents[i - 1];
                    var key = parent.EntryKey;
                    var input = Path.Combine(workspace.Path, "replacement"); Directory.CreateDirectory(input);
                    var replacement = Path.Combine(input, key[(key.LastIndexOf('/') + 1)..]);
                    await CopyFileAsync(staged[i - 1].FilePath, replacement, control, progress, token);
                    var folder = key.Contains('/') ? key[..key.LastIndexOf('/')] : "";
                    await EditZipAsync(candidate, [replacement], null, null, null, control, progress, token, parent.Password, folder);
                }
                var keyPassword = i == 0 ? password : parents[i - 1].Password;
                var validated = await OpenAsync(path, keyPassword, token, original.CodePage);
                await TestAsync(validated, keyPassword, control, progress, token);
                staged.Add(validated);
                stagedHashes.Add(await ComputeHashesAsync(path, token, control));
            }
            // Check that every target can be opened for replacement before publishing
            // anything. Keep writers excluded through the final hash check and replace.
            foreach (var original in originals)
                locks.Add(new FileStream(original.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 128 * 1024, true));
            for (int i = 0; i < originals.Length; i++)
                if (await ComputeHashesAsync(locks[i], token, control) != hashes[i])
                    throw new IOException("The archive changed outside AuraZip. Reopen it before saving.");
            control.Wait(token);
            // The outermost archive is the persistent user file. Inner files are the
            // materialized browsing copies, refreshed after the outer commit succeeds.
            var committed = new List<int>();
            try
            {
                for (int i = originals.Length - 1; i >= 0; i--)
                {
                    File.Replace(staged[i].FilePath, originals[i].FilePath, Path.Combine(workspaces[i].Path, "original.bak"));
                    committed.Add(i);
                }
            }
            catch (Exception publicationError)
            {
                try
                {
                    // After replacement these handles refer to the backup files. Close
                    // them before using those backups as replacement sources.
                    foreach (var i in committed) locks[i].Dispose();
                    foreach (var i in committed.AsEnumerable().Reverse())
                    {
                        if (await ComputeHashesAsync(originals[i].FilePath) != stagedHashes[i]) throw new IOException("An archive changed during recovery.");
                        File.Replace(Path.Combine(workspaces[i].Path, "original.bak"), originals[i].FilePath, null);
                    }
                }
                catch (Exception recoveryError)
                {
                    retainRecovery = true;
                    throw new NestedArchiveRecoveryException(workspaces.Select(w => w.Path).ToArray(), new AggregateException(publicationError, recoveryError));
                }
                throw;
            }
            return new NestedArchiveResult(staged.Select((s, i) => s with { FilePath = originals[i].FilePath }).ToArray(), stagedHashes.ToArray());
        }
        finally
        {
            foreach (var file in locks) file.Dispose();
            if (!retainRecovery) foreach (var workspace in workspaces) workspace.Dispose();
        }
    }, token);

    private static async Task CopyFileAsync(string source, string destination, OperationControl control, IProgress<ArchiveProgress> progress, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
        var buffer = new byte[128 * 1024]; long done = 0;
        progress.Report(new(Path.GetFileName(source), 0, input.Length, 0, 1, ArchiveOperationStage.Preparing));
        while (true)
        {
            control.Wait(token);
            int read = await input.ReadAsync(buffer, token);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), token); done += read;
            progress.Report(new(Path.GetFileName(source), done, input.Length, 0, 1, ArchiveOperationStage.Preparing));
        }
    }
}
