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
        if (!Settings.PinnedFiles.Contains(path)) Settings.RecentFiles.Insert(0, path);
        Settings.RecentOpened[path] = DateTime.Now;
        TrimRecentFiles();
        Save();
    }

    public void RemoveRecentFile(string path)
    {
        Settings.RecentFiles.Remove(path);
        Settings.PinnedFiles.Remove(path);
        Settings.RecentOpened.Remove(path);
        Save();
    }

    public void SetPinned(string path, bool pinned)
    {
        Settings.RecentFiles.Remove(path);
        Settings.PinnedFiles.Remove(path);
        if (pinned) Settings.PinnedFiles.Add(path);
        else Settings.RecentFiles.Insert(0, path);
        TrimRecentFiles();
        Save();
    }

    public void ClearRecentFiles()
    {
        Settings.RecentFiles.Clear();
        TrimRecentFiles();
        Save();
    }

    private void TrimRecentFiles()
    {
        while (Settings.RecentFiles.Count > Math.Max(1, Settings.MaxRecentFiles))
            Settings.RecentFiles.RemoveAt(Settings.RecentFiles.Count - 1);
        foreach (var gone in Settings.RecentOpened.Keys.Where(p => !Settings.RecentFiles.Contains(p) && !Settings.PinnedFiles.Contains(p)).ToList())
            Settings.RecentOpened.Remove(gone);
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
