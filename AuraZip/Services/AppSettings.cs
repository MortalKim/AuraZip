using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AuraZip.Services;

public sealed class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AuraZip", "settings.json");
    public string Theme { get; set; } = "System";
    public string Language { get; set; } = "System";
    public bool UseArchiveDirectory { get; set; } = true;
    public string CustomDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public bool SmartExtraction { get; set; } = true;
    public bool OpenAfterExtraction { get; set; }
    public bool ShowFolderPane { get; set; } = true;
    public string RarEncoderPath { get; set; } = "";
    public bool RecordHistory { get; set; } = true;
    public List<string> RecentFiles { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize(File.ReadAllText(SettingsPath), AppJsonContext.Default.AppSettings) ?? new();
            if (!settings.RecordHistory && settings.RecentFiles.Count > 0) settings.ClearHistory();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        try
        {
            if (!RecordHistory) RecentFiles.Clear();
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, AppJsonContext.Default.AppSettings));
            File.Move(temp, SettingsPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    public void Remember(string path)
    {
        if (!RecordHistory) return;
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        RecentFiles = RecentFiles.Take(12).ToList();
        Save();
    }

    public void ClearHistory()
    {
        RecentFiles.Clear();
        Save();
    }
}
