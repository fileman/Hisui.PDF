using System.IO;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Reactive;
using Hisui.Pdf.App.Localization;
using Hisui.Pdf.App.Services;
using Hisui.Pdf.App.ViewModels;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.Views;

public partial class MainWindow : Window
{
    private Canvas? _annotCanvas;
    private readonly ISignatureService _signatures;

    public MainWindow(MainViewModel viewModel, ISignatureService signatures)
    {
        InitializeComponent();
        DataContext = viewModel;
        _signatures = signatures;

        // Lets the view model prompt for a password when opening an encrypted PDF.
        viewModel.RequestPasswordAsync = async () =>
        {
            var dialog = new PasswordDialog("Password.EnterPrompt");
            return await dialog.ShowDialog<bool>(this) ? dialog.Password : null;
        };

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);

        // Wire annotation canvas pointer events after layout is complete.
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Push the preview image's size to the VM so it can place search highlights in pixel space.
        if (this.FindControl<Image>("PreviewImageControl") is { } preview)
            preview.GetObservable(Visual.BoundsProperty).Subscribe(new AnonymousObserver<Rect>(b =>
            {
                if (DataContext is MainViewModel vm) vm.UpdateSearchOverlaySize(b.Width, b.Height);
            }));

        // Track the scroll viewport (for fit-width/height) and enable Ctrl+wheel zoom.
        if (this.FindControl<ScrollViewer>("PreviewScroll") is { } scroll)
        {
            scroll.GetObservable(Visual.BoundsProperty).Subscribe(new AnonymousObserver<Rect>(b =>
            {
                if (DataContext is MainViewModel vm) vm.UpdateViewportSize(b.Width, b.Height);
            }));
            scroll.AddHandler(PointerWheelChangedEvent, OnPreviewWheel, RoutingStrategies.Tunnel);
        }

        _annotCanvas = this.FindControl<Canvas>("AnnotationCanvas");
        if (_annotCanvas is null) return;

