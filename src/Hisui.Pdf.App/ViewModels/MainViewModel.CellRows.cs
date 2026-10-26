using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.ViewModels;

public partial class MainViewModel
{
    /// <summary>The row of form boxes under a point of the page, for the free-text tool to fit its cells to; null when there is none.</summary>
    public async Task<CellRow?> FindCellRowAsync(PageItemViewModel page, double nx, double ny)
    {
        if (_session is null) return null;
        try
        {
            return await _formDetector.FindCellRowAsync(_session.GetSource(page.SourceDocumentId), page.SourcePageIndex, nx, ny);
        }
        catch (Exception)
        {
            return null; // detection is a convenience: never let it break the tool
        }
    }
}
