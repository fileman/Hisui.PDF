using System.ComponentModel;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Hisui.Pdf.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Hisui.Pdf.App.Views;

/// <summary>Tabbed shell: every open document lives in its own <see cref="DocumentView"/> tab.</summary>
public partial class MainWindow : Window
{
    private readonly IServiceProvider _services;
    private readonly HashSet<DocumentView> _claimed = [];

    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        Tabs.SelectionChanged += (_, _) => UpdateWindowTitle();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Control) && Tabs.SelectedItem is TabItem t)
            {
                CloseTab(t);
                e.Handled = true;
            }
        };
        AddTab();
    }

    /// <summary>The document view of the selected tab.</summary>
    public DocumentView? CurrentView => (Tabs.SelectedItem as TabItem)?.Content as DocumentView;

    /// <summary>Opens <paramref name="path"/> in the selected tab when it is empty, otherwise in a new tab.
    /// With no path, adds an empty tab.</summary>
    public void OpenInTab(string? path = null)
    {
        // Loading is async, so IsDocumentLoaded alone can't tell that a tab was just claimed by a previous file.
        var view = path is not null && CurrentView is { } cur && !_claimed.Contains(cur)
                   && cur.DataContext is MainViewModel { IsDocumentLoaded: false }
            ? cur
            : AddTab();
        if (path is not null && view.DataContext is MainViewModel vm)
        {
            _claimed.Add(view);
            _ = vm.OpenPathAsync(path);
        }
    }

    private DocumentView AddTab()
    {
        var view = _services.GetRequiredService<DocumentView>();
        var vm = (MainViewModel)view.DataContext!;

        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 180, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        var close = new Button { Content = "✕", Padding = new(4, 0), Background = Avalonia.Media.Brushes.Transparent, BorderThickness = new(0), FontSize = 11 };
        var item = new TabItem { Content = view, Header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { label, close } } };
        close.Click += (_, _) => CloseTab(item);

        void Refresh()
        {
            label.Text = vm.Title.Contains(" — ") ? vm.Title[(vm.Title.IndexOf(" — ", StringComparison.Ordinal) + 3)..] : vm.Title;
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
