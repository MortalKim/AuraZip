using AuraZip.Core;
using AuraZip.Services;
using AuraZip.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using static AuraZip.Services.Strings;

namespace AuraZip;

public sealed partial class MainWindow
{
    private int? _codePage;
    private bool _searchAll, _batchMode;
    private CancellationTokenSource? _opening;
    private sealed class ParentArchive(ArchiveIndex index, string? password, string folder, string entryKey, FileHashes hash)
    {
        public ArchiveIndex Index = index;
        public string? Password = password;
        public string Folder = folder;
        public string EntryKey = entryKey;
        public FileHashes Hash = hash;
    }
    private readonly Stack<ParentArchive> _parents = new();
    private readonly List<TemporaryWorkspace> _temporary = new();
    private readonly List<string> _batchResults = new();
    private (string Directory, bool PerArchive, bool Smart, bool Folder, ConflictPolicy Policy, bool Open)? _batchPlan;
    private string SelectionRoot => _searchAll && _query.Length > 0 ? "" : _currentPath;
    private bool CanEditArchive(ArchiveIndex index) => _archives.SupportsEditing(index);
    private bool EditableZip => _index is not null && CanEditArchive(_index) && _parents.All(p => CanEditArchive(p.Index));

    private async Task CreateArchiveDialogAsync(IReadOnlyList<string>? initial = null)
    {
        if (_busy || _dialogOpen) return;
        var sources = new List<string>(initial ?? Array.Empty<string>());
        var list = new ListView { MaxHeight = 140, SelectionMode = ListViewSelectionMode.None };
        var destination = new TextBox { Header = T("压缩包路径"), Text = sources.Count == 0 ? "" : Path.Combine(Path.GetDirectoryName(sources[0])!, Path.GetFileNameWithoutExtension(sources[0]) + ".zip") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(destination, "CompressionDestination");
        var formats = new[] { "zip", "7z", "tar", "tar.gz", "tar.bz2", "tar.xz", "gz", "bz2", "xz", "rar" };
        var format = new ComboBox { Header = T("格式"), ItemsSource = formats.Select(f => f.ToUpperInvariant()).ToArray(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        bool SingleStream() => formats[format.SelectedIndex] is "gz" or "bz2" or "xz";
        bool HasPassword() => formats[format.SelectedIndex] is "zip" or "7z" or "rar";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(format, "CompressionFormat");
        var formatHint = new TextBlock { Text = T("GZ、BZ2、XZ 仅支持一个文件，不支持密码和分卷。多个文件或文件夹请使用 TAR 系列。"), TextWrapping = TextWrapping.Wrap, MaxWidth = 440, FontSize = 12, Visibility = Visibility.Collapsed };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(formatHint, "CompressionFormatHint");
        var suggestedDestination = destination.Text;
        var level = new ComboBox { Header = T("压缩级别"), ItemsSource = new[] { T("仅存储"), T("快速"), T("标准"), T("最大"), T("极限") }, SelectedIndex = 2, HorizontalAlignment = HorizontalAlignment.Stretch };
        var password = new PasswordBox { Header = T("密码（如有）"), PlaceholderText = T("无需密码时留空") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(password, "CompressionPassword");
        var confirm = new PasswordBox { Header = T("确认密码") };
        var names = new CheckBox { Content = T("加密文件名（7Z / RAR）"), IsChecked = true, IsEnabled = false };
        var volume = new ComboBox { Header = T("分卷大小"), ItemsSource = new[] { T("不分卷"), "10 MB", "100 MB", "500 MB", "1 GB", "2 GB", T("自定义") }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(volume, "CompressionVolume");
        var volumeSize = new TextBox { Text = "100", PlaceholderText = T("大小"), HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(volumeSize, "CompressionVolumeSize");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(volumeSize, T("自定义分卷大小"));
        var volumeUnit = new ComboBox { ItemsSource = new[] { "MB", "GB" }, SelectedIndex = 0, Width = 100 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(volumeUnit, "CompressionVolumeUnit");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(volumeUnit, T("单位"));
        var customVolume = new Grid { ColumnSpacing = 8, Visibility = Visibility.Collapsed };
        customVolume.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        customVolume.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(volumeUnit, 1);
        customVolume.Children.Add(volumeSize); customVolume.Children.Add(volumeUnit);
        volume.SelectionChanged += (_, _) => customVolume.Visibility = volume.SelectedIndex == 6 ? Visibility.Visible : Visibility.Collapsed;
        var addFiles = new Button { Content = T("添加文件…") };
        var addFolder = new Button { Content = T("添加文件夹…") };
        var select = new Button { Content = T("选择"), IsEnabled = sources.Count > 0 };
        var remove = new Button { Content = T("移除所选"), IsEnabled = false, Visibility = Visibility.Collapsed };
        list.SelectionChanged += (_, _) => remove.IsEnabled = list.SelectedItems.Count > 0;
        var browse = new Button { Content = T("选择保存目录…") };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; buttons.Children.Add(addFiles); buttons.Children.Add(addFolder); buttons.Children.Add(select); buttons.Children.Add(remove);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 440, FontSize = 12 };
        var panel = new StackPanel { Spacing = 10, MinWidth = Math.Min(420, Math.Max(220, Root.ActualWidth - 100)), MaxWidth = 460, AllowDrop = true };
        var sourceHint = new TextBlock { Text = T("列表中的所有项目都会参与压缩。"), FontSize = 12, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextWrapping = TextWrapping.Wrap };
        foreach (var child in new UIElement[] { new TextBlock { Text = T("文件和文件夹"), FontSize = 12 }, list, sourceHint, buttons, destination, browse, format, formatHint, level, password, confirm, names, volume, customVolume, error }) panel.Children.Add(child);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = T("创建压缩包"), Content = new ScrollViewer { Content = panel, MaxHeight = Math.Max(240, Math.Min(650, Root.ActualHeight - 180)) }, PrimaryButtonText = T("压缩"), CloseButtonText = T("取消"), DefaultButton = ContentDialogButton.Primary };
        void SetSelecting(bool enabled)
        {
            if (list.SelectionMode != ListViewSelectionMode.None) list.SelectedItems.Clear();
            list.SelectionMode = enabled ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
            select.Content = T(enabled ? "完成选择" : "选择");
            remove.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        }
        void Refresh()
        {
            list.Items.Clear();
            foreach (var path in sources)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = new TextBlock { Text = path, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(label, path);
                var dismiss = new Button { Content = new FontIcon { Glyph = "\uE711", FontSize = 12 }, Width = 28, Height = 28, Padding = new Thickness(0), Style = (Style)Application.Current.Resources["SubtleButtonStyle"] };
                ToolTipService.SetToolTip(dismiss, T("移除"));
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dismiss, T("移除") + " " + path);
                dismiss.Click += (_, _) => { sources.Remove(path); Refresh(); };
                Grid.SetColumn(dismiss, 1);
                row.Children.Add(label); row.Children.Add(dismiss);
                var item = new ListViewItem { Content = row, Tag = path, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, path);
                list.Items.Add(item);
            }
            select.IsEnabled = sources.Count > 0;
            if (sources.Count == 0) SetSelecting(false);
            if (string.IsNullOrWhiteSpace(destination.Text) && sources.Count > 0) destination.Text = suggestedDestination = SuggestedDestination();
            dialog.IsPrimaryButtonEnabled = sources.Count > 0 && (!SingleStream() || (sources.Count == 1 && File.Exists(sources[0]))) && (formats[format.SelectedIndex] != "rar" || _archives.CanCreateRar);
        }
        string SuggestedDestination() => sources.Count == 0 ? "" : Path.Combine(Path.GetDirectoryName(sources[0])!, (SingleStream() ? Path.GetFileName(sources[0]) : Path.GetFileNameWithoutExtension(sources[0])) + "." + formats[format.SelectedIndex]);
        void Add(IEnumerable<string> paths) { foreach (var path in paths) if (!sources.Contains(path, StringComparer.OrdinalIgnoreCase)) sources.Add(path); Refresh(); }
        addFiles.Click += async (_, _) => { var picker = new FileOpenPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, Handle); Add((await picker.PickMultipleFilesAsync()).Select(f => f.Path)); };
        addFolder.Click += async (_, _) => { var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, Handle); var folder = await picker.PickSingleFolderAsync(); if (folder is not null) Add([folder.Path]); };
        select.Click += (_, _) => SetSelecting(list.SelectionMode == ListViewSelectionMode.None);
        remove.Click += (_, _) => { foreach (var item in list.SelectedItems.Cast<ListViewItem>().ToList()) sources.Remove((string)item.Tag); Refresh(); };
        browse.Click += async (_, _) => { var folder = await PickFolderAsync(); if (folder is not null) destination.Text = Path.Combine(folder, string.IsNullOrEmpty(Path.GetFileName(destination.Text)) ? "Archive.zip" : Path.GetFileName(destination.Text)); };
        format.SelectionChanged += (_, e) =>
        {
            names.IsEnabled = formats[format.SelectedIndex] is "7z" or "rar";
            password.IsEnabled = confirm.IsEnabled = HasPassword();
            level.IsEnabled = format.SelectedIndex != 2;
            bool single = SingleStream(); bool rar = formats[format.SelectedIndex] == "rar";
            formatHint.Visibility = single || rar ? Visibility.Visible : Visibility.Collapsed;
            formatHint.Text = rar ? T(_archives.CanCreateRar ? "RAR 创建和修改使用本机 WinRAR 编码器。" : "RAR 创建和修改需要本机 WinRAR 编码器。请在设置中选择 Rar.exe。") : T("GZ、BZ2、XZ 仅支持一个文件，不支持密码和分卷。多个文件或文件夹请使用 TAR 系列。");
            volume.IsEnabled = !single;
            if (single) volume.SelectedIndex = 0;
            if (!string.IsNullOrEmpty(destination.Text))
            {
                var previous = e.RemovedItems.Count > 0 ? e.RemovedItems[0].ToString()!.ToLowerInvariant() : "zip";
                var path = destination.Text;
                destination.Text = path == suggestedDestination ? SuggestedDestination() : (path.EndsWith("." + previous, StringComparison.OrdinalIgnoreCase) ? path[..^(previous.Length + 1)] : Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path))) + "." + formats[format.SelectedIndex];
                suggestedDestination = SuggestedDestination();
            }
            Refresh();
        };
        panel.DragOver += (_, e) => { e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None; e.Handled = true; };
        panel.Drop += async (_, e) => { e.Handled = true; if (e.DataView.Contains(StandardDataFormats.StorageItems)) Add((await e.DataView.GetStorageItemsAsync()).Select(i => i.Path)); };
        long volumeBytes = 0;
        bool TryGetVolumeBytes()
        {
            if (volume.SelectedIndex != 6)
            {
                volumeBytes = new long[] { 0, 10L << 20, 100L << 20, 500L << 20, 1L << 30, 2L << 30 }[volume.SelectedIndex];
                return true;
            }
            long unit = volumeUnit.SelectedIndex == 1 ? 1L << 30 : 1L << 20;
            if (!decimal.TryParse(volumeSize.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var size)
                || size <= 0 || size > (decimal)long.MaxValue / unit || size * unit < 1) return false;
            volumeBytes = (long)(size * unit);
            return true;
        }
        dialog.PrimaryButtonClick += (_, e) =>
        {
            if (!Path.IsPathFullyQualified(destination.Text) || (HasPassword() && password.Password != confirm.Password) || sources.Count == 0)
            { e.Cancel = true; error.Text = T("请选择文件，填写完整路径，并确认两次密码一致。"); }
            else if (format.SelectedIndex == 0 && (password.Password.Length > 99 || password.Password.Any(c => c < 32 || c > 126)))
            { e.Cancel = true; error.Text = T("ZIP 密码最多 99 个英文、数字或符号字符。中文密码请选择 7Z 格式。"); }
            else if (formats[format.SelectedIndex] == "rar" && password.Password.Length > 127)
            { e.Cancel = true; error.Text = T("RAR passwords must contain at most 127 characters without line breaks."); }
            else if (File.Exists(destination.Text) || Directory.Exists(destination.Text) || File.Exists(destination.Text + ".001"))
            { e.Cancel = true; error.Text = T("目标压缩包已存在，请更换名称。"); }
            else if (!TryGetVolumeBytes())
            { e.Cancel = true; error.Text = T("请输入有效的分卷大小，数值须大于 0 且不能超出支持范围。"); volumeSize.Focus(FocusState.Programmatic); }
        };
        Refresh(); _dialogOpen = true;
        ContentDialogResult answer;
        try { answer = await dialog.ShowAsync(); } finally { _dialogOpen = false; }
        if (answer != ContentDialogResult.Primary) return;
        var options = new CompressionOptions(formats[format.SelectedIndex], new[] { 0, 1, 5, 7, 9 }[level.SelectedIndex], HasPassword() ? password.Password : null, names.IsChecked == true, volumeBytes);
        CompressionResult? result = null;
        _busy = _dialogOpen = true; UpdateCommands();
        bool success;
        try
        {
            success = await new OperationDialog().RunAsync(Root.XamlRoot, T("正在压缩"), Path.GetFileName(destination.Text), Path.GetDirectoryName(destination.Text),
                async (control, progress, token) => result = await _archives.CreateArchiveAsync(destination.Text, sources, options, control, progress, token),
                completionSummary: () => result is null ? "" : F("原始大小：{0}\n压缩后：{1}\n压缩率：{2:P1}", ArchiveItem.FormatSize(result.OriginalBytes), ArchiveItem.FormatSize(result.PackedBytes), result.OriginalBytes > 0 ? (double)result.PackedBytes / result.OriginalBytes : 0));
        }
        finally { _busy = _dialogOpen = false; UpdateCommands(); }
        if (success && result is not null) { _codePage = null; _parents.Clear(); await OpenArchiveAsync(result.Files[0]); }
    }

