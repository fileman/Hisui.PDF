using System.ComponentModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Reactive;
using Avalonia.VisualTree;
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
        Tabs.SelectionChanged += (_, _) => UpdateWindowTitle();
        Tabs.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<Control>("PART_NewTab") is Button add) add.Click += (_, _) => OpenInTab();
            _captionSpacer = e.NameScope.Find<Control>("PART_CaptionSpacer");
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Control) && Tabs.SelectedItem is TabItem t)
            {
                CloseTab(t);
                e.Handled = true;
            }
        };
        AddTab();
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
        // Loading is async, so IsDocumentLoaded alone can't tell that a tab was just claimed by a previous file.
        var view = path is not null && CurrentView is { } cur && !_claimed.Contains(cur)
                   && cur.DataContext is MainViewModel { IsDocumentLoaded: false }
            ? cur
            : AddTab();
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
        close.Click += (_, _) => CloseTab(item);

        void Refresh()
        {
            label.Text = vm.Title.Contains(" — ") ? vm.Title[(vm.Title.IndexOf(" — ", StringComparison.Ordinal) + 3)..] : vm.Title;
            ToolTip.SetTip(item, vm.CurrentPath is { } p ? $"{Path.GetFileName(p)}\n{p}" : label.Text);
            if (ReferenceEquals(Tabs.SelectedItem, item)) UpdateWindowTitle();
        }
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Title)) Refresh(); };
        Refresh();

        Tabs.Items.Add(item);
        Tabs.SelectedItem = item;
        return view;
    }

    private void CloseTab(TabItem item)
    {
        Tabs.Items.Remove(item);
        if (item.Content is DocumentView v) _claimed.Remove(v);
        if (Tabs.ItemCount == 0) Close();
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
