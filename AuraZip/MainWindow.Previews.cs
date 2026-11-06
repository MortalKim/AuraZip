using AuraZip.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;
using static AuraZip.Services.Strings;

namespace AuraZip;

public sealed partial class MainWindow
{
    private PdfDocument? _previewPdf;
    private IRandomAccessStream? _pdfInput;
    private uint _pdfPage;
    private bool _renderingPdf;
    private int _pdfRenderGeneration;
    private MediaPlayer? _previewPlayer;
    private MediaSource? _previewSource;
    private TemporaryWorkspace? _mediaWorkspace;
    private byte[]? _officeData;
    private string? _officeExtension;
    private readonly OfficePreviewProcess _officePreview = new();
    private OfficePreviewDocument? _officeDocument;
    private int _officePage;
    private string _officePreviewInfo = "";

    private void ResetRichPreview()
    {
        _pdfRenderGeneration++;
        _officeData = null; _officeExtension = null;
        _officeDocument = null; _officePage = 0; _officePreviewInfo = "";
        OfficePreviewControls.Visibility = OfficeLayout.Visibility = Visibility.Collapsed;
        OfficeLayout.Children.Clear(); PreviewText.Visibility = Visibility.Visible;
        OfficePreviewScroll.ChangeView(null, 0, null, true);
        OfficePasswordButton.Visibility = Visibility.Collapsed;
        _renderingPdf = false;
        _previewPdf = null;
        _pdfInput?.Dispose(); _pdfInput = null;
        PdfControls.Visibility = Visibility.Collapsed;
        PreviewMedia.SetMediaPlayer(null);
        _previewPlayer?.Dispose(); _previewPlayer = null;
        _previewSource?.Dispose(); _previewSource = null;
        PreviewMedia.Visibility = Visibility.Collapsed;
        _mediaWorkspace?.Dispose(); _mediaWorkspace = null;
    }
    private static bool IsRichPreview(string extension) => OfficePreview.Supported(extension) || extension is ".pdf"
        or ".mp3" or ".wav" or ".flac" or ".m4a" or ".wma" or ".aac" or ".mp4" or ".m4v" or ".mov" or ".wmv" or ".avi" or ".mkv";

    private async Task ShowRichPreviewAsync(ArchiveIndex index, ArchiveItem item, string extension, CancellationToken token)
    {
        bool office = OfficePreview.Supported(extension);
        bool pdf = extension == ".pdf";
        var data = await _archives.ReadPreviewAsync(index.FilePath, item.Key, _password, office ? 32 * 1024 * 1024 : pdf ? 64 * 1024 * 1024 : 256 * 1024 * 1024, token, index.CodePage);
        token.ThrowIfCancellationRequested();
        if (office)
        {
            _officePreviewInfo = PreviewInfo.Text;
            try
            {
                var document = await _officePreview.ReadDocumentAsync(data, extension, token);
                token.ThrowIfCancellationRequested();
                _officeDocument = document; RenderOfficeLayout();
            }
            catch (OfficePasswordException ex)
            {
                token.ThrowIfCancellationRequested();
                _officeData = data; _officeExtension = extension;
                PreviewText.Text = T(ex.Message);
                OfficePasswordButton.Visibility = Visibility.Visible; OfficePasswordButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                token.ThrowIfCancellationRequested();
                // Document decoding failures must not invalidate the archive password.
                PreviewText.Text = T("Preview unavailable: ") + DescribeError(ex) + T(" Extract the file to view it.");
            }
        }
        else if (pdf)
        {
            var input = new MemoryStream(data).AsRandomAccessStream();
            try
            {
                var document = await PdfDocument.LoadFromStreamAsync(input).AsTask(token);
                token.ThrowIfCancellationRequested();
                _pdfInput = input; input = null!;
                _previewPdf = document; _pdfPage = 0;
                PdfControls.Visibility = Visibility.Visible;
                await RenderPdfPageAsync(token);
            }
            finally { input?.Dispose(); }
        }
        else
        {
            var workspace = new TemporaryWorkspace();
            try
            {
                var path = Path.Combine(workspace.Path, Path.GetFileName(item.Key));
                await File.WriteAllBytesAsync(path, data, token);
                var file = await StorageFile.GetFileFromPathAsync(path);
                token.ThrowIfCancellationRequested();
                _mediaWorkspace = workspace; workspace = null!;
                var player = _previewPlayer = new MediaPlayer { AutoPlay = false };
                player.MediaFailed += (_, _) => DispatcherQueue.TryEnqueue(() => { if (_previewPlayer == player) PreviewText.Text = T("当前系统无法解码此媒体，请使用外部应用打开。"); });
                player.Source = _previewSource = MediaSource.CreateFromStorageFile(file);
                PreviewMedia.SetMediaPlayer(player);
                PreviewMedia.Visibility = Visibility.Visible;
                PreviewText.Text = "";
            }
            finally { workspace?.Dispose(); }
        }
    }

