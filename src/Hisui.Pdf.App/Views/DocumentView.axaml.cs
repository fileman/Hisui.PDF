using System.Diagnostics;
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
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.Views;

public partial class DocumentView : UserControl
{
    private Canvas? _annotCanvas;
    private readonly ISignatureService _signatures;
    private readonly IPrintService _print;
    private readonly IPdfRenderer _renderer;
    private readonly IUpdateService _updates;
    private readonly ISettingsService _settings;

    public DocumentView(MainViewModel viewModel, ISignatureService signatures, IPrintService print, IPdfRenderer renderer,
        IUpdateService updates, ISettingsService settings)
    {
        InitializeComponent();
        DataContext = viewModel;
        _signatures = signatures;
        _print = print;
        _renderer = renderer;
        _updates = updates;
        _settings = settings;

        // Lets the view model prompt for a password when opening an encrypted PDF.
        viewModel.RequestPasswordAsync = async () =>
        {
            var dialog = new PasswordDialog("Password.EnterPrompt");
            return await dialog.ShowDialog<bool>(Owner) ? dialog.Password : null;
        };

        viewModel.ShowFieldPropertiesDialog = props => FieldPropertiesDialog.ShowAsync(Owner, props);
        viewModel.PromptTextAsync = async (title, initial, multiline) =>
        {
            var dialog = new FormDialog(title);
            var box = dialog.Add(null, multiline
                ? new TextBox { Text = initial, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Height = 120 }
                : new TextBox { Text = initial });
            if (!multiline) box.SelectAll();
            dialog.Opened += (_, _) => box.Focus();
            return await dialog.ShowAsync(Owner) ? box.Text ?? "" : null;
        };
        viewModel.PromptLinkAsync = () => NavigationDialogs.PromptLinkAsync(Owner);
        viewModel.PromptHeaderFooterAsync = () => NavigationDialogs.PromptHeaderFooterAsync(Owner);
        viewModel.ShowReportAsync = (title, text) => NavigationDialogs.ShowReportAsync(Owner, title, text);
        viewModel.PromptRedactionPatternsAsync = () => NavigationDialogs.PromptRedactionPatternsAsync(Owner);
        viewModel.PromptProfileAsync = (initial, title, check) => ConformanceDialog.ShowAsync(Owner, initial, title, check);
        viewModel.PromptSignatureAsync = invisible => ToolDialogs.PromptSignatureAsync(Owner, _signatures, invisible);
        viewModel.PromptOcrLanguagesAsync = (store, current) => ToolDialogs.PromptOcrLanguagesAsync(Owner, store, current);
        viewModel.PromptBatchAsync = () => ToolDialogs.PromptBatchAsync(Owner);
        viewModel.ChooseAsync = (message, choices) => FormDialog.ChooseAsync(Owner, "Hisui PDF", message, choices);

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);

        // Keyboard shortcuts that target the view (not a VM command): print dialog, find, match nav, clear.
        KeyDown += OnWindowKeyDown;

