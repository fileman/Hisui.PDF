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

    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        Tabs.SelectionChanged += (_, _) =>
        {
            UpdateWindowTitle();
            (Tabs.SelectedItem as Control)?.BringIntoView();
        };
        Tabs.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<Control>("PART_NewTab") is Button add) add.Click += (_, _) => OpenInTab();
            _captionSpacer = e.NameScope.Find<Control>("PART_CaptionSpacer");
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
    public DocumentView? CurrentView => (Tabs.SelectedItem as TabItem)?.Content as DocumentView;

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
                .FirstOrDefault(t => t.Content is DocumentView v && !_claimed.Contains(v)
                                     && v.DataContext is MainViewModel { IsDocumentLoaded: false, IsBusy: false });
        if (empty is not null) Tabs.SelectedItem = empty;
        var view = empty?.Content as DocumentView ?? AddTab();
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
        var vm = (MainViewModel)view.DataContext!;

        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 180, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        var close = new Button { Content = "✕", Padding = new(5, 2), Background = Avalonia.Media.Brushes.Transparent, BorderThickness = new(0), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var item = new TabItem { Content = view, Header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { label, close } } };
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
        item.ContextMenu = new ContextMenu { Items = { closeThis, closeOthers, closeAll } };

        void Refresh()
        {
            close.Content = vm.HasUnsavedChanges ? "●" : "✕"; // unsaved marker, like an editor tab
            // An empty tab reads "New tab", not a second copy of the app name in front of the documents.
            label.Text = vm.Title.Contains(" — ") ? vm.Title[(vm.Title.IndexOf(" — ", StringComparison.Ordinal) + 3)..] : Localization.Localizer.Instance["Tab.New"];
            ToolTip.SetTip(item, vm.CurrentPath is { } p ? $"{Path.GetFileName(p)}\n{p}" : label.Text);
            if (ReferenceEquals(Tabs.SelectedItem, item)) UpdateWindowTitle();
        }
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(MainViewModel.Title) or nameof(MainViewModel.HasUnsavedChanges)) Refresh(); };
        Refresh();

        Tabs.Items.Add(item);
        Tabs.SelectedItem = item;
        return view;
    }

    /// <summary>Closes a tab after offering to save unsaved changes; false when the user cancels. Closing the last tab
    /// closes the application, so that asks first (and offers to keep the window with an empty tab instead).</summary>
    private async Task<bool> CloseTabAsync(TabItem item)
    {
        var keepWindow = false;
        if (Tabs.ItemCount == 1 && _services.GetRequiredService<ISettingsService>() is { Settings.ConfirmCloseLastTab: true } settings)
        {
            var loc = Localization.Localizer.Instance;
            var hasDocument = item.Content is DocumentView { DataContext: MainViewModel { IsDocumentLoaded: true } };
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
        Tabs.Items.Remove(item);
        if (item.Content is DocumentView v) _claimed.Remove(v);
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
        if (item.Content is not DocumentView { DataContext: MainViewModel vm }) return true;
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
        foreach (var tab in Tabs.Items.OfType<TabItem>().ToList())
            if (!await ConfirmDiscardAsync(tab)) return;
        _closeConfirmed = true;
        Close();
    }

    /// <summary>Closes every tab and leaves one empty tab (the window stays open).</summary>
    private async Task CloseAllTabsAsync()
    {
        foreach (var tab in Tabs.Items.OfType<TabItem>().ToList())
        {
            if (!await ConfirmDiscardAsync(tab)) return;
            Tabs.Items.Remove(tab);
            if (tab.Content is DocumentView v) _claimed.Remove(v);
        }
        AddTab();
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
