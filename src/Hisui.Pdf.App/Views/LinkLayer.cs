using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Hisui.Pdf.App.Localization;
using Hisui.Pdf.App.ViewModels;

namespace Hisui.Pdf.App.Views;

/// <summary>Clickable link areas of a page (normal viewing only): click follows, right-click offers removal.</summary>
public sealed class LinkLayer : Canvas
{
    public static readonly StyledProperty<PageItemViewModel?> PageProperty =
        AvaloniaProperty.Register<LinkLayer, PageItemViewModel?>(nameof(Page));

    public PageItemViewModel? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    private static readonly IBrush HoverFill = new SolidColorBrush(Color.FromArgb(0x26, 0x00, 0x5F, 0xB8));
    private MainViewModel? _vm;
    private PageItemViewModel? _subscribed;
    private Size _builtFor;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _vm = this.FindAncestorOfType<DocumentView>()?.DataContext as MainViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        Subscribe(Page);
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        Subscribe(null);
        _vm = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageProperty) { Subscribe(Page); Rebuild(); }
        else if (change.Property == BoundsProperty && Bounds.Size != _builtFor) Rebuild();
    }

    private void Subscribe(PageItemViewModel? page)
    {
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnPageChanged;
        _subscribed = page;
        if (page is not null) page.PropertyChanged += OnPageChanged;
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageItemViewModel.Links)) Rebuild();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsAnnotationActive) or nameof(MainViewModel.IsFormLayoutMode) or nameof(MainViewModel.IsCommentsPanel))
            Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        _builtFor = Bounds.Size;
        if (_vm is not { IsAnnotationActive: false, IsFormLayoutMode: false, IsCommentsPanel: false } vm || Page is not { } page) return;
        var (w, h) = (Bounds.Width, Bounds.Height);
        if (w <= 0 || h <= 0) return;
        var loc = Localizer.Instance;

        foreach (var link in page.Links)
        {
            var area = new Border
            {
                Background = Brushes.Transparent,
                Width = Math.Max(4, link.Rect.Width * w),
                Height = Math.Max(4, link.Rect.Height * h),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(area, link.TargetPage is { } t ? loc.Format("Links.GoToPage", t + 1) : link.Uri ?? "");
            area.PointerEntered += (_, _) => area.Background = HoverFill;
            area.PointerExited += (_, _) => area.Background = Brushes.Transparent;
            area.PointerPressed += async (_, e) =>
            {
                if (!e.GetCurrentPoint(area).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                await vm.FollowLinkAsync(page, link);
            };
            var remove = new MenuItem { Header = loc["Links.Remove"] };
            remove.Click += async (_, _) => await vm.DeleteLinkAsync(page, link);
            area.ContextMenu = new ContextMenu { Items = { remove } };
            SetLeft(area, link.Rect.Left * w);
            SetTop(area, link.Rect.Top * h);
            Children.Add(area);
        }
    }
}
