using Avalonia;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.ViewModels;

public partial class MainViewModel
{
    private IReadOnlyList<TextWord>? _cachedWords;
    // Keyed by stable page identity (source + page), NOT display index — the strip position is
    // volatile (move/delete/undo reorder it) and would alias the cache onto the wrong page.
    private (int Source, int Page)? _cachedWordsKey;

    /// <summary>
    /// Handles a click when the TextEdit tool is active: finds the word under the pointer,
    /// invokes <paramref name="editText"/> to ask the user for a replacement, then applies it.
    /// </summary>
    public async Task OnTextEditClickAsync(
        Point pos, Size canvasSize,
        Func<string, Task<string?>> editText)
    {
        if (_session is null || SelectedPage is null) return;
        if (canvasSize.Width <= 0 || canvasSize.Height <= 0) return;

        var nx = pos.X / canvasSize.Width;
        var ny = pos.Y / canvasSize.Height;
        if (nx is < 0 or > 1 || ny is < 0 or > 1) return; // click outside the page

        var pageIdx = Pages.IndexOf(SelectedPage);
        if (pageIdx < 0) return;

        var pageRef = _session.Pages[pageIdx];
        var key = (pageRef.SourceDocumentId, pageRef.SourcePageIndex);

        // Cache words by page identity — re-extract only when the physical page differs.
        if (_cachedWordsKey != key)
        {
            _cachedWords = await _textExtractor.ExtractWordsAsync(
                _session.GetSource(pageRef.SourceDocumentId), pageRef.SourcePageIndex);
            _cachedWordsKey = key;
        }

        var word = _cachedWords?.FirstOrDefault(w =>
            nx >= w.BoundingBox.Left && nx <= w.BoundingBox.Right &&
            ny >= w.BoundingBox.Top  && ny <= w.BoundingBox.Bottom);

        if (word is null) return;

        var newText = await editText(word.Text);
        if (newText is null || newText == word.Text) return;

        var srcId = pageRef.SourceDocumentId;
        var srcIdx = pageRef.SourcePageIndex;

        await RunBusyAsync(_loc["Status.ReplacingText"], async ct =>
        {
            var source = _session.GetSource(srcId);
            var updated = await _textEdit.ReplaceWordAsync(
                source, srcIdx, word.BoundingBox, newText, word.FontSizePoints, ct);

            _session.UpdateSource(srcId, updated);
            _cachedWordsKey = null;   // text changed — invalidate word cache
            InvalidatePageCache(srcId, srcIdx);
            await RenderThumbnailsAsync(ct); // re-render the now-invalidated strip thumbnail
            await UpdatePreviewAsync(SelectedPage);
            RaiseCanExecute(); // the text edit just became undoable
        });
    }
}
