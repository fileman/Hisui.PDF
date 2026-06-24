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

    /// <summary>On-screen rotation in degrees (0/90/180/270). Baked into the PDF only on save.</summary>
    [ObservableProperty]
    private double _rotationAngle;
}
