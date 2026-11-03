using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace AuraZip;

public sealed class ArchiveIconConverter : IValueConverter
{
    private static readonly SolidColorBrush FileBrush = new(Colors.DimGray);
    public static void UpdateTheme(bool dark) => FileBrush.Color = dark ? Colors.LightGray : Colors.DimGray;
    public object Convert(object value, Type targetType, object parameter, string language) => value is true
        ? new SolidColorBrush(Colors.Goldenrod) : FileBrush;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class FileTypeConverter : IValueConverter
{
    public static string GetText(AuraZip.Core.ArchiveItem item)
    {
        if (item.IsDirectory) return Services.Strings.T("文件夹");
        var extension = System.IO.Path.GetExtension(item.Name).TrimStart('.').ToUpperInvariant();
        return (extension.Length == 0 ? "" : extension + " ") + Services.Strings.T("文件");
    }
    public object Convert(object value, Type targetType, object parameter, string language) => value is AuraZip.Core.ArchiveItem item ? GetText(item) : "";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class ArchiveNameConverter : IValueConverter
{
    public static bool FullPath { get; set; }
    public object Convert(object value, Type targetType, object parameter, string language) => value is AuraZip.Core.ArchiveItem item ? (FullPath ? item.Key : item.Name) : "";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
