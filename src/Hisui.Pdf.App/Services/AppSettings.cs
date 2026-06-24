namespace Hisui.Pdf.App.Services;

public sealed class AppSettings
{
    public List<string> RecentFiles { get; set; } = [];
    public int MaxRecentFiles { get; set; } = 10;
    public int PreviewDpi { get; set; } = 150;

    /// <summary>UI language code (e.g. "it", "en"). Null = use the base language.</summary>
    public string? Language { get; set; }

    /// <summary>UI theme code: "System" (follow the OS), "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";
}
