namespace Hisui.Pdf.App.Services;

/// <summary>Abstracts platform file-picker and folder-picker dialogs.</summary>
public interface IFileDialogService
{
    Task<string?> OpenPdfAsync();
    Task<string?> SavePdfAsync(string suggestedFileName);
    Task<string?> PickFolderAsync();
    Task<string?> OpenImageAsync();
}
