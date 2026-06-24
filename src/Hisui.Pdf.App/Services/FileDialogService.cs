using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Hisui.Pdf.App.Localization;

namespace Hisui.Pdf.App.Services;

internal sealed class FileDialogService : IFileDialogService
{
    private readonly ILocalizer _loc;

    public FileDialogService(ILocalizer localizer) => _loc = localizer;

    // Built per call (not cached) so the title/filter follow a runtime language switch.
    private FilePickerFileType PdfType =>
        new(_loc["Picker.PdfType"]) { Patterns = ["*.pdf"] };

    private FilePickerFileType ImageType =>
        new(_loc["Picker.Images"]) { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.tiff"] };

    public async Task<string?> OpenPdfAsync()
    {
        var sp = GetStorageProvider();
        if (sp is null) return null;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _loc["Picker.OpenPdf"],
            AllowMultiple = false,
            FileTypeFilter = [PdfType],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SavePdfAsync(string suggestedFileName)
    {
        var sp = GetStorageProvider();
        if (sp is null) return null;

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = _loc["Picker.SavePdf"],
            SuggestedFileName = suggestedFileName,
            DefaultExtension = ".pdf",
            FileTypeChoices = [PdfType],
        });

        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync()
    {
        var sp = GetStorageProvider();
        if (sp is null) return null;

        var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = _loc["Picker.ChooseFolder"],
            AllowMultiple = false,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> OpenImageAsync()
    {
        var sp = GetStorageProvider();
        if (sp is null) return null;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _loc["Picker.ChooseImage"],
            AllowMultiple = false,
            FileTypeFilter = [ImageType],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private static IStorageProvider? GetStorageProvider()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return TopLevel.GetTopLevel(desktop.MainWindow)?.StorageProvider;
        return null;
    }
}
