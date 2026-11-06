using System.Diagnostics;
using Microsoft.Win32;

namespace AuraZip.Core;

public sealed class RarEncoderException(int exitCode, string details) : IOException("RAR encoder error " + exitCode + ": " + details)
{
    public int ExitCode { get; } = exitCode;
    public string Details { get; } = details;
}

/// <summary>Find a separately installed RAR encoder. No encoder is redistributed.</summary>
public static class RarEncoder
{
    public static string? Find(string? preferredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath)) return IsEncoder(preferredPath) ? Path.GetFullPath(preferredPath) : null;
        foreach (var path in Candidates().Distinct(StringComparer.OrdinalIgnoreCase))
            if (IsEncoder(path)) return Path.GetFullPath(path);
        return null;
    }

    public static bool IsEncoder(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || !Path.GetFileName(path).Equals("Rar.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
            var version = FileVersionInfo.GetVersionInfo(path);
            return version.ProductName == "WinRAR" && version.FileDescription == "Command line RAR" && version.FileMajorPart >= 5;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static IEnumerable<string> Candidates()
    {
        foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetEnvironmentVariable("ProgramW6432") })
            if (!string.IsNullOrEmpty(folder)) yield return Path.Combine(folder, "WinRAR", "Rar.exe");
        if (!OperatingSystem.IsWindows()) yield break;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            string? candidate = null;
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\WinRAR.exe");
                if (key?.GetValue("") is string executable && Path.IsPathFullyQualified(executable.Trim('"')))
                    candidate = Path.Combine(Path.GetDirectoryName(executable.Trim('"'))!, "Rar.exe");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            if (candidate is not null) yield return candidate;
        }
    }
}
