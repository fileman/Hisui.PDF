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
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private string _searchQuery = "";

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

        await RunBusyAsync(_loc["Status.Searching"], async ct =>
        {
            var current = await _pageService.BuildFromSessionAsync(_session!, ct);
            var options = new TextSearchOptions { MatchCase = SearchMatchCase, WholeWord = SearchWholeWord };
            _results = await _textExtractor.SearchAsync(current, query, options, ct);
            _lastSearchSignature = signature;
            _currentMatch = _results.Count > 0 ? 0 : -1;
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
        });
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
        if (PreviewRotationAngle != 0) return;       // overlay only maps to PDF space at 0° (like annotations)
        if (_overlayW <= 0 || _overlayH <= 0) return;

        var pageIdx = Pages.IndexOf(SelectedPage);
        if (pageIdx < 0) return;
        var current = (uint)_currentMatch < (uint)_results.Count ? _results[_currentMatch] : null;

        foreach (var result in _results)
        {
            if (result.PageIndex != pageIdx) continue;
            var isCurrent = ReferenceEquals(result, current);
            foreach (var box in result.Boxes)
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
