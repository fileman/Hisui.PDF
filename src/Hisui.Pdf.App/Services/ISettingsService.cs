namespace Hisui.Pdf.App.Services;

public interface ISettingsService
{
    AppSettings Settings { get; }

    /// <summary>Prepends <paramref name="path"/> to the recent-files list, deduplicates, trims to max, then persists.</summary>
    void AddRecentFile(string path);

    void Save();
}