        _annotCanvas.AddHandler(PointerPressedEvent,  OnAnnotationPressed,  RoutingStrategies.Tunnel);
        _annotCanvas.AddHandler(PointerMovedEvent,    OnAnnotationMoved,    RoutingStrategies.Tunnel);
        _annotCanvas.AddHandler(PointerReleasedEvent, OnAnnotationReleased, RoutingStrategies.Tunnel);
    }

    // ── File backstage menu ───────────────────────────────────────────────────

    // Built programmatically so command bindings and the (dynamic) recent-files list resolve
    // reliably — a declarative MenuFlyout would live in a popup namescope that complicates both.
    private void OnFileBackstageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not Control anchor) return;

        var loc = Localizer.Instance;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };

        flyout.Items.Add(MakeItem(loc["Menu.Open"], vm.OpenCommand));
        flyout.Items.Add(MakeItem(loc["Menu.OpenInNewWindow"], vm.OpenInNewWindowCommand));
        flyout.Items.Add(MakeItem(loc["Menu.NewWindow"], vm.NewWindowCommand));
        flyout.Items.Add(MakeItem(loc["Menu.AddFiles"], vm.AddFilesCommand));
        flyout.Items.Add(MakeItem(loc["Menu.SaveAs"], vm.SaveAsCommand));
        flyout.Items.Add(new Separator());

        // Document tools (Acrobat-parity)
        flyout.Items.Add(MakeItem(loc["Menu.ExtractImages"], vm.ExtractImagesCommand));
        var propsItem = new MenuItem { Header = loc["Menu.DocumentProperties"], IsEnabled = vm.IsDocumentLoaded };
        propsItem.Click += OnDocumentPropertiesClick;
        flyout.Items.Add(propsItem);
        var protectItem = new MenuItem { Header = loc["Menu.Protect"], IsEnabled = vm.IsDocumentLoaded };
        protectItem.Click += OnProtectClick;
        flyout.Items.Add(protectItem);
        flyout.Items.Add(new Separator());

        var recent = new MenuItem { Header = loc["Menu.Recent"], IsEnabled = vm.HasRecentFiles };
        foreach (var entry in vm.RecentFiles)
        {
            var item = new MenuItem
            {
                Header = entry.DisplayName,
                Command = vm.OpenRecentCommand,
                CommandParameter = entry.FullPath,
            };
            ToolTip.SetTip(item, entry.FullPath);
            recent.Items.Add(item);
        }
        flyout.Items.Add(recent);
        flyout.Items.Add(new Separator());

        var language = new MenuItem { Header = loc["Menu.Language"] };
        foreach (var option in vm.Languages)
        {
            var item = new MenuItem
            {
                Header = option.DisplayName,
                Command = vm.SetLanguageCommand,
                CommandParameter = option.Code,
                // A leading check marks the active language.
                Icon = option.Code == vm.CurrentLanguage ? new TextBlock { Text = "✓" } : null,
            };
            language.Items.Add(item);
        }
        flyout.Items.Add(language);
        flyout.Items.Add(new Separator());

        var info = new MenuItem { Header = loc["Menu.About"] };
        info.Click += OnAboutClick;
        flyout.Items.Add(info);

        flyout.Items.Add(MakeItem(loc["Menu.Exit"], vm.ExitCommand));

        flyout.ShowAt(anchor);

        static MenuItem MakeItem(string header, ICommand command) =>
            new() { Header = header, Command = command };
    }

    // ── Zoom ──────────────────────────────────────────────────────────────────

    private void OnPreviewWheel(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return; // plain wheel scrolls as usual
        if (e.Delta.Y > 0) vm.ZoomInCommand.Execute(null);
        else if (e.Delta.Y < 0) vm.ZoomOutCommand.Execute(null);
        e.Handled = true;
    }

    // ── Annotation pointer handlers ───────────────────────────────────────────

    private void OnAnnotationPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (!vm.IsAnnotationActive) return;
        e.Pointer.Capture(_annotCanvas);
        vm.OnPointerPressed(e.GetPosition(_annotCanvas));
        e.Handled = true;
    }

    private void OnAnnotationMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (!vm.IsDragging) return;
        vm.OnPointerMoved(e.GetPosition(_annotCanvas));
        e.Handled = true;
    }

    private async void OnAnnotationReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || _annotCanvas is null) return;
        if (!vm.IsAnnotationActive) return;

        e.Pointer.Capture(null);
        var pos = e.GetPosition(_annotCanvas);
        var canvasSize = _annotCanvas.Bounds.Size;
        e.Handled = true;

        try
        {
            if (vm.ActiveTool == AnnotationTool.TextEdit)
            {
                await vm.OnTextEditClickAsync(pos, canvasSize, async original =>
                {
                    var dialog = new AnnotationInputDialog(AnnotationTool.TextEdit, initialText: original);
                    var ok = await dialog.ShowDialog<bool>(this);
                    return ok ? dialog.InputText : null;
                });
                return;
            }

            await vm.OnPointerReleasedAsync(pos, canvasSize, async tool =>
            {
                // The Signature tool picks a reusable PNG from the library instead of the param dialog.
                if (tool == AnnotationTool.Signature)
                {
                    var picker = new SignatureManagerDialog(_signatures);
                    var picked = await picker.ShowDialog<bool>(this);
                    if (!picked || picker.SelectedImage is null) return null;
                    return new AnnotationInputModel
                    {
                        Tool = AnnotationTool.Signature,
                        ImageBytes = picker.SelectedImage,
                    };
                }

                var dialog = new AnnotationInputDialog(tool);
                var ok = await dialog.ShowDialog<bool>(this);
                return ok ? dialog.Result : null;
            });
        }
        catch (Exception ex)
        {
            vm.StatusMessage = Localizer.Instance.Format("Status.Error", ex.Message);
        }
    }

    // ── Document tools ────────────────────────────────────────────────────────

    private async void OnDocumentPropertiesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        try
        {
            var metadata = await vm.ReadMetadataAsync();
            if (metadata is null) return;
            var dialog = new MetadataDialog(metadata);
            if (await dialog.ShowDialog<bool>(this) && dialog.Result is not null)
                await vm.ApplyMetadataAsync(dialog.Result);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = Localizer.Instance.Format("Status.Error", ex.Message);
        }
    }

    private async void OnProtectClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        try
        {
            var dialog = new PasswordDialog("Password.SetPrompt");
            if (await dialog.ShowDialog<bool>(this) && !string.IsNullOrEmpty(dialog.Password))
                await vm.ProtectAsync(dialog.Password);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = Localizer.Instance.Format("Status.Error", ex.Message);
        }
    }

    // ── About dialog ─────────────────────────────────────────────────────────

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await new AboutDialog().ShowDialog(this);
        }
        catch (Exception ex)
        {
            if (DataContext is MainViewModel vm) vm.StatusMessage = Localizer.Instance.Format("Status.Error", ex.Message);
        }
    }

    // ── Drag-drop ────────────────────────────────────────────────────────────

    private static void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files)) return;
        if (DataContext is not MainViewModel vm) return;

        var files = e.Data.GetFiles()?.OfType<IStorageFile>() ?? [];
        var pdfs = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => p is not null &&
                        Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .ToArray();

        if (pdfs.Length == 0) return;

        try
        {
            await vm.DropFilesAsync(pdfs);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = Localizer.Instance.Format("Status.Error", ex.Message);
        }
    }
}
