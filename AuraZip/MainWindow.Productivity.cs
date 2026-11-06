using AuraZip.Core;
using AuraZip.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using static AuraZip.Services.Strings;

namespace AuraZip;

public sealed partial class MainWindow
{
    private sealed class ExternalEdit(ArchiveIndex archive, string key, string path, string? password, FileHashes fileHash, FileHashes archiveHash, ParentArchive[] parents)
    {
        public ArchiveIndex Archive = archive;
        public string Key = key, Path = path;
        public string? Password = password;
        public FileHashes FileHash = fileHash, ArchiveHash = archiveHash;
        public ParentArchive[] Parents = parents;
        public bool Deferred;
    }
    private readonly List<ExternalEdit> _externalEdits = new();
    private readonly DispatcherTimer _editTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _checkingEdits, _windowActive;
    private async void ExternalEdits_Click(object sender, RoutedEventArgs e) => await CheckExternalEditsAsync(includeDeferred: true);

    private void InitializeExternalEditing()
    {
        Activated += (_, e) => _windowActive = e.WindowActivationState != WindowActivationState.Deactivated;
        _editTimer.Tick += async (_, _) => { if (_windowActive) await CheckExternalEditsAsync(); };
        _editTimer.Start();
        Closed += (_, _) => _editTimer.Stop();
    }
    private async Task TrackExternalEditAsync(ArchiveIndex archive, ArchiveItem item, string path, string? password)
    {
        if (!CanEditArchive(archive) || _parents.Any(p => !CanEditArchive(p.Index))) return;
        _externalEdits.Add(new(archive, item.Key, path, password, await ArchiveService.ComputeHashesAsync(path), await ArchiveService.ComputeHashesAsync(archive.FilePath), _parents.ToArray()));
    }

