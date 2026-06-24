using System.IO;
using System.Text.Json;

namespace Hisui.Pdf.App.Services;

internal sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppSettings Settings { get; } = Load();

    public void AddRecentFile(string path)
    {
        Settings.RecentFiles.Remove(path);
        Settings.RecentFiles.Insert(0, path);
        while (Settings.RecentFiles.Count > Settings.MaxRecentFiles)
            Settings.RecentFiles.RemoveAt(Settings.RecentFiles.Count - 1);
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataPaths.BaseDir);
            File.WriteAllText(AppDataPaths.SettingsFile,
                JsonSerializer.Serialize(Settings, JsonOptions));
        }
        catch { /* best-effort */ }
    }

    private static AppSettings Load()
    {
        try
        {
            if (!File.Exists(AppDataPaths.SettingsFile)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(AppDataPaths.SettingsFile)) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }
}
