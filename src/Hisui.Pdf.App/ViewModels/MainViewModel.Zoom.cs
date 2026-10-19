using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Hisui.Pdf.App.ViewModels;

/// <summary>
/// Preview zoom: in / out, actual size (100%), fit-width and fit-height. The displayed image is scaled
/// by <see cref="ZoomLevel"/> via its Width/Height; the annotation and search overlays follow automatically
/// because they bind to the image's bounds, so their coordinate mapping stays correct at any zoom.
/// </summary>
public partial class MainViewModel
{
    private const double MinZoom = 0.1, MaxZoom = 8.0, ZoomStep = 1.2;
    private double _viewportW, _viewportH;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewDisplayWidth))]
    [NotifyPropertyChangedFor(nameof(PreviewDisplayHeight))]
    [NotifyPropertyChangedFor(nameof(ZoomLabel))]
    private double _zoomLevel = 1.0;

    public double PreviewDisplayWidth => NativeW * ZoomLevel;
    public double PreviewDisplayHeight => NativeH * ZoomLevel;
    public string ZoomLabel => $"{ZoomLevel * 100:0}%";

    private double NativeW => PreviewImage?.Size.Width ?? 0;
    private double NativeH => PreviewImage?.Size.Height ?? 0;

    partial void OnZoomLevelChanged(double value)
    {
        foreach (var page in Pages) page.SetZoom(value);
    }

    partial void OnPreviewImageChanged(IImage? value)
    {
        OnPropertyChanged(nameof(PreviewDisplayWidth));
        OnPropertyChanged(nameof(PreviewDisplayHeight));
    }

    [RelayCommand(CanExecute = nameof(HasDocument))] private void ZoomIn() => SetZoom(ZoomLevel * ZoomStep);
    [RelayCommand(CanExecute = nameof(HasDocument))] private void ZoomOut() => SetZoom(ZoomLevel / ZoomStep);
    [RelayCommand(CanExecute = nameof(HasDocument))] private void ZoomActual() => SetZoom(1.0);

    /// <summary>Jumps to a preset zoom percentage (from the status-bar zoom dropdown).</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void SetZoomPercent(string? percent)
    {
        if (double.TryParse(percent, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var p) && p > 0)
            SetZoom(p / 100.0);
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void FitWidth()
    {
        if (NativeW > 0 && _viewportW > 0) SetZoom((_viewportW - ViewportPadding) / NativeW);
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void FitHeight()
    {
        if (NativeH > 0 && _viewportH > 0) SetZoom((_viewportH - ViewportPadding) / NativeH);
    }

    // ── Page navigation (view toolbar) ───────────────────────────────────────

    private int SelectedIndex => SelectedPage is null ? -1 : Pages.IndexOf(SelectedPage);
    private bool CanGoBack() => SelectedIndex > 0;
    private bool CanGoForward() => SelectedIndex >= 0 && SelectedIndex < Pages.Count - 1;

    [RelayCommand(CanExecute = nameof(CanGoBack))] private void FirstPage() => GoToPage(0);
    [RelayCommand(CanExecute = nameof(CanGoBack))] private void PreviousPage() => GoToPage(SelectedIndex - 1);
    [RelayCommand(CanExecute = nameof(CanGoForward))] private void NextPage() => GoToPage(SelectedIndex + 1);
    [RelayCommand(CanExecute = nameof(CanGoForward))] private void LastPage() => GoToPage(Pages.Count - 1);

    private void GoToPage(int index)
    {
        if ((uint)index < (uint)Pages.Count) SelectedPage = Pages[index];
    }

    /// <summary>The 1-based page number in the view toolbar's box; typing a number (then Enter) jumps there.</summary>
    public string PageNumberText
    {
        get => SelectedIndex < 0 ? string.Empty : (SelectedIndex + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);
        set
        {
            if (int.TryParse(value, out var n)) GoToPage(Math.Clamp(n, 1, Math.Max(1, Pages.Count)) - 1);
            OnPropertyChanged(); // snap the box back to the real page when the input was out of range or invalid
        }
    }

    /// <summary>"/ 12" next to the page number box.</summary>
    public string PageTotalText => Pages.Count == 0 ? string.Empty : $"/ {Pages.Count}";

    private void RaisePageNavigationChanged()
    {
        OnPropertyChanged(nameof(PageNumberText));
        OnPropertyChanged(nameof(PageTotalText));
        FirstPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        LastPageCommand.NotifyCanExecuteChanged();
    }

    // The preview sits inside a 16px margin; leave room so the fitted page doesn't trip a scrollbar.
    private const double ViewportPadding = 36;

    private void SetZoom(double zoom) => ZoomLevel = Math.Clamp(zoom, MinZoom, MaxZoom);

    /// <summary>Pushed from the view: the preview scroll viewport size, used by fit-width / fit-height.</summary>
    public void UpdateViewportSize(double width, double height)
    {
        if (width <= 0 || height <= 0) return; // the hidden one of the two preview scrollers reports 0
        _viewportW = width;
        _viewportH = height;
    }
}
