using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace AuraZip;

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
