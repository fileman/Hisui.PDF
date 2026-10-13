namespace Hisui.Pdf.App.Services;

public sealed class AppSettings
{
    public List<string> RecentFiles { get; set; } = [];

    /// <summary>Files pinned to the top of the recent list, in pin order. Never trimmed.</summary>
    public List<string> PinnedFiles { get; set; } = [];

    /// <summary>When each recent or pinned file was last opened (shown in the recent list).</summary>
    public Dictionary<string, DateTime> RecentOpened { get; set; } = [];

    /// <summary>Cap on the (unpinned) recent files; the list is paged, so it can be long.</summary>
    public int MaxRecentFiles { get; set; } = 30;
    public int PreviewDpi { get; set; } = 150;

    /// <summary>UI language code (e.g. "it", "en"). Null = use the base language.</summary>
    public string? Language { get; set; }

    /// <summary>Check GitHub for a newer release at startup.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Release tag the user chose to skip; not offered again at startup.</summary>
    public string? SkippedUpdateTag { get; set; }

    /// <summary>UI theme code: "System" (follow the OS), "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Page view: false = one page at a time (wheel flips pages), true = pages stacked in one scroll.</summary>
    public bool ContinuousScroll { get; set; }

    /// <summary>Closing the last tab asks before closing the application.</summary>
    public bool ConfirmCloseLastTab { get; set; } = true;

    /// <summary>Form field alignment follows the last selected field instead of the first.</summary>
    public bool AlignToLastSelected { get; set; }

    /// <summary>OCR languages joined with '+', e.g. "ita+eng" (Tesseract syntax).</summary>
    public string? OcrLanguages { get; set; } = "ita+eng";
}
