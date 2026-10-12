using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Hisui.Pdf.App.ViewModels;

namespace Hisui.Pdf.App.Views;

/// <summary>
/// While the Comments panel is open: a box over each visible comment of the page. Click selects it in the list, drag
/// moves it, double-click edits its text, Del deletes it.
/// </summary>
public sealed class CommentLayer : Canvas
{
    public static readonly StyledProperty<PageItemViewModel?> PageProperty =
        AvaloniaProperty.Register<CommentLayer, PageItemViewModel?>(nameof(Page));

    public PageItemViewModel? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    private static readonly IBrush Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xB9, 0x00));
    private static readonly IBrush Stroke = new SolidColorBrush(Color.FromRgb(0xC7, 0x8A, 0x00));
    private MainViewModel? _vm;
    private Size _builtFor;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _vm = this.FindAncestorOfType<DocumentView>()?.DataContext as MainViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageProperty || (change.Property == BoundsProperty && Bounds.Size != _builtFor)) Rebuild();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CommentList) or nameof(MainViewModel.IsCommentsPanel)
            or nameof(MainViewModel.SelectedComment) or nameof(MainViewModel.IsAnnotationActive))
            Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        _builtFor = Bounds.Size;
        if (_vm is not { IsCommentsPanel: true, IsAnnotationActive: false } vm || Page is not { } page) return;
        var (w, h) = (Bounds.Width, Bounds.Height);
        if (w <= 0 || h <= 0) return;

        foreach (var item in vm.CommentList.Where(c => ReferenceEquals(c.Page, page) && !c.IsReply && !c.Comment.IsHidden))
        {
            var r = item.Comment.Rect;
            var selected = vm.SelectedComment is { } s && ReferenceEquals(s.Page, page) && s.Comment.AnnotIndex == item.Comment.AnnotIndex;
            var box = new Border
            {
                Background = Fill,
                BorderBrush = Stroke,
                BorderThickness = new Thickness(selected ? 2.5 : 1),
                Width = Math.Max(8, r.Width * w),
                Height = Math.Max(8, r.Height * h),
                Focusable = true,
                Cursor = new Cursor(StandardCursorType.SizeAll),
            };
            ToolTip.SetTip(box, $"{item.Header}\n{item.Text}");
            SetLeft(box, r.Left * w);
            SetTop(box, r.Top * h);

            Point start = default;
            Point origin = default;
            var dragging = false;
            box.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(box).Properties.IsLeftButtonPressed) return;
                vm.SelectedComment = item;
                box.Focus();
                e.Handled = true;
                if (e.ClickCount == 2) { vm.EditCommentCommand.Execute(null); return; }
                dragging = true;
                start = e.GetPosition(this);
                origin = new Point(GetLeft(box), GetTop(box));
                e.Pointer.Capture(box);
            };
            box.PointerMoved += (_, e) =>
            {
                if (!dragging) return;
                var d = e.GetPosition(this) - start;
                SetLeft(box, Math.Clamp(origin.X + d.X, 0, Math.Max(0, w - box.Width)));
                SetTop(box, Math.Clamp(origin.Y + d.Y, 0, Math.Max(0, h - box.Height)));
            };
            box.PointerReleased += async (_, e) =>
            {
                if (!dragging) return;
                dragging = false;
                e.Pointer.Capture(null);
                var (dx, dy) = ((GetLeft(box) - origin.X) / w, (GetTop(box) - origin.Y) / h);
                if (Math.Abs(dx * w) >= 1 || Math.Abs(dy * h) >= 1) await vm.MoveCommentAsync(item, dx, dy);
            };
            box.KeyDown += (_, e) =>
            {
                if (e.Key is not (Key.Delete or Key.Back)) return;
                vm.SelectedComment = item;
                vm.DeleteCommentCommand.Execute(null);
                e.Handled = true;
            };
            Children.Add(box);
        }
    }
}
