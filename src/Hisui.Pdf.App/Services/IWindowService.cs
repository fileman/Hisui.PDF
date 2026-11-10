namespace Hisui.Pdf.App.Services;

/// <summary>Opens additional top-level document windows, so several PDFs can be worked on side by side.</summary>
public interface IWindowService
{
    /// <summary>Shows a new document tab, optionally loading <paramref name="filePath"/> into it.</summary>
    void OpenWindow(string? filePath = null);

    /// <summary>Brings forward the tab that already holds <paramref name="filePath"/> (in any window); false when
    /// the file is not open, so the caller opens it.</summary>
    bool ActivateExisting(string filePath);
}
