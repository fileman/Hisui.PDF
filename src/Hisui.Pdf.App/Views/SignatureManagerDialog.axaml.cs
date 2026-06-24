using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Hisui.Pdf.App.Services;

namespace Hisui.Pdf.App.Views;

/// <summary>
/// Browses the saved-signature library: pick one to apply, create a new one, or delete one. On a
/// successful pick the chosen PNG bytes are exposed via <see cref="SelectedImage"/> and the dialog
/// closes with true.
/// </summary>
public partial class SignatureManagerDialog : Window
{
    private readonly ISignatureService _service;
    private readonly ObservableCollection<SignatureListItem> _items = [];

    public byte[]? SelectedImage { get; private set; }

    public SignatureManagerDialog(ISignatureService service)
    {
        _service = service;
        InitializeComponent();

        SignatureList.ItemsSource = _items;
        SignatureList.SelectionChanged += (_, _) => UpdateButtons();
        SignatureList.DoubleTapped += (_, _) => Use();
        NewButton.Click += async (_, _) => await NewAsync();
        DeleteButton.Click += (_, _) => DeleteSelected();
        UseButton.Click += (_, _) => Use();
        // Thumbnails are native-backed IDisposable bitmaps — release them when the dialog closes.
        Closed += (_, _) => DisposeThumbnails();

        Reload();
    }

    private void DisposeThumbnails()
    {
        foreach (var item in _items)
            item.Image?.Dispose();
    }

    private void Reload()
    {
        DisposeThumbnails(); // dispose the previous batch before dropping the references
        _items.Clear();
        foreach (var sig in _service.Signatures)
        {
            var bytes = _service.LoadImage(sig);
            Bitmap? bmp = null;
            if (bytes is not null)
            {
                using var ms = new MemoryStream(bytes);
                bmp = new Bitmap(ms);
            }
            _items.Add(new SignatureListItem { Id = sig.Id, Name = sig.Name, Image = bmp, Bytes = bytes });
        }
        EmptyLabel.IsVisible = _items.Count == 0;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var hasSelection = SignatureList.SelectedItem is SignatureListItem;
        DeleteButton.IsEnabled = hasSelection;
        UseButton.IsEnabled = hasSelection;
    }

    private async Task NewAsync()
    {
        var dialog = new SignatureCreateDialog();
        var ok = await dialog.ShowDialog<bool>(this);
        if (ok && dialog.ResultPng is not null && _service.Add(dialog.ResultPng, dialog.ResultName) is not null)
            Reload();
    }

    private void DeleteSelected()
    {
        if (SignatureList.SelectedItem is SignatureListItem item)
        {
            _service.Remove(item.Id);
            Reload();
        }
    }

    private void Use()
    {
        if (SignatureList.SelectedItem is SignatureListItem { Bytes: { } bytes })
        {
            SelectedImage = bytes;
            Close(true);
        }
    }
}

/// <summary>One signature row in the manager: its id/name plus a decoded thumbnail and the raw PNG bytes.</summary>
public sealed class SignatureListItem
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public Bitmap? Image { get; init; }
    public byte[]? Bytes { get; init; }
}
