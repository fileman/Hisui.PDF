using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.ViewModels;

/// <summary>A bookmark being edited: belongs to one source document, targets one of its pages (or none).</summary>
public sealed class BookmarkNode
{
    public required string Title { get; set; }
    public required int SourceId { get; init; }
    public int SourcePage { get; init; } = -1;
    public List<BookmarkNode> Children { get; } = [];
}

/// <summary>One row of the bookmarks panel (the tree flattened with its depth).</summary>
public sealed record BookmarkRow(BookmarkNode Node, int Level, PageItemViewModel? Page)
{
    public string Title => Node.Title;
    public Avalonia.Thickness Indent => new(Level * 14, 0, 0, 0);
    public string PageLabel => Page is null ? "" : Page.PageNumber.ToString();
}

/// <summary>Header/footer text with tokens ({n} page, {N} pages, {data} date, {file} name, {bates} Bates number).</summary>
public sealed record HeaderFooterOptions
{
    public string TopLeft { get; init; } = "";
    public string TopCenter { get; init; } = "";
    public string TopRight { get; init; } = "";
    public string BottomLeft { get; init; } = "";
    public string BottomCenter { get; init; } = "";
    public string BottomRight { get; init; } = "";
    public double FontSize { get; init; } = 9;
    public double Margin { get; init; } = 24;
    public string BatesPrefix { get; init; } = "";
    public int BatesStart { get; init; } = 1;
    public int BatesDigits { get; init; } = 6;
}

public partial class MainViewModel
{
    // ── Bookmarks ─────────────────────────────────────────────────────────────

    private readonly Dictionary<int, List<BookmarkNode>> _outlines = [];
    private long _outlineVersion = -1;

