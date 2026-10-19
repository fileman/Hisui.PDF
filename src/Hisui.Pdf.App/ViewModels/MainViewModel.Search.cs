using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.ViewModels;

/// <summary>
/// Find-in-document: runs a literal, phrase-aware search over the assembled document and overlays the
/// matches on the page preview. Highlights are computed in the view's pixel space (the view pushes the
/// preview size via <see cref="UpdateSearchOverlaySize"/>) and only shown at 0° rotation, matching the
/// annotation overlay's coordinate assumption.
/// </summary>
public partial class MainViewModel
{
    private IReadOnlyList<TextSearchResult> _results = [];
    private int _currentMatch = -1;
    private string? _lastSearchSignature;
    private double _overlayW, _overlayH;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand), nameof(ReplaceCommand), nameof(ReplaceAllCommand))]
    private string _searchQuery = "";

    [ObservableProperty] private string _replaceText = "";

    /// <summary>The floating find widget over the page (Ctrl+F; Ctrl+H opens it with the replace row).</summary>
    [ObservableProperty] private bool _isFindVisible;

    /// <summary>Shows the replace row under the find box (Ctrl+H or the widget's chevron).</summary>
    [ObservableProperty] private bool _isReplaceVisible;

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void OpenFind() => IsFindVisible = true;

    /// <summary>Hides the widget and its highlights; the query stays for the next Ctrl+F.</summary>
    [RelayCommand]
    private void CloseFind()
    {
        IsFindVisible = false;
        InvalidateSearch();
    }

    [ObservableProperty] private bool _searchMatchCase;
    [ObservableProperty] private bool _searchWholeWord;
    [ObservableProperty] private string _searchSummary = "";

    /// <summary>Match rectangles for the current page, in preview pixel coordinates.</summary>
    public ObservableCollection<HighlightBox> Highlights { get; } = [];

    private bool HasMatches => _results.Count > 0;

    // Changing an option invalidates the "same query → cycle" shortcut so the next Enter re-runs the search.
    partial void OnSearchMatchCaseChanged(bool value) => _lastSearchSignature = null;
    partial void OnSearchWholeWordChanged(bool value) => _lastSearchSignature = null;

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        if (!HasDocument) return;
        var query = (SearchQuery ?? string.Empty).Trim();
        if (query.Length == 0) { InvalidateSearch(); return; }

        // Pressing Enter again on the same query+options jumps to the next match instead of re-searching.
        var signature = $"{SearchMatchCase}|{SearchWholeWord}|{query}";
        if (signature == _lastSearchSignature && HasMatches) { MoveMatch(+1); return; }

        await RunBusyAsync(_loc["Status.Searching"], ct => RunSearchAsync(query, signature, 0, ct));
    }

    private TextSearchOptions CurrentSearchOptions => new() { MatchCase = SearchMatchCase, WholeWord = SearchWholeWord };

    /// <summary>Searches the document as shown and selects match <paramref name="select"/> (wrapped to the first).</summary>
    private async Task RunSearchAsync(string query, string signature, int select, CancellationToken ct)
    {
        var current = await _pageService.BuildFromSessionAsync(_session!, ct);
        _results = await _textExtractor.SearchAsync(current, query, CurrentSearchOptions, ct);
        _lastSearchSignature = signature;
        _currentMatch = _results.Count == 0 ? -1 : (uint)select < (uint)_results.Count ? select : 0;
        NextMatchCommand.NotifyCanExecuteChanged();
        PrevMatchCommand.NotifyCanExecuteChanged();

        if (_results.Count == 0)
        {
            SearchSummary = _loc["Search.NoResults"];
            Highlights.Clear();
            return;
        }

        SearchSummary = _loc.Format("Search.Count", _currentMatch + 1, _results.Count);
        await NavigateToCurrentMatchAsync();
    }

    private bool CanReplace() => HasDocument && !string.IsNullOrWhiteSpace(SearchQuery);

    /// <summary>
    /// Replaces the current match, then moves on to the next one. If the results are stale (no search yet, or the
    /// query/options changed) it only searches, so the user sees which match the next press will replace.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReplace))]
    private async Task ReplaceAsync()
    {
        if (_session is null) return;
        var query = (SearchQuery ?? string.Empty).Trim();
        if (query.Length == 0) return;
        var signature = $"{SearchMatchCase}|{SearchWholeWord}|{query}";
        if (signature != _lastSearchSignature || (uint)_currentMatch >= (uint)_results.Count)
        {
            await RunBusyAsync(_loc["Status.Searching"], ct => RunSearchAsync(query, signature, 0, ct));
            return;
        }

        var match = _results[_currentMatch];
        if ((uint)match.PageIndex >= (uint)_session.Pages.Count) return;
        // The match's position among its page's results: the source page's own search yields the same order.
        var ordinal = _results.Take(_currentMatch).Count(r => r.PageIndex == match.PageIndex);
        var pageRef = _session.Pages[match.PageIndex];
        var replacement = ReplaceText ?? string.Empty;
        var options = CurrentSearchOptions;
        var replaced = 0;

        await EditAllSourcesAsync(_loc["Status.ReplacingText"], async (id, src, ct) =>
        {
            if (id != pageRef.SourceDocumentId) return src;
            var edits = await _textExtractor.PlanReplacementsAsync(src, pageRef.SourcePageIndex, query, replacement, options, ordinal, ct);
            replaced = edits.Count;
            return edits.Count == 0 ? src : await _textEdit.ApplyReplacementsAsync(src, edits, ct);
        });
        _cachedWordsKey = null;
        if (replaced == 0) { StatusMessage = _loc["Replace.Skipped"]; MoveMatch(+1); return; }

        // Stay on the same index (the replaced match is gone, so it now holds the next one), unless the
        // replacement itself contains the query, which would otherwise be found and replaced forever.
        var next = _currentMatch + CountOccurrences(replacement, query, options);
        await RunBusyAsync(_loc["Status.Searching"], ct => RunSearchAsync(query, signature, next, ct));
        StatusMessage = _loc.Format("Replace.Done", 1);
    }

    /// <summary>Replaces every match in the document as one undoable step.</summary>
    [RelayCommand(CanExecute = nameof(CanReplace))]
    private async Task ReplaceAllAsync()
    {
        if (_session is null) return;
        var query = (SearchQuery ?? string.Empty).Trim();
        if (query.Length == 0) return;
        var replacement = ReplaceText ?? string.Empty;
        var options = CurrentSearchOptions;
        var replaced = 0;

        // Only pages still in the document; a source page shown twice is edited (and counted) once.
        var pagesBySource = _session.Pages
            .GroupBy(p => p.SourceDocumentId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.SourcePageIndex).Distinct().Order().ToList());

        await EditAllSourcesAsync(_loc["Status.ReplacingText"], async (id, src, ct) =>
        {
            var edits = new List<TextReplacement>();
            foreach (var page in pagesBySource.GetValueOrDefault(id) ?? [])
                edits.AddRange(await _textExtractor.PlanReplacementsAsync(src, page, query, replacement, options, null, ct));
            replaced += edits.Count;
            return edits.Count == 0 ? src : await _textEdit.ApplyReplacementsAsync(src, edits, ct);
        });
        _cachedWordsKey = null;
        InvalidateSearch();
        StatusMessage = replaced == 0 ? _loc["Search.NoResults"] : _loc.Format("Replace.Done", replaced);
    }

    private static int CountOccurrences(string text, string query, TextSearchOptions options)
    {
        var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var count = 0;
        for (var i = text.IndexOf(query, comparison); i >= 0; i = text.IndexOf(query, i + query.Length, comparison))
            count++;
        return count;
    }

    private bool CanSearch() => HasDocument && !string.IsNullOrWhiteSpace(SearchQuery);

    [RelayCommand(CanExecute = nameof(HasMatches))]
    private void NextMatch() => MoveMatch(+1);

    [RelayCommand(CanExecute = nameof(HasMatches))]
    private void PrevMatch() => MoveMatch(-1);

    private void MoveMatch(int delta)
    {
        if (_results.Count == 0) return;
        _currentMatch = (_currentMatch + delta + _results.Count) % _results.Count;
        SearchSummary = _loc.Format("Search.Count", _currentMatch + 1, _results.Count);
        _ = NavigateToCurrentMatchAsync();
    }

    private async Task NavigateToCurrentMatchAsync()
    {
        if ((uint)_currentMatch >= (uint)_results.Count) return;

        var result = _results[_currentMatch];
        if ((uint)result.PageIndex < (uint)Pages.Count)
        {
            var target = Pages[result.PageIndex];
            // Changing the page re-renders the preview; the bounds observable then recomputes highlights
            // at the new size. If it is already the selected page, recompute now.
            if (!ReferenceEquals(SelectedPage, target))
                SelectedPage = target;
        }
        RecomputeHighlights();
        await Task.CompletedTask;
    }

    /// <summary>Pushed from the view whenever the preview image's bounds change (new page, resize).</summary>
    public void UpdateSearchOverlaySize(double width, double height)
    {
        if (width == _overlayW && height == _overlayH) return;
        _overlayW = width;
        _overlayH = height;
        RecomputeHighlights();
    }

    internal void RecomputeHighlights()
    {
        Highlights.Clear();
        if (_results.Count == 0 || SelectedPage is null) return;
        if (_overlayW <= 0 || _overlayH <= 0) return;

        var pageIdx = Pages.IndexOf(SelectedPage);
        if (pageIdx < 0) return;
        var current = (uint)_currentMatch < (uint)_results.Count ? _results[_currentMatch] : null;

        foreach (var result in _results)
        {
            if (result.PageIndex != pageIdx) continue;
            var isCurrent = ReferenceEquals(result, current);
            // Matches come from the rotation-baked document; the layer rotates with the preview, so undo that turn.
            foreach (var box in result.Boxes.Select(b => b.RotateClockwise(360 - PageTurns)))
                Highlights.Add(new HighlightBox
                {
                    Left = box.Left * _overlayW,
                    Top = box.Top * _overlayH,
                    Width = box.Width * _overlayW,
                    Height = box.Height * _overlayH,
                    IsCurrent = isCurrent,
                });
        }
    }

    /// <summary>Clears results + highlights. Wired to structural document changes (open/add/delete/move/undo).</summary>
    /// <summary>Shows externally computed marks (comparison, redaction preview) with the search highlight and F3 navigation.</summary>
    private void ShowMarks(IReadOnlyList<TextSearchResult> marks, string summary)
    {
        _results = marks;
        _currentMatch = marks.Count > 0 ? 0 : -1;
        _lastSearchSignature = null;
        NextMatchCommand.NotifyCanExecuteChanged();
        PrevMatchCommand.NotifyCanExecuteChanged();
        SearchSummary = summary;
        if (marks.Count > 0) _ = NavigateToCurrentMatchAsync();
        else Highlights.Clear();
    }

    internal void InvalidateSearch()
    {
        _results = [];
        _currentMatch = -1;
        _lastSearchSignature = null;
        Highlights.Clear();
        SearchSummary = string.Empty;
        NextMatchCommand.NotifyCanExecuteChanged();
        PrevMatchCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>A single search-match rectangle in preview pixel coordinates.</summary>
public sealed class HighlightBox
{
    private static readonly IBrush MatchFill = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xD5, 0x4A));
    private static readonly IBrush CurrentFill = new SolidColorBrush(Color.FromArgb(0x82, 0xFF, 0x9F, 0x1A));
    private static readonly IBrush CurrentStroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0x6A, 0x00));

    public double Left { get; init; }
    public double Top { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public bool IsCurrent { get; init; }

    public IBrush Fill => IsCurrent ? CurrentFill : MatchFill;
    public IBrush Stroke => IsCurrent ? CurrentStroke : Brushes.Transparent;
    public double StrokeThickness => IsCurrent ? 1.5 : 0;
}
