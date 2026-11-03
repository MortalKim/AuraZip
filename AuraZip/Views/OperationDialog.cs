using static AuraZip.Services.Strings;
using AuraZip.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AuraZip.Views;

public sealed class OperationDialog
{
    public bool WasCancelled { get; private set; }
    public Exception? LastError { get; private set; }
    public async Task<bool> RunAsync(XamlRoot root, string title, string source, string? destination,
        Func<OperationControl, IProgress<ArchiveProgress>, CancellationToken, Task> operation, bool openAfter = false, bool autoClose = false, Func<string>? completionSummary = null)
    {
        using var cancellation = new CancellationTokenSource();
        using var control = new OperationControl();
        var clock = Stopwatch.StartNew();
        bool running = true, succeeded = false;
        var file = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 430 };
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, IsIndeterminate = true, Height = 5 };
        var count = new TextBlock { FontSize = 12 };
        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 430 };
        var elapsed = new TextBlock { FontSize = 12 };
        var rate = new TextBlock { FontSize = 12 };
        long processed = 0, totalBytes = 0;
        var activeClock = Stopwatch.StartNew();
        var panel = new StackPanel { Spacing = 16, MinWidth = Math.Min(380, Math.Max(200, root.Size.Width - 80)) };
        panel.Children.Add(new TextBlock { Text = source, TextWrapping = TextWrapping.Wrap, FontSize = 12, MaxWidth = 430 });
        panel.Children.Add(file); panel.Children.Add(bar); panel.Children.Add(count);
        if (destination is not null) panel.Children.Add(new TextBlock { Text = T("目标位置\n") + destination, TextWrapping = TextWrapping.Wrap, FontSize = 12, MaxWidth = 430 });
        panel.Children.Add(elapsed); panel.Children.Add(rate); panel.Children.Add(status);
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = panel,
            PrimaryButtonText = destination is null ? "" : T("打开目标文件夹"),
            SecondaryButtonText = T("暂停"), CloseButtonText = T("取消"), DefaultButton = ContentDialogButton.Close
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            try { OpenFolder(destination!); }
            catch (Exception ex) { status.Text = T("Could not open the destination folder: ") + ex.Message + T(" Try again when the folder is available."); }
        };
        dialog.SecondaryButtonClick += (_, args) => { args.Cancel = true; control.TogglePause(); if (control.IsPaused) activeClock.Stop(); else activeClock.Start(); dialog.SecondaryButtonText = control.IsPaused ? T("继续") : T("暂停"); status.Text = control.IsPaused ? T("已暂停") : T("正在处理…"); };
        dialog.CloseButtonClick += (_, args) => { if (running) { args.Cancel = true; cancellation.Cancel(); dialog.CloseButtonText = T("正在取消…"); } };
        dialog.Closing += (_, args) => { if (running) { args.Cancel = true; cancellation.Cancel(); } };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            elapsed.Text = T("用时  ") + clock.Elapsed.ToString(@"hh\:mm\:ss");
            var speed = activeClock.Elapsed.TotalSeconds > 0 ? processed / activeClock.Elapsed.TotalSeconds : 0;
            rate.Text = speed > 0 ? F("速度：{0}/s · 预计剩余：{1}", ArchiveItem.FormatSize((long)speed), TimeSpan.FromSeconds(Math.Clamp((totalBytes - processed) / speed, 0, 31536000)).ToString(@"hh\:mm\:ss")) : T("正在计算速度…");
        };
        timer.Start();
        var showing = dialog.ShowAsync();
        var lastPaint = TimeSpan.Zero;
        var progress = new Progress<ArchiveProgress>(p =>
        {
            if (!running || (clock.Elapsed - lastPaint < TimeSpan.FromMilliseconds(80) && p.FilesDone != p.TotalFiles)) return;
            lastPaint = clock.Elapsed;
            processed = p.BytesDone; totalBytes = p.TotalBytes;
            file.Text = p.FileName;
            bar.IsIndeterminate = false;
            bar.Value = p.TotalBytes > 0 ? Math.Min(100, 100d * p.BytesDone / p.TotalBytes) : p.TotalFiles > 0 ? 100d * p.FilesDone / p.TotalFiles : 100;
            count.Text = F("{0} / {1} 个文件 · {2} / {3}", p.FilesDone, p.TotalFiles, ArchiveItem.FormatSize(p.BytesDone), ArchiveItem.FormatSize(p.TotalBytes));
        });
        try
        {
            await operation(control, progress, cancellation.Token);
            succeeded = true;
            bar.IsIndeterminate = false; bar.Value = 100;
            dialog.Title = title == T("正在解压") ? T("解压完成") : title == T("正在测试") ? T("测试完成") : title == T("正在更新压缩包") ? T("更新完成") : T("压缩完成");
            status.Text = title == T("正在测试") ? T("文件数据读取完成，未发现错误。") : T("已完成");
            if (completionSummary is not null) status.Text += "\n" + completionSummary();
            if (openAfter && destination is not null)
            {
                try { OpenFolder(destination); }
                catch (Exception ex) { status.Text = T("The operation completed, but the destination folder could not be opened: ") + ex.Message + T(" Use the folder button to retry."); }
            }
        }
        catch (OperationCanceledException) { WasCancelled = true; dialog.Title = T("已取消"); status.Text = T("已完成的文件会保留，当前未完成的文件已清理。"); }
        catch (Exception ex) { LastError = ex; dialog.Title = T("Operation failed"); status.Text = ex.Message + T("\nCheck the file, password and destination, then try again."); }
        finally
        {
            running = false; timer.Stop(); clock.Stop(); activeClock.Stop(); rate.Text = "";
            elapsed.Text = T("用时  ") + clock.Elapsed.ToString(@"hh\:mm\:ss");
            bar.IsIndeterminate = false;
            dialog.SecondaryButtonText = ""; dialog.CloseButtonText = T("关闭");
        }
        if (autoClose) dialog.Hide();
        await showing;
        return succeeded;
    }

    public static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(T("The destination folder does not exist yet."));
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "\"" + Path.GetFullPath(path) + "\"", UseShellExecute = true });
    }
}