    [ObservableProperty] private IReadOnlyList<BookmarkRow> _bookmarkRows = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddChildBookmarkCommand), nameof(RenameBookmarkCommand), nameof(DeleteBookmarkCommand), nameof(MoveBookmarkCommand))]
    private BookmarkRow? _selectedBookmark;

    public bool IsBookmarksPanel => IsToolPanelOpen && RibbonTab == RibbonTab.Bookmarks;

    /// <summary>Set by the view: asks for a link target — a page number or a web address (null = cancelled).</summary>
    public Func<Task<(int? Page, string? Uri)?>>? PromptLinkAsync { get; set; }

    /// <summary>Set by the view: header/footer and Bates options (null = cancelled).</summary>
    public Func<Task<HeaderFooterOptions?>>? PromptHeaderFooterAsync { get; set; }

    partial void OnSelectedBookmarkChanged(BookmarkRow? value)
    {
        if (value?.Page is { } page && !ReferenceEquals(SelectedPage, page)) SelectedPage = page;
    }

    private async Task RefreshBookmarksAsync(bool force = false)
    {
        if (_session is null) { BookmarkRows = []; return; }
        if (!IsBookmarksPanel || _savingOutline || (!force && _outlineVersion == _session.Version)) return;
        _outlineVersion = _session.Version;
        try
        {
            _outlines.Clear();
            foreach (var id in _session.Pages.Select(p => p.SourceDocumentId).Distinct().ToList())
            {
                var outline = await _navigation.ReadOutlineAsync(_session.GetSource(id));
                _outlines[id] = outline.Select(b => ToNode(b, id)).ToList();
            }
            RebuildBookmarkRows();
        }
        catch (Exception ex)
        {
            StatusMessage = _loc.Format("Status.Error", ex.Message);
        }

        static BookmarkNode ToNode(PdfBookmark b, int id)
        {
            var node = new BookmarkNode { Title = b.Title, SourceId = id, SourcePage = b.PageIndex };
            node.Children.AddRange(b.Children.Select(c => ToNode(c, id)));
            return node;
        }
    }

    private void RebuildBookmarkRows(BookmarkNode? select = null)
    {
        var rows = new List<BookmarkRow>();
        void Walk(IEnumerable<BookmarkNode> nodes, int level)
        {
            foreach (var n in nodes)
            {
                var page = Pages.FirstOrDefault(p => p.SourceDocumentId == n.SourceId && p.SourcePageIndex == n.SourcePage);
                rows.Add(new BookmarkRow(n, level, page));
                Walk(n.Children, level + 1);
            }
        }
        // Sources in the order their first page appears.
        foreach (var id in Pages.Select(p => p.SourceDocumentId).Distinct())
            if (_outlines.TryGetValue(id, out var list)) Walk(list, 0);
        var keep = select ?? SelectedBookmark?.Node;
        BookmarkRows = rows;
        SelectedBookmark = keep is null ? null : rows.FirstOrDefault(r => ReferenceEquals(r.Node, keep));
    }

    /// <summary>The sibling list holding <paramref name="node"/>.</summary>
    private List<BookmarkNode>? SiblingsOf(BookmarkNode node)
    {
        List<BookmarkNode>? Find(List<BookmarkNode> list) =>
            list.Contains(node) ? list : list.Select(n => Find(n.Children)).FirstOrDefault(l => l is not null);
        return _outlines.TryGetValue(node.SourceId, out var roots) ? Find(roots) : null;
    }

    private bool HasSelectedBookmark => SelectedBookmark is not null;

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task AddBookmarkAsync() => await AddBookmarkCoreAsync(asChild: false);

    [RelayCommand(CanExecute = nameof(HasSelectedBookmark))]
    private async Task AddChildBookmarkAsync() => await AddBookmarkCoreAsync(asChild: true);

    /// <summary>Bookmarks the selected page, after the selected bookmark (same document) or as its child.</summary>
    private async Task AddBookmarkCoreAsync(bool asChild)
    {
        if (SelectedPage is not { } page || PromptTextAsync is null) return;
        var title = await PromptTextAsync(_loc["Bookmarks.Title"], _loc.Format("Bookmarks.DefaultTitle", page.PageNumber), false);
        if (string.IsNullOrWhiteSpace(title)) return;
        var node = new BookmarkNode { Title = title.Trim(), SourceId = page.SourceDocumentId, SourcePage = page.SourcePageIndex };
        if (!_outlines.TryGetValue(page.SourceDocumentId, out var roots)) _outlines[page.SourceDocumentId] = roots = [];

        if (SelectedBookmark?.Node is { } anchor && anchor.SourceId == node.SourceId)
        {
            if (asChild) anchor.Children.Add(node);
            else if (SiblingsOf(anchor) is { } siblings) siblings.Insert(siblings.IndexOf(anchor) + 1, node);
        }
        else roots.Add(node);
        await SaveOutlineAsync(node.SourceId, node);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedBookmark))]
    private async Task RenameBookmarkAsync()
    {
        if (SelectedBookmark?.Node is not { } node || PromptTextAsync is null) return;
        var title = await PromptTextAsync(_loc["Bookmarks.Rename"], node.Title, false);
        if (string.IsNullOrWhiteSpace(title) || title == node.Title) return;
        node.Title = title.Trim();
        await SaveOutlineAsync(node.SourceId, node);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedBookmark))]
    private async Task DeleteBookmarkAsync()
    {
        if (SelectedBookmark?.Node is not { } node || SiblingsOf(node) is not { } siblings) return;
        siblings.Remove(node);
        SelectedBookmark = null;
        await SaveOutlineAsync(node.SourceId, null);
    }

    private bool CanMoveBookmark(string? direction) => SelectedBookmark is not null;

    /// <summary>up / down among siblings; in (child of the previous sibling) / out (after its parent).</summary>
    [RelayCommand(CanExecute = nameof(CanMoveBookmark))]
    private async Task MoveBookmarkAsync(string direction)
    {
        if (SelectedBookmark?.Node is not { } node || SiblingsOf(node) is not { } siblings) return;
        var at = siblings.IndexOf(node);
        switch (direction)
        {
            case "up" when at > 0:
                (siblings[at - 1], siblings[at]) = (siblings[at], siblings[at - 1]);
                break;
            case "down" when at < siblings.Count - 1:
                (siblings[at + 1], siblings[at]) = (siblings[at], siblings[at + 1]);
                break;
            case "in" when at > 0:
                siblings.RemoveAt(at);
                siblings[at - 1].Children.Add(node);
                break;
            case "out":
                var parent = _outlines[node.SourceId].SelectMany(Flatten).FirstOrDefault(p => p.Children.Contains(node));
                if (parent is null || SiblingsOf(parent) is not { } upper) return;
                siblings.Remove(node);
                upper.Insert(upper.IndexOf(parent) + 1, node);
                break;
            default:
                return;
        }
        await SaveOutlineAsync(node.SourceId, node);

        static IEnumerable<BookmarkNode> Flatten(BookmarkNode n) => n.Children.SelectMany(Flatten).Prepend(n);
    }

    private bool _savingOutline;

    private async Task SaveOutlineAsync(int sourceId, BookmarkNode? select)
    {
        var outline = _outlines[sourceId].Select(ToBookmark).ToList();
        _savingOutline = true; // the tree in memory is already current: no re-read (it would drop the selection)
        try { await EditSourceAsync(sourceId, null, src => _navigation.WriteOutlineAsync(src, outline)); }
        finally { _savingOutline = false; }
        _outlineVersion = _session?.Version ?? -1;
        RebuildBookmarkRows(select);

        static PdfBookmark ToBookmark(BookmarkNode n) => new(n.Title, n.SourcePage, n.Children.Select(ToBookmark).ToList());
    }

    // ── Links ─────────────────────────────────────────────────────────────────

    private readonly Dictionary<int, (byte[] Bytes, IReadOnlyList<PdfLink> Links)> _linkCache = [];

    private async Task LoadLinksAsync(PageItemViewModel item)
    {
        if (_session is null) return;
        var bytes = _session.GetSource(item.SourceDocumentId);
        if (!_linkCache.TryGetValue(item.SourceDocumentId, out var cached) || !ReferenceEquals(cached.Bytes, bytes))
        {
            try { cached = (bytes, await _navigation.ReadLinksAsync(bytes)); }
            catch (Exception) { cached = (bytes, []); }
            _linkCache[item.SourceDocumentId] = cached;
        }
        var links = cached.Links.Where(l => l.PageIndex == item.SourcePageIndex).ToList();
        if (!links.SequenceEqual(item.Links)) item.Links = links;
    }

    /// <summary>Goes to a link's page, or opens its web address after confirmation (http, https, mailto only).</summary>
    public async Task FollowLinkAsync(PageItemViewModel page, PdfLink link)
    {
        if (link.TargetPage is { } target)
        {
            if (Pages.FirstOrDefault(p => p.SourceDocumentId == page.SourceDocumentId && p.SourcePageIndex == target) is { } to)
                SelectedPage = to;
            return;
        }
        if (link.Uri is not { } uri || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https" or "mailto")) return;
        if (ChooseAsync is null || await ChooseAsync(_loc.Format("Links.OpenConfirm", uri), [_loc["Dialog.Cancel"], _loc["Links.Open"]]) != 1) return;
        Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
    }

    public Task DeleteLinkAsync(PageItemViewModel page, PdfLink link) =>
        EditSourceAsync(page.SourceDocumentId, page, src => _navigation.DeleteLinkAsync(src, page.SourcePageIndex, link.AnnotIndex));

    /// <summary>Link tool: asks for the target and adds the link over <paramref name="rect"/> of the selected page.</summary>
    private async Task AddLinkAsync(PdfRect rect)
    {
        if (SelectedPage is not { } page || PromptLinkAsync is null || await PromptLinkAsync() is not { } target) return;
        int? targetPage = null;
        if (target.Page is { } number)
        {
            // The target must live in the same source document (links can't cross into a merged file).
            if (number < 1 || number > Pages.Count || Pages[number - 1].SourceDocumentId != page.SourceDocumentId)
            {
                StatusMessage = _loc["Links.SameDocumentOnly"];
                return;
            }
            targetPage = Pages[number - 1].SourcePageIndex;
        }
        await EditSourceAsync(page.SourceDocumentId, page, src => _navigation.AddLinkAsync(src, page.SourcePageIndex, rect, targetPage, target.Uri));
    }

    // ── Header / footer / Bates ───────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task AddHeaderFooterAsync()
    {
        if (_session is null || PromptHeaderFooterAsync is null || await PromptHeaderFooterAsync() is not { } o) return;
        var total = Pages.Count;
        var date = DateTime.Today.ToString("d");
        var file = _currentPath is null ? "" : Path.GetFileNameWithoutExtension(_currentPath);
        // Resolve the tokens per page of the document as shown, then stamp each source page once.
        var stamps = new Dictionary<int, List<PageStamp>>();
        for (var i = 0; i < Pages.Count; i++)
        {
            var bates = o.BatesPrefix + (o.BatesStart + i).ToString("D" + Math.Clamp(o.BatesDigits, 1, 12));
            string R(string t) => t.Replace("{n}", (i + 1).ToString()).Replace("{N}", total.ToString())
                .Replace("{data}", date).Replace("{file}", file).Replace("{bates}", bates);
            var p = Pages[i];
            if (!stamps.TryGetValue(p.SourceDocumentId, out var list)) stamps[p.SourceDocumentId] = list = [];
            if (list.Any(s => s.PageIndex == p.SourcePageIndex)) continue; // a page shown twice is stamped once
            list.Add(new PageStamp
            {
                PageIndex = p.SourcePageIndex,
                TopLeft = R(o.TopLeft), TopCenter = R(o.TopCenter), TopRight = R(o.TopRight),
                BottomLeft = R(o.BottomLeft), BottomCenter = R(o.BottomCenter), BottomRight = R(o.BottomRight),
                FontSize = o.FontSize, Margin = o.Margin,
            });
        }
        await EditAllSourcesAsync(_loc["Stamp.Applying"], (id, src, ct) =>
            stamps.TryGetValue(id, out var list) ? _navigation.StampAsync(src, list, ct) : Task.FromResult(src));
    }

    // ── Shared ────────────────────────────────────────────────────────────────

    /// <summary>Rewrites one source (one undo step); repaints <paramref name="page"/> when given.</summary>
    private async Task EditSourceAsync(int sourceId, PageItemViewModel? page, Func<byte[], Task<byte[]>> edit)
    {
        if (_session is null) return;
        await _fieldGate.WaitAsync();
        try
        {
            _session.UpdateSource(sourceId, await edit(_session.GetSource(sourceId)));
            if (page is not null)
            {
                InvalidatePageCache(sourceId, page.SourcePageIndex);
                await RenderThumbnailsAsync();
                if (ReferenceEquals(page, SelectedPage)) await UpdatePreviewAsync(page);
                else EnsurePagePreview(page);
            }
            RaiseCanExecute();
        }
        catch (Exception ex)
        {
            StatusMessage = _loc.Format("Status.Error", ex.Message);
        }
        finally
        {
            _fieldGate.Release();
        }
    }
}
