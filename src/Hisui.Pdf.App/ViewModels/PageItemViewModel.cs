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

    /// <summary>Form widgets on this page (loaded with its preview) for the fill/layout overlay.</summary>
    [ObservableProperty]
    private IReadOnlyList<Hisui.Pdf.Core.Model.PdfFormWidget> _fields = [];

    /// <summary>Link annotations on this page (clickable in normal viewing).</summary>
    [ObservableProperty]
    private IReadOnlyList<Hisui.Pdf.Core.Model.PdfLink> _links = [];

    /// <summary>Updates <see cref="Fields"/> without notifying: the live editors already show these values.</summary>
#pragma warning disable MVVMTK0034
    internal void SetFieldsSilently(IReadOnlyList<Hisui.Pdf.Core.Model.PdfFormWidget> fields) => _fields = fields;
#pragma warning restore MVVMTK0034

    /// <summary>On-screen rotation in degrees (0/90/180/270). Baked into the PDF only on save.</summary>
    [ObservableProperty]
    private double _rotationAngle;
}
