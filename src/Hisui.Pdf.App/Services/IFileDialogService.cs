namespace Hisui.Pdf.App.Services;

/// <summary>Abstracts platform file-picker and folder-picker dialogs.</summary>
public interface IFileDialogService
{
    Task<string?> OpenPdfAsync();
    Task<IReadOnlyList<string>> OpenPdfsAsync();
    Task<string?> SavePdfAsync(string suggestedFileName);
    Task<string?> PickFolderAsync();
    /// <summary>Save-file picker for a non-PDF export (e.g. ".docx"); null if cancelled.</summary>
    Task<string?> SaveFileAsync(string suggestedFileName, string extension, string typeName);
    Task<string?> OpenImageAsync();
}
