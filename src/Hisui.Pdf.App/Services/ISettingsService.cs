namespace Hisui.Pdf.App.Services;

public interface ISettingsService
{
    AppSettings Settings { get; }

    /// <summary>Prepends <paramref name="path"/> to the recent-files list (a pinned file keeps its place), deduplicates,
    /// trims to max, then persists.</summary>
    void AddRecentFile(string path);

    /// <summary>Removes <paramref name="path"/> from the recent and pinned files, then persists.</summary>
    void RemoveRecentFile(string path);

    /// <summary>Pins <paramref name="path"/> to the top of the list, or moves it back to the head of the recent files.</summary>
    void SetPinned(string path, bool pinned);

    /// <summary>Empties the recent files; pinned files stay.</summary>
    void ClearRecentFiles();

    void Save();
}
