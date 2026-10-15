using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppRemote.Windows;

namespace AppRemote;

internal enum VideoPreference { Auto, H265, H264, Jpeg }
internal enum TouchInputMode { Direct, Cursor }
internal enum SourceKind { Windows, Android }

internal sealed class AppSettings
{
    public bool RestoreLastWindow { get; set; }
    public SourceKind LastSource { get; set; }
    public string? LastAndroidSerial { get; set; }
    public string? AdbPath { get; set; }
    public string? AndroidAddress { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter<VideoPreference>))]
    public VideoPreference AndroidVideoMode { get; set; } = VideoPreference.Auto;
    public int Port { get; set; } = 8765;
    [JsonConverter(typeof(JsonStringEnumConverter<VideoPreference>))]
    public VideoPreference VideoMode { get; set; } = VideoPreference.Auto;
    [JsonConverter(typeof(JsonStringEnumConverter<TouchInputMode>))]
    public TouchInputMode TouchMode { get; set; } = TouchInputMode.Direct;
    public bool BackgroundInput { get; set; }
    public string? LastExecutablePath { get; set; }
    public string? LastWindowTitle { get; set; }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppRemote", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings()
                : new AppSettings();
            if (!Enum.IsDefined(settings.VideoMode)) settings.VideoMode = VideoPreference.Auto;
            if (!Enum.IsDefined(settings.AndroidVideoMode) || settings.AndroidVideoMode == VideoPreference.Jpeg)
                settings.AndroidVideoMode = VideoPreference.Auto;
            if (!Enum.IsDefined(settings.LastSource)) settings.LastSource = SourceKind.Windows;
            if (!Enum.IsDefined(settings.TouchMode)) settings.TouchMode = TouchInputMode.Direct;
            if (settings.Port is < 1 or > 65535) settings.Port = 8765;
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        var path = SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this));
        File.Move(temporaryPath, path, true);
    }

    public bool Remember(WindowEntry entry)
    {
        LastSource = SourceKind.Windows;
        var executablePath = GetExecutablePath(entry);
        if (executablePath is null) return false;
        LastExecutablePath = executablePath;
        LastWindowTitle = entry.Title;
        return true;
    }

    public void RememberAndroid(string serial)
    {
        LastSource = SourceKind.Android;
        LastAndroidSerial = serial;
    }

    public void Forget()
    {
        LastExecutablePath = null;
        LastWindowTitle = null;
        LastAndroidSerial = null;
    }

    public WindowEntry? FindLastWindow(IEnumerable<WindowEntry> windows)
    {
        if (string.IsNullOrWhiteSpace(LastExecutablePath)) return null;
        var candidates = windows.Where(window =>
            string.Equals(GetExecutablePath(window), LastExecutablePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        var titleMatches = candidates.Where(window =>
            string.Equals(window.Title, LastWindowTitle, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (titleMatches.Length == 1) return titleMatches[0];
        return titleMatches.Length == 0 && candidates.Length == 1 ? candidates[0] : null;
    }

    private static string? GetExecutablePath(WindowEntry entry)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)entry.ProcessId));
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