    private async void OfficePassword_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _dialogOpen || _officeData is not { } data || _officeExtension is not { } extension) return;
        var token = _previewCancellation?.Token ?? default;
        var password = new PasswordBox { Header = T("文档密码"), MinWidth = Math.Min(300, Math.Max(180, Root.ActualWidth - 100)) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(password, "OfficeDocumentPassword");
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = T("文档密码"), Content = password, PrimaryButtonText = T("预览"), CloseButtonText = T("取消"), DefaultButton = ContentDialogButton.Primary };
        _dialogOpen = true; ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        finally { _dialogOpen = false; }
        if (result != ContentDialogResult.Primary || token.IsCancellationRequested) return;
        OfficePasswordButton.IsEnabled = false; PreviewText.Text = T("正在读取…");
        var secret = password.Password; password.Password = "";
        try
        {
            var document = await _officePreview.ReadDocumentAsync(data, extension, token, secret);
            token.ThrowIfCancellationRequested();
            _officeDocument = document; RenderOfficeLayout();
            OfficePasswordButton.Visibility = Visibility.Collapsed;
            _officeData = null; _officeExtension = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) PreviewText.Text = DescribeError(ex); }
        finally { secret = ""; if (ReferenceEquals(_officeData, data)) OfficePasswordButton.IsEnabled = true; }
    }
    private async Task RenderPdfPageAsync(CancellationToken token)
    {
        var document = _previewPdf;
        if (document is null || _renderingPdf || document.PageCount == 0) return;
        _renderingPdf = true;
        int generation = _pdfRenderGeneration;
        PdfPrevious.IsEnabled = PdfNext.IsEnabled = false;
        try
        {
            using var page = document.GetPage(_pdfPage);
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = 600 }).AsTask(token);
            var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream).AsTask(token);
            token.ThrowIfCancellationRequested();
            if (_previewPdf != document) return;
            PreviewImage.Source = bitmap; PreviewImage.MaxHeight = 700; PreviewImage.Visibility = Visibility.Visible;
            PreviewText.Text = "";
            PdfPageNumber.Text = $"{_pdfPage + 1} / {document.PageCount}";
        }
        finally
        {
            if (_pdfRenderGeneration == generation) _renderingPdf = false;
            if (_previewPdf == document) { PdfPrevious.IsEnabled = _pdfPage > 0; PdfNext.IsEnabled = _pdfPage + 1 < document.PageCount; }
        }
    }
    private async void PdfPage_Click(object sender, RoutedEventArgs e)
    {
        if (_previewPdf is null || _renderingPdf) return;
        if (ReferenceEquals(sender, PdfPrevious) && _pdfPage > 0) _pdfPage--;
        else if (ReferenceEquals(sender, PdfNext) && _pdfPage + 1 < _previewPdf.PageCount) _pdfPage++;
        try { await RenderPdfPageAsync(_previewCancellation?.Token ?? default); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PreviewText.Text = T("Preview unavailable: ") + ex.Message; }
    }
}
