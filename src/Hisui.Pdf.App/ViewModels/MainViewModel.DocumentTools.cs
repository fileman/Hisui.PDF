using System.IO;
using CommunityToolkit.Mvvm.Input;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.ViewModels;

/// <summary>
/// Document-level tools that bring the app closer to Acrobat: export embedded images, edit document
/// properties (metadata), and password-protect / open encrypted PDFs. Dialog-driven tools expose
/// read/apply methods the view calls around its dialog; image export needs only a folder picker.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Set by the view: prompts for a password (e.g. to open an encrypted PDF). Returns null if cancelled.</summary>
    public Func<Task<string?>>? RequestPasswordAsync { get; set; }

    // ── Extract images ──────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task ExtractImagesAsync()
    {
        if (!HasDocument) return;

        var folder = await _dialogs.PickFolderAsync();
        if (folder is null) return;

        await RunBusyAsync(_loc["Status.ExtractingImages"], async ct =>
        {
            var current = await _pageService.BuildFromSessionAsync(_session!, ct);
            var images = await _imageExtractor.ExtractImagesAsync(current, pageIndex: null, ct);

            var count = 0;
            foreach (var image in images)
            {
                ct.ThrowIfCancellationRequested();
                var ext = image.Format.ToLowerInvariant() switch { "jpeg" => "jpg", "png" => "png", _ => "bin" };
                var file = Path.Combine(folder, $"immagine-p{image.PageIndex + 1:D3}-{++count:D3}.{ext}");
                await Task.Run(() => File.WriteAllBytes(file, image.Bytes), ct);
            }

            StatusMessage = count > 0
                ? _loc.Format("Status.ImagesExtracted", count, folder)
                : _loc["Status.NoImages"];
        });
    }

    // ── Document properties (metadata) — view shows the dialog around these ───

    public async Task<PdfMetadata?> ReadMetadataAsync()
    {
        if (!HasDocument) return null;
        var current = await _pageService.BuildFromSessionAsync(_session!);
        return await _metadata.ReadAsync(current);
    }

    public async Task ApplyMetadataAsync(PdfMetadata metadata)
    {
        if (!HasDocument) return;

        var path = await _dialogs.SavePdfAsync("documento.pdf");
        if (path is null) return;

        await RunBusyAsync(_loc["Status.SavingMetadata"], async ct =>
        {
            var current = await _pageService.BuildFromSessionAsync(_session!, ct);
            var updated = await _metadata.WriteAsync(current, metadata, ct);
            await Task.Run(() => File.WriteAllBytes(path, updated), ct);
            StatusMessage = _loc.Format("Status.Saved", Path.GetFileName(path));
        });
    }

    // ── Password protect (encrypt) — view supplies the password ───────────────

    public async Task ProtectAsync(string password)
    {
        if (!HasDocument || string.IsNullOrEmpty(password)) return;

        var path = await _dialogs.SavePdfAsync("documento-protetto.pdf");
        if (path is null) return;

        await RunBusyAsync(_loc["Status.Protecting"], async ct =>
        {
            var current = await _pageService.BuildFromSessionAsync(_session!, ct);
            var options = new PdfEncryptionOptions { UserPassword = password, OwnerPassword = password };
            var encrypted = await _security.EncryptAsync(current, options, ct);
            await Task.Run(() => File.WriteAllBytes(path, encrypted), ct);
            StatusMessage = _loc.Format("Status.Protected", Path.GetFileName(path));
        });
    }
}