        // Collapse/restore the side-panel columns with the VM flags.
        viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.IsToolPanelOpen) or nameof(MainViewModel.IsThumbnailPanelOpen):
                    ApplyPanelLayout();
                    break;
                case nameof(MainViewModel.SelectedPage) when !_followingScroll:
                    ScrollToSelectedPage();
                    break;
                case nameof(MainViewModel.ShowContinuousPages) or nameof(MainViewModel.ZoomLevel):
                    Avalonia.Threading.Dispatcher.UIThread.Post(ScrollToSelectedPage, Avalonia.Threading.DispatcherPriority.Loaded);
                    break;
                case nameof(MainViewModel.PreviewImage): // a cache invalidation re-rendered the page: refresh the stack too
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateContinuousPages(select: false), Avalonia.Threading.DispatcherPriority.Loaded);
                    break;
                case nameof(MainViewModel.PreviewDisplayHeight):
                    ApplyPendingEdge();
                    break;
            }
        };
        ApplyPanelLayout();

        // The recent list is shared by every tab and window: re-read it whenever this tab comes into view.
        AttachedToVisualTree += (_, _) => viewModel.RefreshRecentFiles();

        // Wire annotation canvas pointer events after layout is complete.
        Loaded += OnLoaded;
    }

    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    // Item height: 130 thumbnail + 6 gap + ~16 label + 10 padding + 4 selection border.
    private const double ThumbItemHeight = 166;

    private void UpdateThumbLayout()
    {
        if (DataContext is not MainViewModel vm || this.FindControl<ListBox>("ThumbList") is not { } list) return;
        if (list.ItemsPanelRoot is not WrapPanel wrap || list.Bounds.Width <= 0) return;

        var fits = vm.Pages.Count * ThumbItemHeight <= list.Bounds.Height;
        wrap.ItemWidth = Math.Floor(list.Bounds.Width / (fits ? 1 : 2));
    }

    // BodyGrid columns: 0 rail | 1 tool panel | 2 thumbnails | 3 splitter | 4 preview.
    private void ApplyPanelLayout()
    {
        if (DataContext is not MainViewModel vm) return;
        var cols = BodyGrid.ColumnDefinitions;
        cols[1].Width = new GridLength(vm.IsToolPanelOpen ? 220 : 0);
        cols[2].MinWidth = vm.IsThumbnailPanelOpen ? 200 : 0;
        cols[2].Width = new GridLength(vm.IsThumbnailPanelOpen ? 266 : 0);
        cols[3].Width = new GridLength(vm.IsThumbnailPanelOpen ? 5 : 0);
    }

    private bool _wired;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Loaded fires again each time the tab is re-attached; wire up only once.
        if (_wired) return;
        _wired = true;

        // Push the preview image's size to the VM so it can place search highlights in pixel space.
        if (this.FindControl<Image>("PreviewImageControl") is { } preview)
            preview.GetObservable(Visual.BoundsProperty).Subscribe(new AnonymousObserver<Rect>(b =>
            {
                if (DataContext is MainViewModel vm) vm.UpdateSearchOverlaySize(b.Width, b.Height);
            }));

        // Thumbnail strip: stack vertically while everything fits, switch to two columns when it would scroll.
        if (this.FindControl<ListBox>("ThumbList") is { } thumbs)
        {
            thumbs.GetObservable(Visual.BoundsProperty).Subscribe(new AnonymousObserver<Rect>(_ => UpdateThumbLayout()));
            if (DataContext is MainViewModel tvm)
                tvm.Pages.CollectionChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateThumbLayout);
        }

        // Track the scroll viewport (for fit-width/height) and handle the wheel (Ctrl = zoom, edge = page flip).
        foreach (var scroll in new[] { PreviewScroll, ContinuousScroll })
        {
            scroll.GetObservable(Visual.BoundsProperty).Subscribe(new AnonymousObserver<Rect>(b =>
            {
                if (DataContext is MainViewModel vm) vm.UpdateViewportSize(b.Width, b.Height);
            }));
            scroll.AddHandler(PointerWheelChangedEvent, OnPreviewWheel, RoutingStrategies.Tunnel);
        }
        PreviewScroll.ScrollChanged += (_, _) => _lastScrollTick = Environment.TickCount64;
        ContinuousScroll.ScrollChanged += (_, e) => UpdateContinuousPages(select: e.OffsetDelta.Y != 0 && !_suppressSelect);

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
        flyout.Items.Add(MakeItem(loc["Menu.Save"], vm.SaveCommand));
        flyout.Items.Add(MakeItem(loc["Menu.SaveAs"], vm.SaveAsCommand));
        flyout.Items.Add(MakeItem(loc["Menu.SaveAsProfile"], vm.SaveAsProfileCommand));
        flyout.Items.Add(new Separator());

        // Document tools (Acrobat-parity)
        flyout.Items.Add(MakeItem(loc["Menu.ExtractImages"], vm.ExtractImagesCommand));
        var convert = new MenuItem { Header = loc["Menu.Convert"], IsEnabled = vm.IsDocumentLoaded };
        foreach (var (key, format) in new[] { ("Menu.ConvertWord", "docx"), ("Menu.ConvertExcel", "xlsx"), ("Menu.ConvertPowerPoint", "pptx"), ("Menu.ConvertText", "txt"), ("Menu.ConvertImages", "png") })
            convert.Items.Add(new MenuItem { Header = loc[key], Command = vm.ConvertCommand, CommandParameter = format });
        flyout.Items.Add(convert);
        var propsItem = new MenuItem { Header = loc["Menu.DocumentProperties"], IsEnabled = vm.IsDocumentLoaded };
        propsItem.Click += OnDocumentPropertiesClick;
        flyout.Items.Add(propsItem);
        var protectItem = new MenuItem { Header = loc["Menu.Protect"], IsEnabled = vm.IsDocumentLoaded };
        protectItem.Click += OnProtectClick;
        flyout.Items.Add(protectItem);
        flyout.Items.Add(MakeItem(loc["Menu.RemovePassword"], vm.RemovePasswordCommand));
        if (FeatureFlags.DigitalSigning) flyout.Items.Add(MakeItem(loc["Menu.SignInvisible"], vm.SignInvisibleCommand));
        flyout.Items.Add(MakeItem(loc["Menu.VerifySignatures"], vm.VerifySignaturesCommand));
        flyout.Items.Add(MakeItem(loc["Menu.Batch"], vm.BatchCommand));
        flyout.Items.Add(MakeItem(loc["Menu.OcrLanguages"], vm.ChooseOcrLanguagesCommand));
        flyout.Items.Add(new Separator());

        vm.RefreshRecentFiles(); // another tab or window may have changed the shared list
        var recent = new MenuItem { Header = loc["Menu.Recent"], IsEnabled = vm.HasRecentFiles };
        foreach (var entry in vm.AllRecentFiles.Take(15)) // the full list is in the empty state
        {
            var item = new MenuItem
            {
                Header = entry.DisplayName,
                Command = vm.OpenRecentCommand,
                CommandParameter = entry.FullPath,
                // A leading star marks the pinned files.
                Icon = entry.IsPinned ? new TextBlock { Text = "★" } : null,
            };
            ToolTip.SetTip(item, entry.FullPath);
            recent.Items.Add(item);
        }
        if (vm.HasUnpinnedRecentFiles)
        {
            recent.Items.Add(new Separator());
            recent.Items.Add(MakeItem(loc["Recent.Clear"], vm.ClearRecentCommand));
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

        var theme = new MenuItem { Header = loc["Menu.Theme"] };
        foreach (var option in vm.Themes)
        {
            var item = new MenuItem
            {
                Header = loc[option.DisplayNameKey],
                Command = vm.SetThemeCommand,
                CommandParameter = option.Code,
                // A leading check marks the active theme.
                Icon = option.Code == vm.CurrentTheme ? new TextBlock { Text = "✓" } : null,
            };
            theme.Items.Add(item);
        }
        flyout.Items.Add(theme);

        // Re-enables the prompt a "Don't ask again" turned off.
        var confirmClose = new MenuItem
        {
            Header = loc["Menu.ConfirmCloseLastTab"],
            Icon = _settings.Settings.ConfirmCloseLastTab ? new TextBlock { Text = "✓" } : null,
        };
        confirmClose.Click += (_, _) =>
        {
            _settings.Settings.ConfirmCloseLastTab = !_settings.Settings.ConfirmCloseLastTab;
            _settings.Save();
        };
        flyout.Items.Add(confirmClose);
        flyout.Items.Add(new Separator());

        var updates = new MenuItem { Header = loc["Menu.CheckUpdates"] };
        updates.Click += async (_, _) => await CheckForUpdatesAsync(manual: true);
        flyout.Items.Add(updates);

        var log = new MenuItem { Header = loc["Menu.ViewLog"] };
        log.Click += async (_, _) => await new LogViewerDialog().ShowDialog(Owner);
        flyout.Items.Add(log);

        var info = new MenuItem { Header = loc["Menu.About"] };
        info.Click += OnAboutClick;
        flyout.Items.Add(info);

        flyout.Items.Add(MakeItem(loc["Menu.Exit"], vm.ExitCommand));

        flyout.ShowAt(anchor);

        static MenuItem MakeItem(string header, ICommand command) =>
            new() { Header = header, Command = command };
    }

    // ── Keyboard shortcuts (view-targeted) ────────────────────────────────────

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        switch (e.Key)
        {
            case Key.P when ctrl:
                OnPrintClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.F when ctrl:
                this.FindControl<TextBox>("SearchBox")?.Focus();
                e.Handled = true;
                break;
            case Key.F3:
                var prev = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                var cmd = prev ? vm.PrevMatchCommand : vm.NextMatchCommand;
                if (cmd.CanExecute(null)) cmd.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape when !string.IsNullOrEmpty(vm.SearchQuery):
                vm.SearchQuery = string.Empty;
                e.Handled = true;
                break;
        }
    }

    // ── Zoom ──────────────────────────────────────────────────────────────────

    private void OnPreviewWheel(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Plain wheel scrolls as usual; in single-page view it also flips pages at the scroll edges.
            if (ReferenceEquals(sender, PreviewScroll)) FlipPageAtEdge(vm, e);
            return;
        }
        if (e.Delta.Y > 0) vm.ZoomInCommand.Execute(null);
        else if (e.Delta.Y < 0) vm.ZoomOutCommand.Execute(null);
        e.Handled = true;
    }

    // ── Wheel paging (single-page view) ───────────────────────────────────────

    private enum Edge { None, Top, Bottom }

    private long _lastScrollTick;
    private Edge _pendingEdge;

    private void FlipPageAtEdge(MainViewModel vm, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0 || vm.SelectedPage is null) return;
        var down = e.Delta.Y < 0;
        var atEdge = down
            ? PreviewScroll.Offset.Y + PreviewScroll.Viewport.Height >= PreviewScroll.Extent.Height - 1
            : PreviewScroll.Offset.Y <= 1;
        // The notch must arrive already at the edge, and not right after the scroll that got there.
        if (!atEdge || Environment.TickCount64 - _lastScrollTick < 250) return;

        var index = vm.Pages.IndexOf(vm.SelectedPage) + (down ? 1 : -1);
        if (index < 0 || index >= vm.Pages.Count) return;
        _pendingEdge = down ? Edge.Top : Edge.Bottom;
        _lastScrollTick = Environment.TickCount64;
        vm.SelectedPage = vm.Pages[index];
        e.Handled = true;
    }

    // Once the new page's preview has laid out, show its top (flipped forward) or bottom (flipped back).
    private void ApplyPendingEdge()
    {
        var edge = _pendingEdge;
        if (edge == Edge.None) return;
        _pendingEdge = Edge.None;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (edge == Edge.Top) PreviewScroll.ScrollToHome();
            else PreviewScroll.ScrollToEnd();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    // ── Continuous view ───────────────────────────────────────────────────────

    private const double ContinuousMargin = 16, PageGap = 12;

    private bool _followingScroll, _suppressSelect;

    // Rotation is a layout transform, so a sideways page takes its width as slot height.
    private static double SlotHeight(PageItemViewModel p) =>
        (Math.Abs(p.RotationAngle) % 180 > 1 ? p.DisplayWidth : p.DisplayHeight) + PageGap;

    /// <summary>Scrolls the stack so the selected page is at the top (thumbnail click, mode/zoom change).</summary>
    private void ScrollToSelectedPage()
    {
        if (DataContext is not MainViewModel { ShowContinuousPages: true, SelectedPage: { } selected } vm) return;
        var y = ContinuousMargin;
        foreach (var page in vm.Pages)
        {
            if (ReferenceEquals(page, selected)) break;
            y += SlotHeight(page);
        }
        _suppressSelect = true;
        ContinuousScroll.Offset = new Vector(ContinuousScroll.Offset.X, y);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _suppressSelect = false;
            UpdateContinuousPages(select: false);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Loads previews for pages near the viewport and, when scrolled, selects the most visible page.</summary>
    private void UpdateContinuousPages(bool select)
    {
        if (DataContext is not MainViewModel { ShowContinuousPages: true } vm) return;
        var viewport = ContinuousScroll.Viewport.Height;
        var top = ContinuousScroll.Offset.Y - ContinuousMargin;
        var bottom = top + viewport;
        PageItemViewModel? best = null;
        var bestVisible = double.MinValue;
        var y = 0.0;
        foreach (var page in vm.Pages)
        {
            var height = SlotHeight(page) - PageGap;
            var visible = Math.Min(y + height, bottom) - Math.Max(y, top);
            if (visible > bestVisible) { bestVisible = visible; best = page; }
            if (y + height > top - viewport && y < bottom + viewport) vm.EnsurePagePreview(page); // one viewport of look-ahead
            y += height + PageGap;
            if (y > bottom + viewport) break;
        }

        if (!select || best is null || ReferenceEquals(best, vm.SelectedPage)) return;
        _followingScroll = true;
        vm.SelectedPage = best;
        _followingScroll = false;
        ThumbList.ScrollIntoView(best);
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
                    var ok = await dialog.ShowDialog<bool>(Owner);
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
                    var picked = await picker.ShowDialog<bool>(Owner);
                    if (!picked || picker.SelectedImage is null) return null;
                    return new AnnotationInputModel
                    {
                        Tool = AnnotationTool.Signature,
                        ImageBytes = picker.SelectedImage,
                    };
                }

                var dialog = new AnnotationInputDialog(tool);
                var ok = await dialog.ShowDialog<bool>(Owner);
                return ok ? dialog.Result : null;
            });
        }
        catch (Exception ex)
        {
            vm.StatusMessage = Localizer.Instance.Format("Status.Error", ex.Message);
        }
    }

    // ── Print ─────────────────────────────────────────────────────────────────

    private async void OnPrintClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.IsDocumentLoaded) return;
        try
        {
            // No in-app print stack on this platform — hand off to the OS print path.
            if (!_print.SupportsSystemDialog)
            {
                if (vm.PrintCommand.CanExecute(null)) vm.PrintCommand.Execute(null);
                return;
            }

            var pdf = await vm.BuildCurrentDocumentAsync();
            if (pdf is null) return;

            var dialog = new PrintDialog(_print, _renderer, pdf, vm.Pages.Count, vm.CurrentPageIndex);
            if (await dialog.ShowDialog<bool>(Owner) && dialog.Result is not null)
                await vm.RunPrintJobAsync(pdf, dialog.Result);
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
            if (await dialog.ShowDialog<bool>(Owner) && dialog.Result is not null)
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
            if (await dialog.ShowDialog<bool>(Owner) && !string.IsNullOrEmpty(dialog.Password))
                await vm.ProtectAsync(dialog.Password);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = Localizer.Instance.Format("Status.Error", ex.Message);
        }
    }

    // ── Updates ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks GitHub for the latest release and, if it is newer, offers to open its download page.
    /// A startup check stays silent unless there is something new; a manual check always reports.
    /// </summary>
    public async Task CheckForUpdatesAsync(bool manual)
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;

            var update = await _updates.CheckForUpdateAsync();
            if (update is null)
            {
                if (manual) vm.StatusMessage = Localizer.Instance["Update.UpToDate"];
                return;
            }

            var settings = _settings;
            if (!manual && settings.Settings.SkippedUpdateTag == update.Tag) return;

            var choice = await new UpdateDialog(update, _updates.CurrentVersion).ShowDialog<UpdateChoice>(Owner);
            switch (choice)
            {
                case UpdateChoice.Download:
                    // Windows: fetch the MSI and hand it to Windows Installer (MajorUpgrade replaces this build).
                    if (OperatingSystem.IsWindows() && update.MsiUrl is not null
                        && await _updates.DownloadInstallerAsync(update) is { } msi)
                    {
                        // Elevate up front: AppLocker's default MSI rules allow unsigned packages only for Administrators,
                        // and the per-machine install needs elevation anyway. UAC cancel throws, so we stay open.
                        Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{msi}\"") { UseShellExecute = true, Verb = "runas" });
                        Owner.Close();
                    }
                    else
                        Process.Start(new ProcessStartInfo(update.Url) { UseShellExecute = true });
                    break;
                case UpdateChoice.Skip:
                    settings.Settings.SkippedUpdateTag = update.Tag;
                    settings.Save();
                    break;
            }
        }
        catch (Exception ex)
        {
            if (manual && DataContext is MainViewModel vm) vm.StatusMessage = Localizer.Instance.Format("Status.Error", ex.Message);
        }
    }

    // ── About dialog ─────────────────────────────────────────────────────────

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await new AboutDialog().ShowDialog(Owner);
        }
        catch (Exception ex)
        {
            if (DataContext is MainViewModel vm) vm.StatusMessage = Localizer.Instance.Format("Status.Error", ex.Message);
        }
    }

    // ── Drag-drop ────────────────────────────────────────────────────────────

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        var hasFiles = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        if (hasFiles && DataContext is MainViewModel vm) vm.IsDragOver = true;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.IsDragOver = false;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is MainViewModel dropVm) dropVm.IsDragOver = false;
        if (!e.DataTransfer.Contains(DataFormat.File)) return;
        if (DataContext is not MainViewModel vm) return;

        var files = e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>() ?? [];
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
