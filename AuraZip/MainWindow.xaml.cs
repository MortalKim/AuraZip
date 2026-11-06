using static AuraZip.Services.Strings;
using AuraZip.Core;
using AuraZip.Services;
using AuraZip.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace AuraZip;

public sealed partial class MainWindow : Window
{
    private readonly ArchiveService _archives = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly Stack<string> _history = new();
    private readonly Dictionary<string, FolderNode> _folders = new(StringComparer.Ordinal);
    private ArchiveIndex? _index;
    private string _currentPath = "", _query = "", _sort = "Name";
    private bool _descending, _busy, _dialogOpen;
    private bool _stopShellBatch;
    private string? _password;
    private CancellationTokenSource? _previewCancellation;
    private DataTemplate? _wideFileTemplate;
    private bool _compactColumns;
    private bool _showNarrowPreview = true;
    private long _titleForegroundCallback;
    public ObservableCollection<ArchiveItem> VisibleItems { get; } = new();
    public ObservableCollection<FolderNode> FolderRoots { get; } = new();
    public string? InitialArchivePath { get; set; }
    public string? InitialShellAction { get; set; }
    public IReadOnlyList<string>? InitialShellPaths { get; set; }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    private nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    public MainWindow()
    {
        Strings.SetLanguage(_settings.Language);
        InitializeComponent();
        FileList.DragStarting += FileList_DragStarting;
        FileList.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            var point = args.GetCurrentPoint(null);
            _dragUsesMouse = point.PointerDeviceType.ToString() == "Mouse";
            _restartFileDrag = async () => await FileList.StartDragAsync(point);
        }), true);
        FileList.ContainerContentChanging += (_, e) =>
        {
            e.ItemContainer.DragStarting -= FileList_DragStarting;
            if (!e.InRecycleQueue) e.ItemContainer.DragStarting += FileList_DragStarting;
        };
        InitializeExternalEditing();
        SearchAllMenu.RegisterPropertyChangedCallback(ToggleMenuFlyoutItem.IsCheckedProperty, (_, _) => SearchAllChanged());
        _wideFileTemplate = FileList.ItemTemplate;
        FileList.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(FileList_KeyDown), true);
        FileList.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(FileList_DoubleTapped), true);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AuraZip.ico"));
        var scale = GetDpiForWindow(Handle) / 96d;
        var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        int width = Math.Min((int)(1180 * scale), work.Width - 40), height = Math.Min((int)(820 * scale), work.Height - 40);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
        Root.ActualThemeChanged += (_, _) => UpdateWindowTheme();
        _titleForegroundCallback = WindowTitle.RegisterPropertyChangedCallback(TextBlock.ForegroundProperty, (_, _) => UpdateWindowTheme());
        ApplyTheme(); UpdateWindowTheme();
        AddShortcut(VirtualKey.O, VirtualKeyModifiers.Control, async () => await PickArchiveAsync());
        AddShortcut(VirtualKey.N, VirtualKeyModifiers.Control, async () => await CreateZipAsync());
        AddShortcut(VirtualKey.W, VirtualKeyModifiers.Control, ShowStart);
        AddShortcut(VirtualKey.F, VirtualKeyModifiers.Control, () => SearchBox.Focus(FocusState.Programmatic));
        AddShortcut(VirtualKey.A, VirtualKeyModifiers.Control, () => { if (_index is not null && !SearchBox.FocusState.Equals(FocusState.Keyboard)) FileList.SelectAll(); });
        AddShortcut(VirtualKey.E, VirtualKeyModifiers.Menu, async () => await ExtractAsync());
        AddShortcut(VirtualKey.Up, VirtualKeyModifiers.Menu, Up);
        AddShortcut(VirtualKey.Left, VirtualKeyModifiers.Menu, Back);
        AddShortcut(VirtualKey.Enter, VirtualKeyModifiers.Menu, async () => await PropertiesAsync());
        var copy = new KeyboardAccelerator { Key = VirtualKey.C, Modifiers = VirtualKeyModifiers.Control };
        copy.Invoked += (_, e) => { if (!_busy && !_dialogOpen) { e.Handled = true; Copy_Click(this, new RoutedEventArgs()); } };
        FileList.KeyboardAccelerators.Add(copy);
        Closed += (_, _) => { WindowTitle.UnregisterPropertyChangedCallback(TextBlock.ForegroundProperty, _titleForegroundCallback); _previewCancellation?.Cancel(); _opening?.Cancel(); ResetRichPreview(); };
        AppWindow.Closing += (_, e) => { if (_busy) { e.Cancel = true; StatusText.Text = T("Finish or cancel the current operation before closing AuraZip."); } };
    }

    private void AddShortcut(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) => { if (!_dialogOpen && !_busy) { if (key == VirtualKey.A && FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox or PasswordBox) return; e.Handled = true; action(); } };
        Root.KeyboardAccelerators.Add(accelerator);
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        _ = Task.Run(() => TemporaryWorkspace.CleanupExpired());
        RefreshControlNames();
        ShowStart();
        if (InitialShellAction == "compress") { await CreateArchiveDialogAsync(InitialShellPaths); return; }
        var paths = InitialShellPaths ?? (InitialArchivePath is null ? Array.Empty<string>() : new[] { InitialArchivePath });
        _batchMode = paths.Count > 1 && InitialShellAction is not null and not "open";
        foreach (var archivePath in paths)
        {
            if (_stopShellBatch) break;
            await OpenArchiveAsync(archivePath);
            if (_index is not null && string.Equals(_index.FilePath, ArchiveFormats.FirstVolume(archivePath), StringComparison.OrdinalIgnoreCase) && InitialShellAction is not null && InitialShellAction != "open")
            {
                if (InitialShellAction == "test") await TestAsync();
                else
                {
                    if (_index.Items.Any(i => i.IsEncrypted) && string.IsNullOrEmpty(_password))
                    {
                        _password = await AskPasswordAsync();
                        if (_password is null) { _stopShellBatch = true; break; }
                    }
                    await ExtractAsync(InitialShellAction == "extract" ? null : InitialShellAction);
                }
            }
        }
        if (_batchMode) { _batchMode = false; await ShowMessageAsync(T("批量处理结果"), string.Join("\n\n", _batchResults)); }
    }

    private void ApplyTheme() => Root.RequestedTheme = _settings.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
    private void UpdateWindowTheme()
    {
        // Track the resolved theme brush, including contrast-theme resource changes.
        Windows.UI.Color? foreground = (WindowTitle.Foreground as Microsoft.UI.Xaml.Media.SolidColorBrush)?.Color;
        AppWindow.TitleBar.ButtonForegroundColor = foreground;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = foreground;
        AppWindow.TitleBar.ButtonBackgroundColor = AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
    }
    private void ShowStart()
    {
        if (_busy || _dialogOpen) return;
        _previewCancellation?.Cancel(); _index = null; _password = null; _codePage = null; _parents.Clear();
        StartPanel.Visibility = Visibility.Visible; ArchivePanel.Visibility = Visibility.Collapsed;
        WindowTitle.Text = Title = "AuraZip"; StatusText.Text = T("就绪"); FormatStatus.Text = "";
        RecentList.ItemsSource = _settings.RecentFiles.Where(File.Exists).Select(p => new RecentArchive(Path.GetFileName(p), p)).ToList();
        RecentEmpty.Visibility = RecentList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCommands();
    }

    private async Task PickArchiveAsync()
    {
        if (_busy || _dialogOpen) return;
        var picker = new FileOpenPicker();
        foreach (var extension in ArchiveFormats.Extensions) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Handle);
        try { var file = await picker.PickSingleFileAsync(); if (file is not null) { _parents.Clear(); _codePage = null; await OpenArchiveAsync(file.Path); } }
        catch (Exception ex) { await ErrorAsync(T("Could not open the file picker"), ex); }
    }

    public async Task OpenArchiveAsync(string path)
    {
        if (_busy || _dialogOpen) return;
        path = ArchiveFormats.FirstVolume(path);
        if (!File.Exists(path)) { if (_batchMode) _batchResults.Add(path + ": " + T("Archive not found")); else await ErrorAsync(T("Archive not found"), new FileNotFoundException(T("Choose an existing archive and try again."), path)); return; }
        _opening = new CancellationTokenSource();
        _busy = true; BusyOverlay.Visibility = Visibility.Visible; Commands.IsEnabled = false;
        try
        {
            ArchiveIndex archive;
            string? password = null;
            while (true)
            {
                try { archive = await _archives.OpenAsync(path, password, _opening.Token, _codePage); break; }
                catch (Exception ex) when (ArchiveService.IsPasswordError(ex))
                {
                    BusyOverlay.Visibility = Visibility.Collapsed;
                    password = await AskPasswordAsync(password is not null);
                    if (password is null) { if (_batchMode) _stopShellBatch = true; return; }
                    BusyOverlay.Visibility = Visibility.Visible;
                }
            }
            _previewCancellation?.Cancel();
            _index = archive; _password = password; _history.Clear(); _currentPath = ""; _query = ""; SearchBox.Text = "";
            _settings.Remember(archive.FilePath);
            WindowTitle.Text = Title = archive.Name + " — AuraZip";
            ArchiveSummary.Text = F("{0} 压缩文件 · {1} · {2} 个文件 · {3}", Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), ArchiveItem.FormatSize(new FileInfo(path).Length), archive.Items.Count(i => !i.IsDirectory), archive.Directory);
            FormatStatus.Text = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            StartPanel.Visibility = Visibility.Collapsed; ArchivePanel.Visibility = Visibility.Visible;
            BuildTree(); RefreshFiles(); RefreshPath();
        }
        catch (OperationCanceledException) { StatusText.Text = T("已取消"); if (_batchMode) _stopShellBatch = true; }
        catch (Exception ex) { if (_batchMode) _batchResults.Add(Path.GetFileName(path) + ": " + ex.Message); else await ErrorAsync(T("Could not open the archive"), ex); }
        finally { _opening?.Dispose(); _opening = null; _busy = false; BusyOverlay.Visibility = Visibility.Collapsed; Commands.IsEnabled = true; UpdateCommands(); }
    }

    private void BuildTree()
    {
        FolderRoots.Clear(); _folders.Clear();
        if (_index is null) return;
        var root = new FolderNode { Name = _index.Name, Key = "", IsExpanded = true };
        _folders[""] = root;
        foreach (var item in _index.Items.Where(i => i.IsDirectory).OrderBy(i => i.Key.Count(c => c == '/')).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var node = new FolderNode { Name = item.Name, Key = item.Key };
            _folders[item.Key] = node;
            if (_folders.TryGetValue(item.Parent, out var parent)) parent.Children.Add(node);
        }
        FolderRoots.Add(root);
    }

    private void Navigate(string path, bool record = true)
    {
        if (_index is null || _busy) return;
        if (record && path != _currentPath) _history.Push(_currentPath);
        _currentPath = path; _query = ""; SearchBox.Text = "";
        RefreshFiles(); RefreshPath();
        if (_folders.TryGetValue(path, out var selected)) FolderTree.SelectedItem = selected;
        foreach (var node in _folders.Values) if (path.StartsWith(node.Key + "/", StringComparison.Ordinal) || path == node.Key || node.Key == "") node.IsExpanded = true;
    }

    private void RefreshPath()
    {
        if (_index is null) return;
        var crumbs = new List<PathCrumb> { new(_index.Name, "") };
        string key = "";
        foreach (var part in _currentPath.Split('/', StringSplitOptions.RemoveEmptyEntries)) { key = key.Length == 0 ? part : key + "/" + part; crumbs.Add(new(part, key)); }
        if (_parents.Count > 0) crumbs.Insert(0, new(_parents.Peek().Index.Name, "\0parent"));
        PathBar.ItemsSource = crumbs;
        BackButton.IsEnabled = _history.Count > 0 || _parents.Count > 0; UpButton.IsEnabled = _currentPath.Length > 0 || _parents.Count > 0;
    }

    private void RefreshFiles()
    {
        if (_index is null) return;
        bool global = _searchAll && _query.Length > 0;
        ArchiveNameConverter.FullPath = global;
        var pattern = _query.IndexOfAny(['*', '?']) >= 0 ? new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(_query).Replace("\\*", ".*").Replace("\\?", ".") + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.NonBacktracking) : null;
        var files = (global ? _index.Items : _index.Children(_currentPath)).Where(i => pattern is not null ? pattern.IsMatch(i.Name) || (global && pattern.IsMatch(i.Key)) : i.Name.Contains(_query, StringComparison.CurrentCultureIgnoreCase) || (global && i.Key.Contains(_query, StringComparison.CurrentCultureIgnoreCase)));
        Func<ArchiveItem, object?> order = _sort switch { "Size" => i => i.Size, "PackedSize" => i => i.PackedSize, "TypeText" => i => i.TypeText, "Modified" => i => i.Modified, _ => i => i.Name };
        var sorted = _descending ? files.OrderBy(i => !i.IsDirectory).ThenByDescending(order) : files.OrderBy(i => !i.IsDirectory).ThenBy(order);
        FileList.SelectedItems.Clear(); VisibleItems.Clear(); foreach (var item in sorted) VisibleItems.Add(item);
        ListEmpty.Visibility = VisibleItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListEmpty.Text = _query.Length > 0 ? T("没有匹配的文件") : T("此文件夹为空");
        UpdateStatus(); UpdateCommands(); _ = UpdatePreviewAsync();
    }

    private void UpdateStatus()
    {
        if (_index is null) return;
        var children = VisibleItems.ToList();
        StatusText.Text = F("{0} 个文件夹，{1} 个文件", children.Count(i => i.IsDirectory), children.Count(i => !i.IsDirectory));
        var selected = FileList.SelectedItems.Cast<ArchiveItem>().ToList();
        if (selected.Count > 0) StatusText.Text += F("   |   已选择 {0} 项 · {1}", selected.Count, ArchiveItem.FormatSize(_index.Scope(selected.Select(i => i.Key).ToList()).Where(i => !i.IsDirectory).Sum(i => i.Size)));
    }

    private void UpdateCommands()
    {
        ExtractCommand.IsEnabled = TestCommand.IsEnabled = PropertiesCommand.IsEnabled = _index is not null && !_busy;
        ViewCommand.IsEnabled = _index is not null && FileList.SelectedItems.Count == 1 && !_busy;
        AddFilesCommand.IsEnabled = DeleteCommand.IsEnabled = _index is not null && EditableZip && !_busy;
        bool narrow = Root.ActualWidth < 860;
        FolderPane.Visibility = (narrow ? _showNarrowPreview : _settings.ShowFolderPane) ? Visibility.Visible : Visibility.Collapsed;
        FolderColumn.Width = !narrow && _settings.ShowFolderPane ? new GridLength(260) : new GridLength(0);
        Grid.SetRow(FolderPane, narrow ? 1 : 0);
        Grid.SetColumnSpan(FolderPane, narrow ? 2 : 1);
        FolderPane.Height = narrow ? Math.Clamp(Root.ActualHeight * .32, 120, 260) : double.NaN;
        FolderTreeSection.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(PreviewSection, narrow ? 0 : 1);
        Grid.SetRowSpan(PreviewSection, narrow ? 2 : 1);
        PreviewPaneCommand.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        PreviewPaneCommand.Label = T(_showNarrowPreview ? "隐藏预览" : "显示预览");
        PreviewPaneCommand.IsEnabled = _index is not null;
    }
    private void PreviewPane_Click(object sender, RoutedEventArgs e)
    { _showNarrowPreview = !_showNarrowPreview; UpdateCommands(); }

    private async Task UpdatePreviewAsync()
    {
        _previewCancellation?.Cancel();
        ResetRichPreview();
        var cancellation = _previewCancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        PreviewImage.Source = null; PreviewImage.MaxHeight = 200; PreviewImage.Visibility = Visibility.Collapsed;
        PreviewName.Text = ""; PreviewInfo.Text = "";
        var selected = FileList.SelectedItems.Cast<ArchiveItem>().ToList();
        var index = _index;
        if (index is null || selected.Count == 0) { PreviewText.Text = T("选择文件以预览"); return; }
        if (selected.Count > 1) { PreviewName.Text = F("已选择 {0} 项", selected.Count); PreviewText.Text = T("选择单个文件可查看内容。"); return; }
        var item = selected[0]; PreviewName.Text = item.Name; PreviewInfo.Text = FileTypeConverter.GetText(item) + (item.IsDirectory ? "" : " · " + item.SizeText);
        if (item.IsDirectory)
        {
            var children = index.Children(item.Key).ToList();
            PreviewInfo.Text = F("{0} 个直接子项", children.Count);
            PreviewText.Text = string.Join("\n", children.Take(40).Select(i => (i.IsDirectory ? "▸ " : "") + i.Name)); return;
        }
        var extension = Path.GetExtension(item.Name).ToLowerInvariant();
        var image = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico", ".webp" }.Contains(extension);
        var text = new[] { ".txt", ".md", ".json", ".xml", ".xaml", ".cs", ".cpp", ".h", ".log", ".ini", ".config", ".yaml", ".yml", ".csv", ".ps1", ".js", ".css", ".html", ".gitignore", ".editorconfig", ".props", ".csproj" }.Contains(extension) || item.Name is "LICENSE" or "README";
        bool rich = IsRichPreview(extension);
        if (!image && !text && !rich) { PreviewText.Text = T("此格式暂无内容预览。\n可解压后使用相应应用打开。"); return; }
        if (item.IsEncrypted && string.IsNullOrEmpty(_password)) { PreviewText.Text = T("此文件已加密。请在解压时输入密码。"); return; }
        PreviewText.Text = T("正在读取…");
        try
        {
            if (rich) { await ShowRichPreviewAsync(index, item, extension, token); return; }
            var data = await _archives.ReadPreviewAsync(index.FilePath, item.Key, _password, image ? 8 * 1024 * 1024 : 256 * 1024, token, index.CodePage);
            token.ThrowIfCancellationRequested();
            if (image)
            {
                using var stream = new MemoryStream(data);
                var bitmap = new BitmapImage { DecodePixelWidth = 460 };
                await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
                token.ThrowIfCancellationRequested();
                PreviewImage.Source = bitmap; PreviewImage.Visibility = Visibility.Visible; PreviewText.Text = "";
            }
            else
            {
                string content;
                try
                {
                    using var stream = new StreamReader(new MemoryStream(data), new UTF8Encoding(false, true), true);
                    content = await stream.ReadToEndAsync(token);
                }
                catch (DecoderFallbackException)
                {
                    using var stream = new StreamReader(new MemoryStream(data), Encoding.GetEncoding(index.CodePage ?? System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage), true);
                    content = await stream.ReadToEndAsync(token);
                }
                token.ThrowIfCancellationRequested();
                PreviewText.Text = content.Contains('\0') ? T("此文件包含二进制数据，无法按文本预览。") : content;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                if (ReferenceEquals(_index, index) && ArchiveService.IsPasswordError(ex)) _password = null;
                PreviewText.Text = T("Preview unavailable: ") + T(ex.Message) + T(" Extract the file to view it.");
            }
        }
    }

    private async Task ExtractAsync(string? quickMode = null)
    {
        if (_index is null || _busy || _dialogOpen) return;
        var index = _index;
        if (_batchMode && _batchPlan is not null && quickMode is null) quickMode = "batch";
        var selectedKeys = FileList.SelectedItems.Cast<ArchiveItem>().Select(i => i.Key).ToList();
        var destination = new TextBox { Text = _settings.UseArchiveDirectory ? index.Directory : _settings.CustomDirectory, PlaceholderText = T("目标路径") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(destination, T("目标路径"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(destination, "ExtractionDestination");
        var browse = new Button { Content = T("浏览…") };
        var pathRow = new Grid { ColumnSpacing = 8 }; pathRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); pathRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        pathRow.Children.Add(destination); Grid.SetColumn(browse, 1); pathRow.Children.Add(browse);
        var all = new RadioButton { Content = T("所有文件"), GroupName = "ExtractScope", IsChecked = selectedKeys.Count == 0 };
        var chosen = new RadioButton { Content = F("选中的文件 ({0} 项)", selectedKeys.Count), GroupName = "ExtractScope", IsEnabled = selectedKeys.Count > 0, IsChecked = selectedKeys.Count > 0 };
        var scopeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 }; scopeRow.Children.Add(all); scopeRow.Children.Add(chosen);
        var smart = new CheckBox { Content = T("智能解压"), IsChecked = _settings.SmartExtraction };
        var rules = T("只有一个顶层文件或文件夹：直接解压到目标路径，保留该项目。\n\n多个顶层项目：创建与压缩包同名的文件夹。\n\n提取所选文件时，按本次选中的项目判断；同名文件仍按所选策略处理。");
        var help = new Button { Content = "?", Width = 26, Height = 26, Padding = new(0), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(help, new ToolTip { Content = new TextBlock { Text = rules, TextWrapping = TextWrapping.Wrap, MaxWidth = 280 } });
        help.Flyout = new Flyout { Content = new TextBlock { Text = rules, TextWrapping = TextWrapping.Wrap, MaxWidth = 280 } };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(help, T("智能解压规则"));
        var smartRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; smartRow.Children.Add(smart); smartRow.Children.Add(help);
        var same = new CheckBox { Content = T("创建同名文件夹"), IsChecked = true, IsEnabled = smart.IsChecked != true };
        var actual = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 430 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(actual, "ResolvedDestination");
        var conflict = new ComboBox { Header = T("同名文件"), HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { T("询问我"), T("覆盖"), T("跳过"), T("自动重命名"), T("仅覆盖较新文件") }, SelectedIndex = 0 };
        var password = new PasswordBox { Header = T("密码（如有）"), Password = _password ?? "", PlaceholderText = T("无需密码时留空") };
        var openAfter = new CheckBox { Content = T("完成后打开目标文件夹"), IsChecked = _settings.OpenAfterExtraction };
        if (quickMode is not null)
        {
            destination.Text = index.Directory;
            smart.IsChecked = quickMode == "smart";
            same.IsChecked = quickMode == "folder";
            if (quickMode == "batch" && _batchPlan is { } plan)
            {
                destination.Text = plan.PerArchive ? index.Directory : plan.Directory;
                smart.IsChecked = plan.Smart; same.IsChecked = plan.Folder;
                conflict.SelectedIndex = plan.Policy switch { ConflictPolicy.Overwrite => 1, ConflictPolicy.Skip => 2, ConflictPolicy.Rename => 3, ConflictPolicy.Newer => 4, _ => 0 };
                openAfter.IsChecked = plan.Open;
            }
        }
        var panel = new StackPanel { Spacing = 12, MinWidth = Math.Min(390, Math.Max(200, Root.ActualWidth - 80)), MaxWidth = 450 };
        panel.Children.Add(new TextBlock { Text = index.Name, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        panel.Children.Add(new TextBlock { Text = T("目标路径"), FontSize = 12 }); panel.Children.Add(pathRow);
        panel.Children.Add(scopeRow); panel.Children.Add(smartRow); panel.Children.Add(same);
        panel.Children.Add(new TextBlock { Text = T("实际解压位置"), FontSize = 11 }); panel.Children.Add(actual);
        panel.Children.Add(conflict); panel.Children.Add(password); panel.Children.Add(openAfter);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = T("解压到"), Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 570 }, PrimaryButtonText = T("解压"), CloseButtonText = T("取消"), DefaultButton = ContentDialogButton.Primary };
        IReadOnlyCollection<string>? Scope() => chosen.IsChecked == true ? selectedKeys : null;
        void Update()
        {
            same.IsEnabled = smart.IsChecked != true;
            try
            {
                if (string.IsNullOrWhiteSpace(destination.Text) || !Path.IsPathFullyQualified(destination.Text)) throw new ArgumentException(T("Enter an absolute destination path."));
                actual.Text = ArchiveService.ResolveDestination(index, destination.Text, smart.IsChecked == true, same.IsChecked == true, Scope());
                dialog.IsPrimaryButtonEnabled = true;
            }
            catch (Exception ex) { actual.Text = ex.Message; dialog.IsPrimaryButtonEnabled = false; }
        }
        destination.TextChanged += (_, _) => Update();
        smart.Checked += (_, _) => Update(); smart.Unchecked += (_, _) => Update(); same.Checked += (_, _) => Update(); same.Unchecked += (_, _) => Update();
        all.Checked += (_, _) => Update(); chosen.Checked += (_, _) => Update();
        browse.Click += async (_, _) => { var path = await PickFolderAsync(); if (path is not null) destination.Text = path; };
        Update();
        _dialogOpen = true;
        ContentDialogResult result;
        try { result = quickMode is null ? await dialog.ShowAsync() : ContentDialogResult.Primary; } finally { _dialogOpen = false; }
        if (result != ContentDialogResult.Primary) { _stopShellBatch = true; return; }
        try
        {
            var scope = Scope();
            var target = ArchiveService.ResolveDestination(index, destination.Text, smart.IsChecked == true, same.IsChecked == true, scope);
            var policy = conflict.SelectedIndex switch { 1 => ConflictPolicy.Overwrite, 2 => ConflictPolicy.Skip, 3 => ConflictPolicy.Rename, 4 => ConflictPolicy.Newer, _ => ConflictPolicy.Fail };
            if (policy == ConflictPolicy.Fail)
            {
                var collisions = ArchiveService.FindConflicts(index, target, scope, scope is null ? "" : SelectionRoot);
                if (collisions.Count > 0)
                {
                    var incoming = index.Scope(scope).Where(i => !i.IsDirectory).ToDictionary(i => ArchiveService.SafeOutputPath(target, scope is not null && SelectionRoot.Length > 0 && i.Key.StartsWith(SelectionRoot + "/", StringComparison.Ordinal) ? i.Key[(SelectionRoot.Length + 1)..] : i.Key), StringComparer.OrdinalIgnoreCase);
                    var question = new ContentDialog { XamlRoot = Root.XamlRoot, Title = T("同名文件"), Content = new ScrollViewer { MaxHeight = 340, Content = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 450, Text = F("目标目录中已有 {0} 个同名文件。请选择本次任务的处理方式。", collisions.Count) + "\n\n" + string.Join("\n\n", collisions.Take(5).Select(p => F("{0}\n压缩包：{1} · {2}\n目标：{3} · {4}", Path.GetFileName(p), ArchiveItem.FormatSize(incoming[p].Size), incoming[p].Modified?.ToString("yyyy-MM-dd HH:mm") ?? "—", ArchiveItem.FormatSize(new FileInfo(p).Length), File.GetLastWriteTime(p).ToString("yyyy-MM-dd HH:mm")))) } }, PrimaryButtonText = T("全部覆盖"), SecondaryButtonText = T("全部跳过"), CloseButtonText = T("取消"), DefaultButton = ContentDialogButton.Close };
                    _dialogOpen = true; var answer = await question.ShowAsync(); _dialogOpen = false;
                    if (answer == ContentDialogResult.None) { _stopShellBatch = true; return; }
                    policy = answer == ContentDialogResult.Primary ? ConflictPolicy.Overwrite : ConflictPolicy.Skip;
                }
            }
            if (quickMode is null) { _settings.SmartExtraction = smart.IsChecked == true; _settings.Save(); }
            _password = password.Password;
            _busy = _dialogOpen = true; UpdateCommands();
            OperationResult? outcome = null;
            if (_batchMode) _batchPlan = (destination.Text, string.Equals(destination.Text, index.Directory, StringComparison.OrdinalIgnoreCase), smart.IsChecked == true, same.IsChecked == true, policy, openAfter.IsChecked == true);
            var operationDialog = new OperationDialog();
            var success = await operationDialog.RunAsync(Root.XamlRoot, T("正在解压"), index.Name, target,
                async (control, progress, token) => { outcome = await _archives.ExtractAsync(index, target, scope, policy, _password, control, progress, token, scope is null ? "" : SelectionRoot); }, openAfter.IsChecked == true, autoClose: _batchMode);
            if (operationDialog.LastError is { } error && ArchiveService.IsPasswordError(error)) _password = null;
            StatusText.Text = success ? F("已解压 {0} 个文件，跳过 {1} 个文件 · {2}", outcome?.FilesWritten, outcome?.FilesSkipped, target) : T("Extraction did not finish. Check the operation result before retrying.");
            _stopShellBatch = operationDialog.WasCancelled;
            if (_batchMode) _batchResults.Add(index.Name + "\n" + (success ? StatusText.Text : operationDialog.LastError?.Message ?? T("已取消")));
        }
        catch (Exception ex) { _dialogOpen = false; if (_batchMode) _batchResults.Add(index.Name + ": " + ex.Message); else await ErrorAsync(T("Could not start extraction"), ex); }
        finally { _busy = _dialogOpen = false; UpdateCommands(); }
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, Handle);
        try { return (await picker.PickSingleFolderAsync())?.Path; }
        catch (Exception ex) { StatusText.Text = T("Could not open the folder picker: ") + ex.Message + T(" Try again."); return null; }
    }

    private async Task TestAsync()
    {
        if (_index is null || _busy || _dialogOpen) return;
        if (_index.Items.Any(i => i.IsEncrypted) && string.IsNullOrEmpty(_password)) { _password = await AskPasswordAsync(); if (_password is null) { _stopShellBatch = true; return; } }
        _busy = _dialogOpen = true; UpdateCommands();
        try
        {
            var operation = new OperationDialog();
            bool success = await operation.RunAsync(Root.XamlRoot, T("正在测试"), _index.Name, null, (control, progress, token) => _archives.TestAsync(_index, _password, control, progress, token), autoClose: _batchMode);
            if (operation.LastError is { } error && ArchiveService.IsPasswordError(error)) _password = null;
            _stopShellBatch = operation.WasCancelled;
            if (_batchMode) _batchResults.Add(_index.Name + "\n" + (success ? T("文件数据读取完成，未发现错误。") : operation.LastError?.Message ?? T("已取消")));
        }
        finally { _busy = _dialogOpen = false; UpdateCommands(); }
    }

    private Task CreateZipAsync() => CreateArchiveDialogAsync();

    private async Task SettingsAsync()
    {
        if (_busy || _dialogOpen) return;
        var language = new ComboBox { Header = T("语言"), ItemsSource = new[] { T("跟随系统"), "简体中文", "English" }, SelectedIndex = _settings.Language == "zh-CN" ? 1 : _settings.Language == "en-US" ? 2 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(language, "LanguageSelector");
        var theme = new ComboBox { Header = T("外观"), ItemsSource = new[] { T("跟随系统"), T("浅色"), T("深色") }, SelectedIndex = _settings.Theme == "Light" ? 1 : _settings.Theme == "Dark" ? 2 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(theme, "ThemeSelector");
        var mode = new ComboBox { Header = T("默认解压目录"), ItemsSource = new[] { T("压缩包所在目录"), T("指定目录") }, SelectedIndex = _settings.UseArchiveDirectory ? 0 : 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        var custom = new TextBox { Header = T("指定目录"), Text = _settings.CustomDirectory, Visibility = _settings.UseArchiveDirectory ? Visibility.Collapsed : Visibility.Visible };
        mode.SelectionChanged += (_, _) => custom.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        var open = new CheckBox { Content = T("完成后打开目标文件夹"), IsChecked = _settings.OpenAfterExtraction };
        var panel = new StackPanel { Spacing = 18, MinWidth = Math.Min(360, Math.Max(200, Root.ActualWidth - 80)) }; panel.Children.Add(language); panel.Children.Add(theme); panel.Children.Add(mode); panel.Children.Add(custom); panel.Children.Add(open);
        panel.Children.Add(new TextBlock { Text = T("压缩文件的操作收纳在 AuraZip 子菜单中。"), TextWrapping = TextWrapping.Wrap, MaxWidth = 360, FontSize = 12 });
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = T("设置"), Content = new ScrollViewer { Content = panel, MaxHeight = Math.Max(160, Root.ActualHeight - 180), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, PrimaryButtonText = T("保存"), CloseButtonText = T("取消"), DefaultButton = ContentDialogButton.Primary };
        dialog.PrimaryButtonClick += (_, e) => { if (mode.SelectedIndex == 1 && !Path.IsPathFullyQualified(custom.Text)) { e.Cancel = true; custom.Header = T("Enter an absolute directory path."); } };
        _dialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            _settings.Theme = theme.SelectedIndex == 1 ? "Light" : theme.SelectedIndex == 2 ? "Dark" : "System";
            _settings.UseArchiveDirectory = mode.SelectedIndex == 0; _settings.CustomDirectory = custom.Text;
            _settings.OpenAfterExtraction = open.IsChecked == true;
            _settings.Language = language.SelectedIndex == 1 ? "zh-CN" : language.SelectedIndex == 2 ? "en-US" : "System";
            _settings.Save(); Strings.SetLanguage(_settings.Language); ApplyTheme(); RefreshLanguage();
        }
        finally { _dialogOpen = false; }
    }

    private void RefreshLanguage()
    {
        RefreshControlNames();
        SearchBox.PlaceholderText = _searchAll ? T("搜索整个压缩包") : T("搜索当前目录");
        NameHeader.Content = T("名称") + (_sort == "Name" ? (_descending ? " ↓" : " ↑") : "");
        if (_index is null) { StatusText.Text = T("就绪"); return; }
        ArchiveSummary.Text = F("{0} 压缩文件 · {1} · {2} 个文件 · {3}", Path.GetExtension(_index.FilePath).TrimStart('.').ToUpperInvariant(), ArchiveItem.FormatSize(new FileInfo(_index.FilePath).Length), _index.Items.Count(i => !i.IsDirectory), _index.Directory);
        var selected = FileList.SelectedItems.Cast<ArchiveItem>().Select(i => i.Key).ToHashSet();
        RefreshFiles();
        foreach (var item in VisibleItems.Where(i => selected.Contains(i.Key))) FileList.SelectedItems.Add(item);
    }

    private void RefreshControlNames()
    {
        static IEnumerable<DependencyObject> Children(DependencyObject parent)
        {
            for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var descendant in Children(child)) yield return descendant;
            }
        }
        var searchName = _searchAll ? T("搜索整个压缩包") : T("搜索当前目录");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(BackButton, T("返回 (Alt+←)"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(UpButton, T("上一级 (Alt+↑)"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(FileList, T("文件和文件夹"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(FolderTree, T("文件夹"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchBox, searchName);
        foreach (var text in Children(SearchBox).OfType<TextBox>())
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(text, searchName);
        foreach (var button in Children(Commands).OfType<Button>().Where(b => b.Name == "MoreButton"))
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, T("更多选项"));
            ToolTipService.SetToolTip(button, T("更多选项"));
        }
    }

    private async Task PropertiesAsync()
    {
        if (_index is null || _dialogOpen) return;
        var item = FileList.SelectedItems.Count == 1 ? FileList.SelectedItem as ArchiveItem : null;
        var text = item is null ? F("名称：{0}\n\n位置：{1}\n\n压缩后：{2}\n\n原始大小：{3}\n\n文件数量：{4}", _index.Name, _index.Directory, ArchiveItem.FormatSize(new FileInfo(_index.FilePath).Length), ArchiveItem.FormatSize(_index.Items.Where(i => !i.IsDirectory).Sum(i => i.Size)), _index.Items.Count(i => !i.IsDirectory)) : F("名称：{0}\n\n路径：{1}\n\n类型：{2}\n\n大小：{3}\n\n压缩后：{4}\n\n修改时间：{5}\n\n加密：{6}", item.Name, item.Key, FileTypeConverter.GetText(item), item.SizeText, item.PackedText, item.ModifiedText, (item.IsEncrypted ? T("是") : T("否")));
        await ShowMessageAsync(T("属性"), text);
    }

    private async Task<string?> AskPasswordAsync(bool retry = false)
    {
        var field = new PasswordBox { Header = T("压缩包密码"), MinWidth = Math.Min(320, Math.Max(200, Root.ActualWidth - 80)) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(field, "ArchivePassword");
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(field);
        if (retry) panel.Children.Add(new TextBlock { Text = T("密码错误或加密数据已损坏，请重新输入密码。"), TextWrapping = TextWrapping.Wrap, MaxWidth = 360 });
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = T("输入密码"), Content = panel, PrimaryButtonText = T("打开"), CloseButtonText = T("取消"), DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = false };
        field.PasswordChanged += (_, _) => dialog.IsPrimaryButtonEnabled = field.Password.Length > 0;
        _dialogOpen = true;
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary ? field.Password : null; }
        finally { _dialogOpen = false; }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try { await new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = new ScrollViewer { MaxHeight = Math.Max(180, Math.Min(420, Root.ActualHeight - 200)), Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 480 } }, CloseButtonText = T("关闭") }.ShowAsync(); }
        finally { _dialogOpen = false; }
    }
    private Task ErrorAsync(string title, Exception ex) => ShowMessageAsync(title, ex.Message + T("\n\nCheck the file or destination and try again."));
    private void Up() { if (_currentPath.Length == 0 && _parents.Count > 0) { ReturnToParentArchive(); return; } if (_currentPath.Length > 0) Navigate(_currentPath.Contains('/') ? _currentPath[.._currentPath.LastIndexOf('/')] : ""); }
    private void Back() { if (_history.TryPop(out var path)) Navigate(path, false); else if (_parents.Count > 0) ReturnToParentArchive(); }
    private async void OpenSelected() => await OpenSelectedAsync();

    private async void Open_Click(object sender, RoutedEventArgs e) => await PickArchiveAsync();
    private async void New_Click(object sender, RoutedEventArgs e) => await CreateZipAsync();
    private async void Extract_Click(object sender, RoutedEventArgs e) => await ExtractAsync();
    private async void Test_Click(object sender, RoutedEventArgs e) => await TestAsync();
    private async void Settings_Click(object sender, RoutedEventArgs e) => await SettingsAsync();
    private async void Properties_Click(object sender, RoutedEventArgs e) => await PropertiesAsync();
    private void View_Click(object sender, RoutedEventArgs e) => OpenSelected();
    private void CloseArchive_Click(object sender, RoutedEventArgs e) => ShowStart();
    private void SelectAll_Click(object sender, RoutedEventArgs e) { if (_index is not null) FileList.SelectAll(); }
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => FileList.SelectedItems.Clear();
    private void TreeToggle_Click(object sender, RoutedEventArgs e) { _settings.ShowFolderPane = !_settings.ShowFolderPane; _settings.Save(); UpdateCommands(); }
    private async void Help_Click(object sender, RoutedEventArgs e) => await ShowMessageAsync("AuraZip", T("Ctrl+O  打开压缩包\nCtrl+N  新建压缩包\nCtrl+C  复制文件\nF2  重命名\nDelete  删除\nCtrl+A  全选\nCtrl+F  搜索（支持 * 和 ?）\nAlt+E  解压到\nAlt+↑  上一级\nAlt+←  返回\nAlt+Enter  属性\n\n单击预览，双击使用默认应用打开文件，或进入文件夹和嵌套压缩包。\n支持 ZIP、7Z、RAR、TAR 等格式解压；创建 ZIP、7Z、TAR、TAR.GZ、TAR.BZ2、TAR.XZ；修改单卷 ZIP、7Z。ZIP/7Z 支持密码，全部创建格式支持分卷。工具菜单提供文件哈希、ZIP 注释和外部修改写回。\n\n解码：SharpCompress（MIT）。\n压缩：7-Zip（GNU LGPL 及 unRAR 限制）。\n许可与对应源码随应用提供，位于 ThirdParty/7zip。"));
    private void Back_Click(object sender, RoutedEventArgs e) => Back();
    private void Up_Click(object sender, RoutedEventArgs e) => Up();
    private void PathBar_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args) { if (args.Item is PathCrumb crumb) { if (crumb.Key == "\0parent") ReturnToParentArchive(); else Navigate(crumb.Key); } }
    private void FolderTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args) { if (args.InvokedItem is FolderNode node) Navigate(node.Key); }
    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) { _query = sender.Text; if (_index is not null && !_busy) RefreshFiles(); }
    private void Sort_Click(object sender, RoutedEventArgs e) { var key = (sender as Button)?.Tag?.ToString() ?? "Name"; _descending = _sort == key && !_descending; _sort = key; NameHeader.Content = key == "Name" ? (_descending ? T("名称 ↓") : T("名称 ↑")) : T("名称"); RefreshFiles(); }
    private async void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) { UpdateStatus(); UpdateCommands(); await UpdatePreviewAsync(); }
    private void FileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_busy || _dialogOpen || _index is null) return;
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source != FileList && source is not ListViewItem)
            source = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(source);
        if (source is not ListViewItem { Content: ArchiveItem item }) return;
        e.Handled = true;
        var index = _index;
        // Finish the routed gesture before navigation replaces the item containers.
        DispatcherQueue.TryEnqueue(() => { if (ReferenceEquals(_index, index)) _ = OpenItemAsync(item); });
    }
    private void FileList_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Escape && _opening is not null) { e.Handled = true; _opening.Cancel(); return; } if (_dialogOpen || _busy) return; if (e.Key == VirtualKey.Enter && !e.KeyStatus.IsMenuKeyDown) { e.Handled = true; OpenSelected(); } else if (e.Key == VirtualKey.Delete) { e.Handled = true; _ = UpdateZipAsync(delete: true); } else if (e.Key == VirtualKey.F2) { e.Handled = true; _ = UpdateZipAsync(rename: true); } else if (e.Key == VirtualKey.Back) { e.Handled = true; Up(); } }
    private void FileList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var element = e.OriginalSource as FrameworkElement;
        if (element?.DataContext is ArchiveItem item && !FileList.SelectedItems.Contains(item)) FileList.SelectedItem = item;
        var menu = new MenuFlyout();
        foreach (var (label, action) in new (string, Action)[] { (T("打开 / 查看"), OpenSelected), (T("解压到…"), async () => await ExtractAsync()), (T("全选"), () => FileList.SelectAll()), (T("属性"), async () => await PropertiesAsync()), (T("复制文件"), () => Copy_Click(this, new RoutedEventArgs())), (T("重命名"), async () => await UpdateZipAsync(rename: true)), (T("删除"), async () => await UpdateZipAsync(delete: true)), (T("添加文件…"), () => AddFiles_Click(this, new RoutedEventArgs())) })
        { var command = new MenuFlyoutItem { Text = label }; command.Click += (_, _) => action(); menu.Items.Add(command); }
        menu.ShowAt(FileList, e.GetPosition(FileList));
    }
    private async void RecentList_ItemClick(object sender, ItemClickEventArgs e) { if (e.ClickedItem is RecentArchive recent) { _parents.Clear(); _codePage = null; await OpenArchiveAsync(recent.Path); } }
    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (FolderPane is not null) UpdateCommands();
        if (SearchColumn is not null) SearchColumn.Width = new(e.NewSize.Width < 860 ? 150 : 220);
        if (Commands is not null) Commands.MaxWidth = Math.Max(200, e.NewSize.Width);
        bool compact = e.NewSize.Width < 1100;
        if (_wideFileTemplate is null || compact == _compactColumns) return;
        _compactColumns = compact;
        FileList.ItemTemplate = compact ? (DataTemplate)Root.Resources["CompactFileTemplate"] : _wideFileTemplate;
        FileHeaderGrid.ColumnDefinitions[1].Width = new(compact ? 90 : 100);
        for (int i = 2; i < 5; i++)
        {
            FileHeaderGrid.ColumnDefinitions[i].Width = new(compact ? 0 : i == 2 ? 100 : i == 3 ? 110 : 150);
            FileHeaderGrid.Children[i].Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }
    }
    private void Root_DragOver(object sender, DragEventArgs e) { e.AcceptedOperation = !_busy && !_dialogOpen && e.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None; }
    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (_busy || _dialogOpen || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).ToArray();
            if (paths.Length == 0) return;
            if (paths.All(p => File.Exists(p) && ArchiveFormats.Supported(p))) { _parents.Clear(); _codePage = null; await OpenArchiveAsync(paths[0]); }
            else if (_index is not null && EditableZip) await UpdateZipAsync(paths);
            else await CreateArchiveDialogAsync(paths);
        }
        catch (Exception ex) { await ErrorAsync(T("Could not open the dropped file"), ex); }
    }
}
