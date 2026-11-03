using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace AuraZip.Services;

public sealed class Strings : INotifyPropertyChanged
{
    public static Strings Current { get; } = new();
    public Strings() { if (Current is not null) Current.PropertyChanged += (_, e) => PropertyChanged?.Invoke(this, e); }
    private static readonly Dictionary<string, string> English = Load("en-US");
    private static readonly Dictionary<string, string> Chinese = Load("zh-CN");
    public static bool IsChinese { get; private set; } = true;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key] => T(key);

    public static string T(string key) => (IsChinese ? Chinese : English).TryGetValue(key, out var value) ? value : key;
    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

    public static void SetLanguage(string preference)
    {
        var languages = Windows.System.UserProfile.GlobalizationPreferences.Languages;
        var language = preference == "System" ? (languages.Count > 0 ? languages[0] : CultureInfo.InstalledUICulture.Name) : preference;
        IsChinese = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var culture = CultureInfo.GetCultureInfo(IsChinese ? "zh-CN" : "en-US");
        CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
        Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = culture.Name;
        Current.PropertyChanged?.Invoke(Current, new PropertyChangedEventArgs("Item[]"));
        // A small shared file lets the native shell extension follow the saved preference.
        var folder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        try { File.WriteAllText(Path.Combine(folder, "shell-language.txt"), preference == "System" ? "System" : culture.Name); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    private static Dictionary<string, string> Load(string language)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"AuraZip.Resources.{language}.json")
            ?? throw new InvalidOperationException($"Language resources are missing: {language}.");
        return JsonSerializer.Deserialize(stream, AppJsonContext.Default.DictionaryStringString)!;
    }
}
