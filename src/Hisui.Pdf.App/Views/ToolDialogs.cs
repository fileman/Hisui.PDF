using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Hisui.Pdf.App.Localization;
using Hisui.Pdf.App.Services;
using Hisui.Pdf.App.ViewModels;
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.Views;

/// <summary>Digital signature, OCR languages and batch dialogs, built on <see cref="FormDialog"/>.</summary>
internal static class ToolDialogs
{
    // ── Digital signature ─────────────────────────────────────────────────────

    public static async Task<SignRequest?> PromptSignatureAsync(Window owner, ISignatureService library, bool invisible)
    {
        var loc = Localizer.Instance;
        var dialog = new FormDialog(loc["Sign.Title"], 540);
        dialog.AddText(loc["Sign.Intro"], hint: true);

        // Certificates usable for signing: personal store (smart cards included), with a private key, currently valid.
        var certs = new List<X509Certificate2>();
        if (OperatingSystem.IsWindows())
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            certs.AddRange(store.Certificates.Where(c => c.HasPrivateKey && c.NotAfter > DateTime.Now && c.NotBefore < DateTime.Now && CanSignDocuments(c)));
        }
        static string Label(X509Certificate2 c) =>
            $"{c.GetNameInfo(X509NameType.SimpleName, false)} — {c.GetNameInfo(X509NameType.SimpleName, true)} — {c.NotAfter:d}";
        var choices = new ObservableCollection<string>(certs.Select(Label)) { loc["Sign.FromFile"] };
        var pick = dialog.Add(loc["Sign.Certificate"], new ComboBox { ItemsSource = choices, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch });
        var pfxPath = dialog.Add(loc["Sign.PfxFile"], new TextBox { IsReadOnly = true, PlaceholderText = loc["Sign.PfxHint"] });
        var browse = dialog.Add(null, new Button { Content = loc["Sign.Browse"], HorizontalAlignment = HorizontalAlignment.Left });
        var password = dialog.Add(loc["Sign.PfxPassword"], new TextBox { PasswordChar = '•' });
        void Sync()
        {
            var fromFile = pick.SelectedIndex == choices.Count - 1;
            pfxPath.IsEnabled = browse.IsEnabled = password.IsEnabled = fromFile;
        }
        pick.SelectionChanged += (_, _) => Sync();
        Sync();
        browse.Click += async (_, _) =>
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = loc["Sign.PfxFile"],
                FileTypeFilter = [new FilePickerFileType(loc["Sign.PfxType"]) { Patterns = ["*.pfx", "*.p12"] }],
            });
            if (files.Count > 0) pfxPath.Text = files[0].TryGetLocalPath();
        };

        var reason = dialog.Add(loc["Sign.Reason.Label"], new TextBox { Text = loc["Sign.DefaultReason"] });
        var location = dialog.Add(loc["Sign.Location.Label"], new TextBox());
        var contact = dialog.Add(loc["Sign.Contact.Label"], new TextBox());
        ComboBox? image = null;
        if (!invisible && library.Signatures.Count > 0)
            image = dialog.Add(loc["Sign.Image"], new ComboBox
            {
                ItemsSource = new[] { loc["Sign.NoImage"] }.Concat(library.Signatures.Select(s => s.Name)).ToList(),
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            });
        var tsa = dialog.Add(loc["Sign.Tsa"], new TextBox { PlaceholderText = "https://freetsa.org/tsr" });
        dialog.AddText(loc["Sign.TsaHint"], hint: true);
        var error = dialog.AddText("", hint: true);

        X509Certificate2? chosen = null;
        var ok = await dialog.ShowAsync(owner, loc["Sign.SignAndSave"], () =>
        {
            error.Text = "";
            if (!string.IsNullOrWhiteSpace(tsa.Text) && !(Uri.TryCreate(tsa.Text.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https"))
            { error.Text = loc["Sign.TsaInvalid"]; return false; }
            if (pick.SelectedIndex < certs.Count) { chosen = certs[pick.SelectedIndex]; return true; }
            if (string.IsNullOrWhiteSpace(pfxPath.Text)) { error.Text = loc["Sign.PfxMissing"]; return false; }
            try
            {
                chosen = X509CertificateLoader.LoadPkcs12FromFile(pfxPath.Text, password.Text, X509KeyStorageFlags.EphemeralKeySet);
                if (!chosen.HasPrivateKey) { error.Text = loc["Sign.NoKey"]; return false; }
                return true;
            }
            catch (CryptographicException) { error.Text = loc["Sign.PfxBad"]; return false; }
        });
        if (!ok || chosen is null) return null;

        byte[]? imageBytes = null;
        if (image is { SelectedIndex: > 0 }) imageBytes = library.LoadImage(library.Signatures[image.SelectedIndex - 1]);
        return new SignRequest
        {
            Certificate = chosen,
            Reason = NullIfBlank(reason.Text),
            Location = NullIfBlank(location.Text),
            ContactInfo = NullIfBlank(contact.Text),
            TimestampServer = string.IsNullOrWhiteSpace(tsa.Text) ? null : new Uri(tsa.Text.Trim()),
            Image = imageBytes,
        };
    }

    // Extended key usages meant for signing documents: any purpose, e-mail (S/MIME), Microsoft document signing,
    // Adobe authentic documents. Certificates restricted to TLS (localhost, device and Entra ID certificates) are hidden.
    private static readonly HashSet<string> DocumentSigningUsages =
        ["2.5.29.37.0", "1.3.6.1.5.5.7.3.4", "1.3.6.1.4.1.311.10.3.12", "1.2.840.113583.1.1.5", "1.3.6.1.5.5.7.3.36"];

    private static bool CanSignDocuments(X509Certificate2 c)
    {
        // Self-signed certificates in the store are development/test ones; a real one can still be used as a file.
        if (c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData)) return false;
        if (c.Extensions.OfType<X509KeyUsageExtension>().Any(k => (k.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) == 0))
            return false;
        var eku = c.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return eku is null || eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(o => o.Value is { } v && DocumentSigningUsages.Contains(v));
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ── OCR languages ─────────────────────────────────────────────────────────

    public static async Task<IReadOnlyList<string>?> PromptOcrLanguagesAsync(Window owner, IOcrLanguageStore store, IReadOnlyList<string> current)
    {
        var loc = Localizer.Instance;
        var dialog = new FormDialog(loc["Ocr.Title"], 560);
        dialog.AddText(loc["Ocr.Intro"], hint: true);
        var selected = new HashSet<string>(current);
        var list = dialog.Add(null, new StackPanel { Spacing = 2 });
        var error = dialog.AddText("", hint: true);

        void Fill()
        {
            list.Children.Clear();
            foreach (var lang in store.List())
            {
                var use = new CheckBox { Content = $"{lang.Name} ({lang.Code})", IsChecked = selected.Contains(lang.Code), IsEnabled = lang.Installed, MinWidth = 250 };
                use.IsCheckedChanged += (_, _) => { if (use.IsChecked == true) selected.Add(lang.Code); else selected.Remove(lang.Code); };
                var status = new TextBlock { Classes = { "dialog-hint" }, VerticalAlignment = VerticalAlignment.Center, Width = 120,
                    Text = lang.Bundled ? loc["Ocr.Bundled"] : lang.Installed ? loc["Ocr.Installed"] : "" };
                var row = new DockPanel { LastChildFill = false };
                row.Children.Add(use);
                row.Children.Add(status);
                if (!lang.Bundled)
                {
                    var action = new Button { Content = loc[lang.Installed ? "Ocr.Remove" : "Ocr.Download"], Padding = new Thickness(10, 2) };
                    ToolTip.SetTip(action, lang.Installed ? null : store.SourceFor(lang.Code).ToString());
                    DockPanel.SetDock(action, Dock.Right);
                    action.Click += async (_, _) =>
                    {
                        error.Text = "";
                        if (lang.Installed) { store.Remove(lang.Code); selected.Remove(lang.Code); Fill(); return; }
                        action.IsEnabled = false;
                        try
                        {
                            await store.InstallAsync(lang.Code, new Progress<double>(p => status.Text = $"{p:P0}"));
                            selected.Add(lang.Code);
                            Fill();
                        }
                        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
                        {
                            error.Text = loc.Format("Ocr.DownloadFailed", lang.Name, ex.Message);
                            action.IsEnabled = true;
                            status.Text = "";
                        }
                    };
                    row.Children.Add(action);
                }
                list.Children.Add(row);
            }
        }
        Fill();

        var import = dialog.Add(null, new Button { Content = loc["Ocr.Import"], HorizontalAlignment = HorizontalAlignment.Left });
        import.Click += async (_, _) =>
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = loc["Ocr.Import"],
                FileTypeFilter = [new FilePickerFileType("Tesseract") { Patterns = ["*.traineddata"] }],
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
            try { selected.Add(await store.ImportAsync(path)); Fill(); error.Text = ""; }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException) { error.Text = ex.Message; }
        };

        var ok = await dialog.ShowAsync(owner, validate: () =>
        {
            var usable = store.List().Where(l => l.Installed).Select(l => l.Code).ToHashSet();
            selected.IntersectWith(usable);
            if (selected.Count == 0) { error.Text = loc["Ocr.PickOne"]; return false; }
            return true;
        });
        // Keep a stable order: the previous choice first, then the new ones.
        return ok ? current.Where(selected.Contains).Concat(selected.Except(current).Order()).ToList() : null;
    }

    // ── Batch ─────────────────────────────────────────────────────────────────

    public static async Task<BatchRequest?> PromptBatchAsync(Window owner)
    {
        var loc = Localizer.Instance;
        var dialog = new FormDialog(loc["Batch.Title"], 560);
        var files = new ObservableCollection<string>();
        var list = dialog.Add(loc["Batch.Files"], new ListBox
        {
            ItemsSource = files, Height = 180,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((path, _) =>
                new TextBlock { Text = Path.GetFileName(path), [ToolTip.TipProperty] = path }),
        });
        var buttons = dialog.Add(null, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 });
        var add = new Button { Content = loc["Batch.AddFiles"] };
        var remove = new Button { Content = loc["Batch.RemoveFile"] };
        buttons.Children.Add(add);
        buttons.Children.Add(remove);
        add.Click += async (_, _) =>
        {
            var picked = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = loc["Batch.AddFiles"], AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType(loc["Picker.PdfType"]) { Patterns = ["*.pdf"] }],
            });
            foreach (var path in picked.Select(f => f.TryGetLocalPath()).OfType<string>())
                if (!files.Contains(path)) files.Add(path);
        };
        remove.Click += (_, _) => { if (list.SelectedItem is string s) files.Remove(s); };

        var actions = Enum.GetValues<BatchAction>();
        var action = dialog.Add(loc["Batch.Action"], new ComboBox
        {
            ItemsSource = actions.Select(a => loc["Batch.Action." + a]).ToList(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch,
        });
        var folder = dialog.Add(loc["Batch.Output"], new TextBox()); // pick it, or type/paste a path
        var pickFolder = dialog.Add(null, new Button { Content = loc["Batch.ChooseFolder"], HorizontalAlignment = HorizontalAlignment.Left });
        pickFolder.Click += async (_, _) =>
        {
            var picked = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = loc["Batch.Output"] });
            if (picked.Count > 0) folder.Text = picked[0].TryGetLocalPath();
        };
        dialog.AddText(loc["Batch.Hint"], hint: true);
        var error = dialog.AddText("", hint: true);

        var ok = await dialog.ShowAsync(owner, loc["Batch.Start"], () =>
        {
            if (files.Count == 0) { error.Text = loc["Batch.NoFiles"]; return false; }
            if (string.IsNullOrWhiteSpace(folder.Text) || !Directory.Exists(folder.Text)) { error.Text = loc["Batch.NoFolder"]; return false; }
            return true;
        });
        return ok ? new BatchRequest(files.ToList(), actions[Math.Max(0, action.SelectedIndex)], folder.Text!) : null;
    }
}
