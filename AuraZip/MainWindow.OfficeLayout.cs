using AuraZip.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using System;
using System.Linq;
using static AuraZip.Services.Strings;

namespace AuraZip;

public sealed partial class MainWindow
{
    private void OfficePreviewMode_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderOfficeLayout();
    private void OfficePage_Click(object sender, RoutedEventArgs e)
    {
        if (_officeDocument is null) return;
        _officePage = Math.Clamp(_officePage + (ReferenceEquals(sender, OfficeNext) ? 1 : -1), 0, Math.Max(0, _officeDocument.Sections.Count - 1));
        RenderOfficeLayout();
    }
    private void RenderOfficeLayout()
    {
        if (_officeDocument is not { } document || OfficePreviewControls is null) return;
        OfficePreviewScroll.ChangeView(null, 0, null, true);
        OfficePreviewControls.Visibility = Visibility.Visible;
        bool layout = OfficePreviewMode.SelectedIndex == 1 && document.Sections.Count > 0;
        PreviewInfo.Text = _officePreviewInfo + " · " + T(layout ? "版式预览" : "文本预览");
        OfficeLayout.Children.Clear(); OfficeLayout.Visibility = layout ? Visibility.Visible : Visibility.Collapsed;
        PreviewText.Visibility = layout ? Visibility.Collapsed : Visibility.Visible;
        PreviewText.Text = string.IsNullOrWhiteSpace(document.Text) ? T("文档没有可预览的文本。") : document.Text;
        OfficePageControls.Visibility = layout && document.Sections.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        OfficePreviewLimit.Visibility = document.IsTruncated ? Visibility.Visible : Visibility.Collapsed;
        OfficeSectionTitle.Text = ""; OfficeSectionTitle.Visibility = Visibility.Collapsed;
        if (!layout) return;
        _officePage = Math.Clamp(_officePage, 0, document.Sections.Count - 1);
        OfficePrevious.IsEnabled = _officePage > 0; OfficeNext.IsEnabled = _officePage + 1 < document.Sections.Count;
        OfficePageNumber.Text = $"{_officePage + 1} / {document.Sections.Count}";
        var section = document.Sections[_officePage]; OfficeSectionTitle.Text = section.Title;
        OfficeSectionTitle.Visibility = string.IsNullOrWhiteSpace(section.Title) ? Visibility.Collapsed : Visibility.Visible;
        int renderedCells = 0;
        foreach (var block in section.Blocks.Take(512))
        {
            if (block.Kind == "table" && block.Rows is { } rows)
            {
                var table = new Grid { ColumnSpacing = 0, RowSpacing = 0 };
                int columns = Math.Min(32, rows.Count == 0 ? 0 : rows.Max(r => r.Count));
                for (int c = 0; c < columns; c++) table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                for (int r = 0; r < Math.Min(256, rows.Count); r++)
                {
                    if (renderedCells >= 2048) { OfficePreviewLimit.Visibility = Visibility.Visible; break; }
                    table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    for (int c = 0; c < columns; c++)
                    {
                        if (++renderedCells > 2048) { OfficePreviewLimit.Visibility = Visibility.Visible; break; }
                        var text = new TextBlock { Text = c < rows[r].Count ? rows[r][c] : "", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 13, MinWidth = 50, MaxWidth = 240 };
                        var cell = new Border { Child = text, Padding = new Thickness(6), BorderThickness = new Thickness(0.5), BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"] };
                        Grid.SetRow(cell, r); Grid.SetColumn(cell, c); table.Children.Add(cell);
                    }
                }
                OfficeLayout.Children.Add(new ScrollViewer { Content = table, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled });
            }
            else
            {
                var rich = new RichTextBlock { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(rich, string.Concat((block.Runs ?? Array.Empty<OfficePreviewRun>()).Select(r => r.Text)));
                var paragraph = new Paragraph { TextAlignment = block.Alignment switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, "both" => TextAlignment.Justify, _ => TextAlignment.Left } };
                foreach (var run in block.Runs ?? Array.Empty<OfficePreviewRun>())
                    paragraph.Inlines.Add(new Run { Text = run.Text, FontSize = Math.Clamp(run.FontSize, 10, 48), FontWeight = run.Bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal, FontStyle = run.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal });
                rich.Blocks.Add(paragraph); OfficeLayout.Children.Add(rich);
            }
        }
    }
}
