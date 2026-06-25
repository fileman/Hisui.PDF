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

    partial void OnPreviewImageChanged(IImage? value)
    {
        OnPropertyChanged(nameof(PreviewDisplayWidth));
        OnPropertyChanged(nameof(PreviewDisplayHeight));
    }

    [RelayCommand] private void ZoomIn() => SetZoom(ZoomLevel * ZoomStep);
    [RelayCommand] private void ZoomOut() => SetZoom(ZoomLevel / ZoomStep);
    [RelayCommand] private void ZoomActual() => SetZoom(1.0);

    [RelayCommand]
    private void FitWidth()
    {
        if (NativeW > 0 && _viewportW > 0) SetZoom((_viewportW - ViewportPadding) / NativeW);
    }

    [RelayCommand]
    private void FitHeight()
    {
        if (NativeH > 0 && _viewportH > 0) SetZoom((_viewportH - ViewportPadding) / NativeH);
    }

    // The preview sits inside a 16px margin; leave room so the fitted page doesn't trip a scrollbar.
    private const double ViewportPadding = 36;

    private void SetZoom(double zoom) => ZoomLevel = Math.Clamp(zoom, MinZoom, MaxZoom);

    /// <summary>Pushed from the view: the preview scroll viewport size, used by fit-width / fit-height.</summary>
    public void UpdateViewportSize(double width, double height)
    {
        _viewportW = width;
        _viewportH = height;
    }
}