    private async Task<string?> MaterializeAsync(IReadOnlyCollection<string> keys, bool preserveFullPath = false, bool showOverlay = true)
    {
        if (_index is null) return null;
        if (_index.Scope(keys).Any(i => i.IsEncrypted) && string.IsNullOrEmpty(_password)) { _password = await AskPasswordAsync(); if (_password is null) return null; }
        var workspace = new TemporaryWorkspace();
        try
        {
            _busy = true; _opening = new CancellationTokenSource(); if (showOverlay) BusyOverlay.Visibility = Visibility.Visible; UpdateCommands();
            using var control = new OperationControl();
            await _archives.ExtractAsync(_index, workspace.Path, keys, ConflictPolicy.Fail, _password, control, new Progress<ArchiveProgress>(p => StatusText.Text = p.FileName), _opening.Token, preserveFullPath ? "" : SelectionRoot);
            _temporary.Add(workspace);
            StatusText.Text = T("就绪");
            return workspace.Path;
        }
        catch (Exception ex) { if (ArchiveService.IsPasswordError(ex)) _password = null; workspace.Dispose(); throw; }
        finally { _opening?.Dispose(); _opening = null; BusyOverlay.Visibility = Visibility.Collapsed; _busy = false; UpdateCommands(); }
    }

    private Task OpenSelectedAsync() => FileList.SelectedItems.Count == 1 && FileList.SelectedItem is ArchiveItem item
        ? OpenItemAsync(item) : Task.CompletedTask;