    private async Task SaveArchiveChangeAsync(ArchiveIndex index, string? password, ParentArchive[] parents,
        Func<ArchiveIndex, Task> update, OperationControl control, IProgress<ArchiveProgress> progress, CancellationToken token)
    {
        if (parents.Length == 0) { await update(index); return; }
        var targets = parents.Select(p => new NestedArchiveParent(p.Index, p.EntryKey, p.Password, p.Hash)).ToArray();
        var result = await _archives.UpdateNestedAsync(index, password, targets, update, control, progress, token);
        var updated = result.Archives;
        for (int i = 0; i < parents.Length; i++)
        {
            parents[i].Index = updated[i + 1];
            parents[i].Hash = result.Hashes[i + 1];
        }
        for (int i = 0; i < updated.Count; i++)
        {
            var changed = updated[i];
            var matching = _externalEdits.Where(e => e.Archive.FilePath == changed.FilePath).ToArray();
            if (matching.Length == 0) continue;
            var hash = result.Hashes[i];
            foreach (var other in matching) { other.Archive = changed; other.ArchiveHash = hash; }
        }
    }
    private async Task CheckExternalEditsAsync(bool includeDeferred = false)
    {
        if (_checkingEdits || _busy || _dialogOpen || Root.XamlRoot is null) return;
        _checkingEdits = true;
        try
        {
            foreach (var edit in _externalEdits.ToArray())
            {
                if (_busy || _dialogOpen || !File.Exists(edit.Path)) continue;
                FileHashes changed;
                try { changed = await ArchiveService.ComputeHashesAsync(edit.Path); }
                catch (IOException) { continue; }
                if ((changed == edit.FileHash && !(includeDeferred && edit.Deferred)) || _busy || _dialogOpen) continue;
                var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = T("文件已修改"), Content = F("是否将 {0} 的修改写回压缩包？\n{1}", edit.Key, edit.Archive.FilePath), PrimaryButtonText = T("写回压缩包"), CloseButtonText = T("暂不写回") };
                _dialogOpen = true;
                ContentDialogResult answer;
                try { answer = await dialog.ShowAsync(); } finally { _dialogOpen = false; }
                // A declined version is not repeatedly prompted; a later save will be detected again.
                edit.FileHash = changed;
                edit.Deferred = answer != ContentDialogResult.Primary;
                if (edit.Deferred) continue;
                if (!File.Exists(edit.Archive.FilePath) || await ArchiveService.ComputeHashesAsync(edit.Archive.FilePath) != edit.ArchiveHash)
                {
                    _externalEdits.Remove(edit);
                    await ShowMessageAsync(T("写回压缩包"), F("原压缩包已发生变化，为避免覆盖其他修改，本次未写回。修改后的文件保留在：\n{0}", edit.Path));
                    continue;
                }
                var parent = edit.Key.Contains('/') ? edit.Key[..edit.Key.LastIndexOf('/')] : "";
                var succeeded = false;
                _busy = _dialogOpen = true; UpdateCommands();
                try
                {
                    succeeded = await new OperationDialog().RunAsync(Root.XamlRoot, T("正在更新压缩包"), edit.Archive.Name, edit.Archive.Directory,
                        (control, progress, token) => SaveArchiveChangeAsync(edit.Archive, edit.Password, edit.Parents,
                            candidate => _archives.EditZipAsync(candidate, new[] { edit.Path }, null, null, null, control, progress, token, edit.Password, parent), control, progress, token));
                }
                finally { _busy = _dialogOpen = false; UpdateCommands(); }
                if (!succeeded) { edit.Deferred = true; continue; }
                var updated = await _archives.OpenAsync(edit.Archive.FilePath, edit.Password, codePage: edit.Archive.CodePage);
                var hash = await ArchiveService.ComputeHashesAsync(updated.FilePath);
                foreach (var other in _externalEdits.Where(e => e.Archive.FilePath == updated.FilePath)) { other.Archive = updated; other.ArchiveHash = hash; }
                if (_index is not null && (_index.FilePath == updated.FilePath || edit.Parents.Any(p => p.Index.FilePath == _index.FilePath)))
                {
                    var folder = _currentPath;
                    await OpenArchiveAsync(_index.FilePath);
                    if (_folders.ContainsKey(folder)) Navigate(folder, false);
                }
            }
        }
        catch (Exception ex) { await ErrorAsync(T("写回压缩包"), ex); }
        finally { _checkingEdits = false; }
    }

    private async void Hashes_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _dialogOpen) return;
        try
        {
            var paths = new List<string>();
            if (_index is not null)
            {
                var keys = FileList.SelectedItems.Cast<ArchiveItem>().Select(i => i.Key).ToArray();
                if (keys.Length == 0) paths.Add(_index.FilePath);
                else
                {
                    var index = _index;
                    var root = await MaterializeAsync(keys, preserveFullPath: true);
                    if (root is null) return;
                    paths.AddRange(index.Scope(keys).Where(i => !i.IsDirectory && !i.IsLink).Select(i => ArchiveService.SafeOutputPath(root, i.Key)));
                }
            }
            else
            {
                var picker = new FileOpenPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, Handle);
                paths.AddRange((await picker.PickMultipleFilesAsync()).Select(f => f.Path));
            }
            if (paths.Count == 0) return;
            var results = new List<(string Name, FileHashes Hash)>();
            _busy = _dialogOpen = true; UpdateCommands();
            bool success;
            try
            {
                success = await new OperationDialog().RunAsync(Root.XamlRoot, T("计算文件哈希"), "SHA-256 / MD5", null, async (control, progress, token) =>
                {
                    for (int i = 0; i < paths.Count; i++)
                    {
                        results.Add((Path.GetFileName(paths[i]), await Task.Run(() => ArchiveService.ComputeHashesAsync(paths[i], token, control), token)));
                        progress.Report(new(Path.GetFileName(paths[i]), i + 1, paths.Count, i + 1, paths.Count));
                    }
                }, autoClose: true);
            }
            finally { _busy = _dialogOpen = false; UpdateCommands(); }
            if (!success) return;
            var report = string.Join("\n\n", results.Select(r => $"{r.Name}\nSHA-256: {r.Hash.SHA256}\nMD5: {r.Hash.MD5}"));
            var expected = new TextBox { Header = T("比对哈希（可选）"), PlaceholderText = T("粘贴 SHA-256 或 MD5"), TextWrapping = TextWrapping.Wrap };
            var comparison = new TextBlock { TextWrapping = TextWrapping.Wrap };
            expected.TextChanged += (_, _) =>
            {
                var value = expected.Text.Trim();
                var matches = results.Where(r => string.Equals(value, r.Hash.SHA256, StringComparison.OrdinalIgnoreCase) || string.Equals(value, r.Hash.MD5, StringComparison.OrdinalIgnoreCase)).Select(r => r.Name).ToArray();
                comparison.Text = value.Length == 0 ? "" : matches.Length > 0 ? T("匹配：") + string.Join(", ", matches) : T("哈希不匹配");
            };
            var panel = new StackPanel { Spacing = 12, MaxWidth = 500 };
            panel.Children.Add(new TextBox { Text = report, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 260 });
            panel.Children.Add(expected); panel.Children.Add(comparison);
            var copy = new Button { Content = T("复制结果") };
            copy.Click += (_, _) => { var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(report); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data); };
            panel.Children.Add(copy);
            _dialogOpen = true;
            try { await new ContentDialog { XamlRoot = Root.XamlRoot, Title = T("文件哈希"), Content = panel, CloseButtonText = T("关闭") }.ShowAsync(); }
            finally { _dialogOpen = false; }
        }
        catch (Exception ex) { await ErrorAsync(T("文件哈希"), ex); }
    }

    private async void Comment_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _dialogOpen || _index is null) return;
        if (!_index.FilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !EditableZip)
        { await ShowMessageAsync(T("压缩包注释"), T("注释编辑支持单卷 ZIP 格式。")); return; }
        try
        {
            var path = _index.FilePath;
            var field = new TextBox { Text = await Task.Run(() => ArchiveService.ReadZipComment(path)), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 280, MinHeight = 140, MaxHeight = 320 };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(field); panel.Children.Add(error);
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = T("压缩包注释"), Content = panel, PrimaryButtonText = T("保存"), CloseButtonText = T("取消") };
            dialog.PrimaryButtonClick += (_, args) => { if (Encoding.UTF8.GetByteCount(field.Text) > ushort.MaxValue) { args.Cancel = true; error.Text = T("注释不能超过 65535 个 UTF-8 字节。"); } };
            _dialogOpen = true;
            ContentDialogResult result;
            try { result = await dialog.ShowAsync(); } finally { _dialogOpen = false; }
            if (result != ContentDialogResult.Primary) return;
            _busy = true; UpdateCommands();
            var comment = field.Text;
            try
            {
                using var control = new OperationControl();
                await SaveArchiveChangeAsync(_index, _password, _parents.ToArray(), candidate => ArchiveService.SetZipCommentAsync(candidate.FilePath, comment),
                    control, new Progress<ArchiveProgress>(), default);
            }
            finally { _busy = false; UpdateCommands(); }
            StatusText.Text = T("注释已保存");
        }
        catch (Exception ex) { await ErrorAsync(T("压缩包注释"), ex); }
    }
}
