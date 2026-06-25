namespace Hisui.Pdf.App.Services;

/// <summary>Opens additional top-level document windows, so several PDFs can be worked on side by side.</summary>
public interface IWindowService
{
    /// <summary>Shows a new independent document window, optionally loading <paramref name="filePath"/> into it.</summary>
    void OpenWindow(string? filePath = null);
}