    private async Task OpenItemAsync(ArchiveItem item)
    {
        if (_busy || _dialogOpen || _index is null || !VisibleItems.Contains(item)) return;
        if (item.IsDirectory) { Navigate(item.Key); return; }
        try
        {
            var parentIndex = _index;
            var parentFolder = _currentPath;
            var nested = ArchiveFormats.Supported(item.Name);
            // Keep the source stable while extracting and hashing the child. One hash
            // pass is enough when writers and replacement are denied for this interval.
            using var parentRead = nested ? File.OpenRead(parentIndex.FilePath) : null;
            var root = await MaterializeAsync([item.Key], preserveFullPath: true);
            if (root is null) return;
            var parentPassword = _password;
            var parentHash = nested ? await HashArchiveForNavigationAsync(parentIndex.FilePath) : null;
            var filePath = ArchiveService.SafeOutputPath(root, item.Key);
            if (nested)
            {
                _codePage = null;
                await OpenArchiveAsync(filePath);
                if (_index?.FilePath == filePath) { _parents.Push(new(parentIndex, parentPassword, parentFolder, item.Key, parentHash!)); RefreshPath(); UpdateCommands(); }
                else _codePage = parentIndex.CodePage;
            }
            else
            {
                await TrackExternalEditAsync(parentIndex, item, filePath, parentPassword);
                if (!await Windows.System.Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(filePath)))
                    await ShowMessageAsync(T("打开文件"), T("没有可用于打开此文件的应用。"));
            }
        }
        catch (OperationCanceledException) { StatusText.Text = T("已取消"); }
        catch (Exception ex) { await ErrorAsync(T("打开文件"), ex); }
    }

    private async Task<FileHashes> HashArchiveForNavigationAsync(string path)
    {
        _busy = true; _opening = new CancellationTokenSource(); BusyOverlay.Visibility = Visibility.Visible; UpdateCommands();
        var token = _opening.Token;
        try { return await Task.Run(() => ArchiveService.ComputeHashesAsync(path, token), token); }
        finally { _opening.Dispose(); _opening = null; _busy = false; BusyOverlay.Visibility = Visibility.Collapsed; UpdateCommands(); }
    }

    private void ReturnToParentArchive()
    {
        if (!_parents.TryPop(out var parent)) return;
        _index = parent.Index; _password = parent.Password; _codePage = _index.CodePage; _currentPath = parent.Folder; _query = ""; SearchBox.Text = ""; _history.Clear();
        WindowTitle.Text = Title = _index.Name + " — AuraZip";
        BuildTree(); RefreshPath(); RefreshLanguage();
    }

    private async Task<IReadOnlyList<IStorageItem>> ExportSelectionAsync(IReadOnlyCollection<string>? selectedKeys = null, bool showOverlay = true)
    {
        if (_index is null) return Array.Empty<IStorageItem>();
        var keys = selectedKeys ?? FileList.SelectedItems.Cast<ArchiveItem>().Select(i => i.Key).ToList();
        if (keys.Count == 0) return Array.Empty<IStorageItem>();
        var root = await MaterializeAsync(keys, showOverlay: showOverlay);
        if (root is null) return Array.Empty<IStorageItem>();
        var items = new List<IStorageItem>();
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
            items.Add(Directory.Exists(path) ? await StorageFolder.GetFolderFromPathAsync(path) : await StorageFile.GetFileFromPathAsync(path));
        return items;
    }
    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _dialogOpen) return;
        try { var items = await ExportSelectionAsync(); if (items.Count == 0) return; var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy }; data.SetStorageItems(items); Clipboard.SetContent(data); StatusText.Text = T("已复制文件，可粘贴到资源管理器。"); }
        catch (OperationCanceledException) { StatusText.Text = T("已取消"); }
        catch (Exception ex) { await ErrorAsync(T("复制文件"), ex); }
    }
    private IReadOnlyList<IStorageItem>? _readyDragItems;
    private Func<Task<DataPackageOperation>>? _restartFileDrag;
    private bool _preparingDrag, _dragUsesMouse;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    private async void FileList_DragStarting(UIElement sender, DragStartingEventArgs e)
    {
        e.AllowedOperations = DataPackageOperation.Copy;
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        if (_readyDragItems is not null) { e.Data.SetStorageItems(_readyDragItems); return; }
        // DragStarting deferrals delay the drag visual, not publication of data formats.
        // Prepare files first, then start a new drag with a complete native file package.
        e.Cancel = true;
        if (_preparingDrag || _restartFileDrag is null) return;
        var keys = FileList.SelectedItems.OfType<ArchiveItem>().Select(i => i.Key).ToArray();
        if (keys.Length == 0) return;
        var index = _index;
        var folder = _currentPath;
        var restart = _restartFileDrag;
        _preparingDrag = true;
        try
        {
            if (index is null || _busy || _dialogOpen) return;
            var items = await ExportSelectionAsync(keys, showOverlay: false);
            if (items.Count == 0 || !ReferenceEquals(_index, index) || _currentPath != folder || (_dragUsesMouse && (GetAsyncKeyState(1) & 0x8000) == 0)) return;
            _readyDragItems = items;
            StatusText.Text = T("文件已准备，可拖放到目标位置。");
            await restart();
        }
        catch (OperationCanceledException) { StatusText.Text = T("已取消"); }
        catch (Exception ex) { StatusText.Text = T(ex.Message); }
        finally { _readyDragItems = null; _preparingDrag = false; }
    }

    private async Task UpdateZipAsync(IReadOnlyList<string>? additions = null, bool delete = false, bool rename = false)
    {
        if (_busy || _dialogOpen || _index is null) return;
        if (!EditableZip) { await ShowMessageAsync(T("修改压缩包"), T("当前压缩包及所有外层压缩包都须为不含符号链接的可修改单卷格式。")); return; }
        var index = _index;
        var selected = FileList.SelectedItems.Cast<ArchiveItem>().ToList();
        if ((delete || rename) && selected.Count == 0) return;
        if (rename && selected.Count != 1) return;
        string? newName = null;
        if (rename)
        {
            var name = new TextBox { Text = selected[0].Name, MinWidth = 280 };
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = T("重命名"), Content = name, PrimaryButtonText = T("保存"), CloseButtonText = T("取消") };
            _dialogOpen = true;
            try { if (await dialog.ShowAsync() != ContentDialogResult.Primary) return; newName = name.Text; } finally { _dialogOpen = false; }
        }
        else
        {
            var question = new ContentDialog { XamlRoot = Root.XamlRoot, Title = T("修改压缩包"), Content = delete ? F("从压缩包中删除 {0} 项及其内容？", selected.Count) : T("添加文件；同名项目将被更新。继续？"), PrimaryButtonText = T("继续"), CloseButtonText = T("取消") };
            _dialogOpen = true; try { if (await question.ShowAsync() != ContentDialogResult.Primary) return; } finally { _dialogOpen = false; }
        }
        if (index.Items.Any(i => i.IsEncrypted) && string.IsNullOrEmpty(_password)) { _password = await AskPasswordAsync(); if (_password is null) return; }
        _busy = _dialogOpen = true; UpdateCommands();
        bool success;
        var parents = _parents.ToArray();
        var password = _password;
        var selectionRoot = SelectionRoot;
        _previewCancellation?.Cancel(); ResetRichPreview();
        try { success = await new OperationDialog().RunAsync(Root.XamlRoot, T("正在更新压缩包"), index.Name, index.Directory,
            (control, progress, token) => SaveArchiveChangeAsync(index, password, parents,
                candidate => _archives.EditZipAsync(candidate, additions, delete ? selected.Select(i => i.Key).ToList() : null, rename ? selected[0].Key : null, newName, control, progress, token, password, selectionRoot), control, progress, token)); }
        finally { _busy = _dialogOpen = false; UpdateCommands(); }
        if (success) { var folder = _currentPath; await OpenArchiveAsync(index.FilePath); if (_folders.ContainsKey(folder)) Navigate(folder, false); }
    }
    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _dialogOpen) return;
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, Handle);
            var files = await picker.PickMultipleFilesAsync(); if (files.Count > 0) await UpdateZipAsync(files.Select(f => f.Path).ToArray());
        }
        catch (Exception ex) { await ErrorAsync(T("Could not open the file picker"), ex); }
    }
    private async void AddFolder_Click(object sender, RoutedEventArgs e) { if (_busy || _dialogOpen) return; var path = await PickFolderAsync(); if (path is not null) await UpdateZipAsync([path]); }
    private async void Delete_Click(object sender, RoutedEventArgs e) => await UpdateZipAsync(delete: true);
    private async void Rename_Click(object sender, RoutedEventArgs e) => await UpdateZipAsync(rename: true);
    private void SearchAllChanged()
    {
        _searchAll = SearchAllMenu.IsChecked;
        SearchBox.PlaceholderText = _searchAll ? T("搜索整个压缩包") : T("搜索当前目录");
        RefreshControlNames();
        RefreshFiles();
    }
    private async void Encoding_Click(object sender, RoutedEventArgs e)
    {
        if (_index is null || _busy || _dialogOpen) return;
        var codes = new int?[] { null, 65001, 936, 932, 950, 949, 437, 1252 };
        var combo = new ComboBox { ItemsSource = new[] { T("自动"), "UTF-8", "GBK / CP936", "Shift-JIS / CP932", "Big5 / CP950", "Korean / CP949", "OEM / CP437", "Western / CP1252" }, SelectedIndex = Array.IndexOf(codes, _codePage), MinWidth = 260 };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = T("文件名编码"), Content = combo, PrimaryButtonText = T("应用"), CloseButtonText = T("取消") };
        _dialogOpen = true;
        try { if (await dialog.ShowAsync() != ContentDialogResult.Primary) return; } finally { _dialogOpen = false; }
        _codePage = codes[Math.Max(0, combo.SelectedIndex)]; var path = _index.FilePath; await OpenArchiveAsync(path);
    }
    private void CancelOpen_Click(object sender, RoutedEventArgs e) => _opening?.Cancel();
}
