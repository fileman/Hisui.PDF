using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Hisui.Pdf.App.ViewModels;

/// <summary>One page in the thumbnail strip. Knows where it came from (for thumbnail caching) plus
/// its display number, rendered thumbnail and on-screen rotation.</summary>
public partial class PageItemViewModel : ObservableObject
{
    public PageItemViewModel(int sourceDocumentId, int sourcePageIndex)
    {
        SourceDocumentId = sourceDocumentId;
        SourcePageIndex = sourcePageIndex;
    }

    public int SourceDocumentId { get; }

    public int SourcePageIndex { get; }

    [ObservableProperty]
    private int _pageNumber;

    [ObservableProperty]
    private IImage? _thumbnail;

    /// <summary>Full-size render, loaded lazily while the page is near the viewport in continuous view.</summary>
    [ObservableProperty]
    private IImage? _preview;

    /// <summary>Unrotated on-screen size in continuous view (base size x zoom); rotation is a layout transform.</summary>
    [ObservableProperty]
    private double _displayWidth;

    [ObservableProperty]
    private double _displayHeight;

    private Avalonia.Size _baseSize;
    private double _zoom = 1;

    public void SetBaseSize(Avalonia.Size size) { _baseSize = size; ApplySize(); }

    public void SetZoom(double zoom) { _zoom = zoom; ApplySize(); }

    private void ApplySize()
    {
        DisplayWidth = _baseSize.Width * _zoom;
        DisplayHeight = _baseSize.Height * _zoom;
    }

    /// <summary>On-screen rotation in degrees (0/90/180/270). Baked into the PDF only on save.</summary>
    [ObservableProperty]
    private double _rotationAngle;
}
