using System.ComponentModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Reactive;
using Avalonia.VisualTree;
using Hisui.Pdf.App.Services;
using Hisui.Pdf.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Hisui.Pdf.App.Views;

/// <summary>Tabbed shell: every open document lives in its own <see cref="DocumentView"/> tab.</summary>
public partial class MainWindow : Window
{
    private readonly IServiceProvider _services;
    private readonly HashSet<DocumentView> _claimed = [];
    private Control? _captionSpacer;

    // Split view: two tabs shown at once. Documents live in the panes (not in TabItem.Content), reachable via TabItem.Tag.
    private Grid? _splitHost;
    private Border? _paneA, _paneB;
    private GridSplitter? _splitter;
    private TabItem? _splitLeft, _splitRight;
    private bool _splitStacked;

    // Windows are peers: tabs can be moved between them and the app exits when the last one closes.
    private static readonly List<MainWindow> Open = [];
    private readonly Dictionary<TabItem, PropertyChangedEventHandler> _hooks = [];

    /// <summary>The window the user is working in (falls back to the main window).</summary>
    public static MainWindow? Active => Open.FirstOrDefault(w => w.IsActive) ?? Open.FirstOrDefault();

    /// <summary>Exit: asks about unsaved changes in every window, then closes them all.</summary>
    public static async Task ExitAsync()
    {
        var all = Open.ToList();
        foreach (var w in all)
            if (!await w.ConfirmAllAsync()) return;
        foreach (var w in all) { w._closeConfirmed = true; w.Close(); }
    }
    private TabItem? _previousTab; // tab selected before the current one (right-click selects the clicked tab first)

    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        Tabs.SelectionChanged += (_, e) =>
        {
            if (e.RemovedItems.OfType<TabItem>().FirstOrDefault() is { } prev && Tabs.Items.Contains(prev)) _previousTab = prev;
            if (_splitLeft is not null && Tabs.SelectedItem is TabItem sel && sel != _splitLeft && sel != _splitRight)
            {
                // Picking a tab outside the split replaces the pane that was active.
                var old = e.RemovedItems.OfType<TabItem>().FirstOrDefault();
                if (old is not null && old == _splitRight) _splitRight = sel; else _splitLeft = sel;
            }
            LayoutPanes();
            UpdateWindowTitle();
            (Tabs.SelectedItem as Control)?.BringIntoView();
        };
        Tabs.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<Control>("PART_NewTab") is Button add) add.Click += (_, _) => OpenInTab();
            _captionSpacer = e.NameScope.Find<Control>("PART_CaptionSpacer");
            _splitHost = e.NameScope.Find<Grid>("PART_SplitHost");
            _paneA = e.NameScope.Find<Border>("PART_PaneA");
            _paneB = e.NameScope.Find<Border>("PART_PaneB");
            _splitter = e.NameScope.Find<GridSplitter>("PART_Splitter");
            // Clicking inside a pane makes its document the active one (keyboard shortcuts, title, ribbon).
            foreach (var pane in new[] { _paneA, _paneB })
                pane?.AddHandler(PointerPressedEvent, (_, _) =>
                {
                    if (pane.Tag is TabItem t && Tabs.SelectedItem != t) Tabs.SelectedItem = t;
                }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            LayoutPanes();
            // The wheel scrolls the tab strip sideways when it overflows.
            if (e.NameScope.Find<Control>("PART_TabScroll") is ScrollViewer strip)
                strip.PointerWheelChanged += (_, w) =>
                {
                    strip.Offset = strip.Offset.WithX(strip.Offset.X - (w.Delta.Y + w.Delta.X) * 60);
                    w.Handled = true;
                };
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Control) && Tabs.SelectedItem is TabItem t)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _ = CloseAllTabsAsync();
                else _ = CloseTabAsync(t);
                e.Handled = true;
            }
        };
        AddTab();
        Open.Add(this);
        Closed += (_, _) => Open.Remove(this);
        Closing += OnClosing;
        Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(FitCaptionButtons, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    // Avalonia 12 draws the title and caption buttons itself when the client area is extended, and exposes no API to
    // trim them. Hide the title text (our tabs fill the strip) and the full-screen button, then reserve exactly the
    // width the remaining caption buttons take, measured from the decoration overlay.
    private void FitCaptionButtons()
    {
        Visual root = this;
        while (root.GetVisualParent() is { } parent) root = parent;
        Control? Part(string name) => root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);

        if (Part("PART_TitleTextPanel") is { } title) title.IsVisible = false;
        if (Part("PART_FullScreenButton") is { } fullScreen) fullScreen.IsVisible = false;
        if (Part("PART_OverlayPanel") is { } buttons && _captionSpacer is { } spacer)
            buttons.GetObservable(BoundsProperty).Subscribe(new AnonymousObserver<Rect>(b => spacer.Width = b.Width));
    }

    /// <summary>The document view of the selected tab.</summary>
    public DocumentView? CurrentView => ViewOf(Tabs.SelectedItem as TabItem);

    private static DocumentView? ViewOf(TabItem? item) => item?.Tag as DocumentView;

    // Context-menu conversions can arrive together (one launch per selected file): ask for save locations one at a time.
    private static readonly SemaphoreSlim ConvertGate = new(1, 1);

    /// <summary>Handles command-line arguments: files to open, optionally preceded by <c>--convert docx|txt|png</c>
    /// (open the file, then export it).</summary>
    public void OpenArgs(IReadOnlyList<string> args)
    {
        string? convert = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--convert" && i + 1 < args.Count && args[i + 1] is "docx" or "txt" or "png") convert = args[++i];
            else if (File.Exists(args[i])) OpenInTab(args[i], convert);
        }
    }

    /// <summary>Opens <paramref name="path"/> in the selected tab when it is empty, otherwise in a new tab, and
    /// optionally exports it to <paramref name="convert"/>. With no path, adds an empty tab.</summary>
    public void OpenInTab(string? path = null, string? convert = null)
    {
        // Reuse an empty tab (the current one first) rather than leaving it in front of the documents. Loading is async,
        // so IsDocumentLoaded alone can't tell that a tab was just claimed by a previous file or is opening one itself.
        var empty = path is null
            ? null
            : Tabs.Items.OfType<TabItem>().OrderBy(t => ReferenceEquals(t, Tabs.SelectedItem) ? 0 : 1)
                .FirstOrDefault(t => ViewOf(t) is { } v && !_claimed.Contains(v)
                                     && v.DataContext is MainViewModel { IsDocumentLoaded: false, IsBusy: false });
        if (empty is not null) Tabs.SelectedItem = empty;
        var view = ViewOf(empty) ?? AddTab();
        if (path is not null && view.DataContext is MainViewModel vm)
        {
            _claimed.Add(view);
            _ = OpenAndConvertAsync(vm, path, convert);
        }
    }

    private static async Task OpenAndConvertAsync(MainViewModel vm, string path, string? convert)
    {
        await vm.OpenPathAsync(path);
        if (convert is null || !vm.IsDocumentLoaded) return;

        await ConvertGate.WaitAsync();
        try { await vm.ConvertAsync(convert); }
        finally { ConvertGate.Release(); }
    }

    private DocumentView AddTab()
    {
        var view = _services.GetRequiredService<DocumentView>();
        AttachView(view);
        return view;
    }

    /// <summary>Creates a new window that adopts an existing document view (a tab moved out of another window).</summary>
    private static MainWindow CreateDetached(IServiceProvider services, DocumentView view, MainWindow from)
    {
        var w = new MainWindow(services);
        foreach (var t in w.Tabs.Items.OfType<TabItem>().ToList()) w.Tabs.Items.Remove(t); // drop its initial empty tab
        w.AttachView(view);
        w.Width = from.Width;
        w.Height = from.Height;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Position = from.Position + new PixelPoint(40, 40);
        return w;
    }

    /// <summary>Moves a tab (document, unsaved changes and all) to a new window, or to <paramref name="target"/>.</summary>
    private void MoveTab(TabItem item, MainWindow? target)
    {
        if (ViewOf(item) is not { } view) return;
        if (target is null && Tabs.ItemCount < 2) return; // moving the only tab out would just rename the window
        if (item == _splitLeft || item == _splitRight) CloseSplit();
        _claimed.Remove(view);
        if (_hooks.Remove(item, out var hook) && view.DataContext is MainViewModel vm) vm.PropertyChanged -= hook;
        Tabs.Items.Remove(item);
        LayoutPanes(); // release the view from its pane before another window hosts it

        if (target is null) CreateDetached(_services, view, this).Show();
        else { target.AttachView(view); target.Activate(); }

        if (Tabs.ItemCount == 0) { _closeConfirmed = true; Close(); }
    }

    private TabItem AttachView(DocumentView view)
    {
        var vm = (MainViewModel)view.DataContext!;

        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 180, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        var close = new Button { Content = "✕", Padding = new(5, 2), Background = Avalonia.Media.Brushes.Transparent, BorderThickness = new(0), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var item = new TabItem { Tag = view, Content = new Border(), Header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { label, close } } };
        close.Click += (_, _) => _ = CloseTabAsync(item);
        var loc = Localization.Localizer.Instance;
        var closeThis = new MenuItem { Header = loc["Tab.Close"], InputGesture = new KeyGesture(Key.W, KeyModifiers.Control) };
        var closeOthers = new MenuItem { Header = loc["Tab.CloseOthers"] };
        var closeAll = new MenuItem { Header = loc["Tab.CloseAll"], InputGesture = new KeyGesture(Key.W, KeyModifiers.Control | KeyModifiers.Shift) };
        closeThis.Click += (_, _) => _ = CloseTabAsync(item);
        closeOthers.Click += async (_, _) =>
        {
            foreach (var other in Tabs.Items.OfType<TabItem>().Where(t => t != item).ToList())
                if (!await CloseTabAsync(other)) return;
        };
        closeAll.Click += (_, _) => _ = CloseAllTabsAsync();
        var splitSide = new MenuItem { Header = loc["Tab.SplitSide"] };
        var splitStacked = new MenuItem { Header = loc["Tab.SplitStacked"] };
        var splitSwitch = new MenuItem { Header = loc["Tab.SplitSwitch"] };
        var splitClose = new MenuItem { Header = loc["Tab.SplitClose"] };
        var moveNew = new MenuItem { Header = loc["Tab.MoveNewWindow"] };
        var moveOther = new MenuItem { Header = loc["Tab.MoveToWindow"] };
        moveNew.Click += (_, _) => MoveTab(item, null);
        splitSide.Click += (_, _) => StartSplit(item, stacked: false);
        splitStacked.Click += (_, _) => StartSplit(item, stacked: true);
        splitSwitch.Click += (_, _) => { _splitStacked = !_splitStacked; LayoutPanes(); };
        splitClose.Click += (_, _) => CloseSplit();
        var menu = new ContextMenu
        {
            Items = { closeThis, closeOthers, closeAll, new Separator(), splitSide, splitStacked, splitSwitch, splitClose,
                      new Separator(), moveNew, moveOther },
        };
        menu.Opening += (_, _) =>
        {
            splitSide.IsEnabled = splitStacked.IsEnabled = Tabs.ItemCount > 1;
            splitSwitch.IsEnabled = splitClose.IsEnabled = _splitLeft is not null;
            moveNew.IsEnabled = Tabs.ItemCount > 1;
            moveOther.Items.Clear();
            foreach (var w in Open.Where(w => !ReferenceEquals(w, this)))
            {
                var target = w;
                var entry = new MenuItem { Header = target.Title };
                entry.Click += (_, _) => MoveTab(item, target);
                moveOther.Items.Add(entry);
            }
            moveOther.IsVisible = moveOther.Items.Count > 0;
        };
        item.ContextMenu = menu;

        void Refresh()
        {
            close.Content = vm.HasUnsavedChanges ? "●" : "✕"; // unsaved marker, like an editor tab
            // An empty tab reads "New tab", not a second copy of the app name in front of the documents.
            label.Text = vm.Title.Contains(" — ") ? vm.Title[(vm.Title.IndexOf(" — ", StringComparison.Ordinal) + 3)..] : Localization.Localizer.Instance["Tab.New"];
            ToolTip.SetTip(item, vm.CurrentPath is { } p ? $"{Path.GetFileName(p)}\n{p}" : label.Text);
            if (ReferenceEquals(Tabs.SelectedItem, item)) UpdateWindowTitle();
        }
        PropertyChangedEventHandler hook = (_, e) => { if (e.PropertyName is nameof(MainViewModel.Title) or nameof(MainViewModel.HasUnsavedChanges)) Refresh(); };
        vm.PropertyChanged += hook;
        _hooks[item] = hook;
        Refresh();

        Tabs.Items.Add(item);
        Tabs.SelectedItem = item;
        return item;
    }

    /// <summary>Closes a tab after offering to save unsaved changes; false when the user cancels. Closing the last tab
    /// closes the application, so that asks first (and offers to keep the window with an empty tab instead).</summary>
    private async Task<bool> CloseTabAsync(TabItem item)
    {
        var keepWindow = false;
        if (Tabs.ItemCount == 1 && Open.Count == 1 && _services.GetRequiredService<ISettingsService>() is { Settings.ConfirmCloseLastTab: true } settings)
        {
            var loc = Localization.Localizer.Instance;
            var hasDocument = ViewOf(item) is { DataContext: MainViewModel { IsDocumentLoaded: true } };
            string[] choices = hasDocument
                ? [loc["Dialog.Cancel"], loc["CloseLast.KeepOpen"], loc["CloseLast.Exit"]]
                : [loc["Dialog.Cancel"], loc["CloseLast.Exit"]];
            var (choice, dontAsk) = await FormDialog.ChooseWithCheckAsync(this, "Hisui PDF", loc["CloseLast.Message"],
                loc["CloseLast.DontAsk"], choices);
            if (choice < 1) return false;
            keepWindow = hasDocument && choice == 1;
            if (dontAsk)
            {
                settings.Settings.ConfirmCloseLastTab = false;
                settings.Save();
            }
        }

        if (!await ConfirmDiscardAsync(item)) return false;
        if (item == _splitLeft || item == _splitRight) CloseSplit(); // one of the two panes goes away
        Tabs.Items.Remove(item);
        if (ViewOf(item) is { } v) _claimed.Remove(v);
        if (Tabs.ItemCount == 0)
        {
            if (keepWindow) AddTab();
            else { _closeConfirmed = true; Close(); }
        }
        return true;
    }

    /// <summary>Save / Don't save / Cancel for a tab with unsaved changes. True when the tab may close.</summary>
    private async Task<bool> ConfirmDiscardAsync(TabItem item)
    {
        if (ViewOf(item) is not { DataContext: MainViewModel vm }) return true;
        await vm.FlushFieldEditAsync();
        if (!vm.HasUnsavedChanges) return true;

        Tabs.SelectedItem = item;
        var loc = Localization.Localizer.Instance;
        var name = vm.CurrentPath is { } p ? Path.GetFileName(p) : loc["Tab.New"];
        return await FormDialog.ChooseAsync(this, "Hisui PDF", loc.Format("Unsaved.Message", name),
                   loc["Dialog.Cancel"], loc["Unsaved.Discard"], loc["Unsaved.Save"]) switch
        {
            2 => await vm.SaveBeforeCloseAsync(),
            1 => true,
            _ => false,
        };
    }

    private bool _closeConfirmed;

    // Window close (title bar, Alt+F4, Exit): ask for every tab with unsaved changes first.
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed) return;
        e.Cancel = true;
        if (!await ConfirmAllAsync()) return;
        _closeConfirmed = true;
        Close();
    }

    private async Task<bool> ConfirmAllAsync()
    {
        foreach (var tab in Tabs.Items.OfType<TabItem>().ToList())
            if (!await ConfirmDiscardAsync(tab)) return false;
        return true;
    }

    /// <summary>Closes every tab and leaves one empty tab (the window stays open).</summary>
    private async Task CloseAllTabsAsync()
    {
        foreach (var tab in Tabs.Items.OfType<TabItem>().ToList())
        {
            if (!await ConfirmDiscardAsync(tab)) return;
            if (tab == _splitLeft || tab == _splitRight) CloseSplit();
            Tabs.Items.Remove(tab);
            if (ViewOf(tab) is { } v) _claimed.Remove(v);
        }
        AddTab();
    }

    /// <summary>Shows <paramref name="other"/> next to the current tab (or replaces the inactive pane when already split).</summary>
    private void StartSplit(TabItem other, bool stacked)
    {
        if (Tabs.SelectedItem is not TabItem current) return;
        if (current == other) // right-click selected the clicked tab: pair it with the one used before, else any other
            other = (_previousTab is { } p && Tabs.Items.Contains(p) ? p : null) ?? Tabs.Items.OfType<TabItem>().FirstOrDefault(t => t != current)!;
        if (other is null || current == other) return;
        _splitStacked = stacked;
        if (_splitLeft is null) { _splitLeft = current; _splitRight = other; }
        else if (current == _splitRight) _splitLeft = other;
        else _splitRight = other;
        LayoutPanes();
    }

    private void CloseSplit()
    {
        _splitLeft = _splitRight = null;
        LayoutPanes();
    }

    /// <summary>(Re)hosts the documents: the selected one alone, or the two split tabs in the chosen arrangement.</summary>
    private void LayoutPanes()
    {
        if (_splitHost is null || _paneA is null || _paneB is null || _splitter is null) return;

        _paneA.Child = null; // detach first: a control can only have one visual parent
        _paneB.Child = null;

        var split = _splitLeft is not null && _splitRight is not null;
        if (split && Tabs.Items.IndexOf(_splitLeft) > Tabs.Items.IndexOf(_splitRight))
            (_splitLeft, _splitRight) = (_splitRight, _splitLeft); // pane order follows tab order
        var first = split ? _splitLeft : Tabs.SelectedItem as TabItem;
        _paneA.Child = ViewOf(first);
        _paneA.Tag = first;
        _paneB.Child = split ? ViewOf(_splitRight) : null;
        _paneB.Tag = split ? _splitRight : null;
        _paneB.IsVisible = _splitter.IsVisible = split;

        _splitHost.ColumnDefinitions.Clear();
        _splitHost.RowDefinitions.Clear();
        // Reset placement first: stale row/column indices from the previous layout would overflow the cleared definitions.
        foreach (var c in new Control[] { _paneA, _splitter, _paneB }) { Grid.SetRow(c, 0); Grid.SetColumn(c, 0); }
        if (!split)
        {
        }
        else if (_splitStacked)
        {
            _splitHost.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star) { MinHeight = 160 });
            _splitHost.RowDefinitions.Add(new RowDefinition(5, GridUnitType.Pixel));
            _splitHost.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star) { MinHeight = 160 });
            Grid.SetRow(_paneA, 0); Grid.SetColumn(_paneA, 0);
            Grid.SetRow(_splitter, 1); Grid.SetColumn(_splitter, 0);
            Grid.SetRow(_paneB, 2); Grid.SetColumn(_paneB, 0);
            _splitter.ResizeDirection = GridResizeDirection.Rows;
        }
        else
        {
            _splitHost.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 320 });
            _splitHost.ColumnDefinitions.Add(new ColumnDefinition(5, GridUnitType.Pixel));
            _splitHost.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 320 });
            Grid.SetRow(_paneA, 0); Grid.SetColumn(_paneA, 0);
            Grid.SetRow(_splitter, 0); Grid.SetColumn(_splitter, 1);
            Grid.SetRow(_paneB, 0); Grid.SetColumn(_paneB, 2);
            _splitter.ResizeDirection = GridResizeDirection.Columns;
        }

        // Accent outline on the active pane so it is clear which document the shortcuts act on.
        var accent = this.TryFindResource("RibbonAccentBrush", ActualThemeVariant, out var b) && b is Avalonia.Media.IBrush br
            ? br : Avalonia.Media.Brushes.DodgerBlue;
        _paneA.BorderBrush = split && Tabs.SelectedItem == _splitLeft ? accent : Avalonia.Media.Brushes.Transparent;
        _paneB.BorderBrush = split && Tabs.SelectedItem == _splitRight ? accent : Avalonia.Media.Brushes.Transparent;
    }

    private void UpdateWindowTitle() =>
        Title = (CurrentView?.DataContext as MainViewModel)?.Title ?? "Hisui PDF";

    /// <summary>Brings the window to the front (second launch / forwarded files).</summary>
    public void Bring()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
}
