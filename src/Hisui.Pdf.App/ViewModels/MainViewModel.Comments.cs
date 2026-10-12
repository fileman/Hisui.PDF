using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.ViewModels;

/// <summary>One row of the comments panel.</summary>
public sealed record CommentItem(PdfComment Comment, PageItemViewModel Page, bool IsReply)
{
    public string Header => $"{Page.PageNumber} · {Comment.Author ?? "—"}{(Comment.Modified is { } m ? " · " + m.ToString("g") : "")}";
    public string Text => Comment.Text.Length > 0 ? Comment.Text : "(" + Comment.Subtype + ")";
    public Avalonia.Thickness Indent => new(IsReply ? 16 : 0, 0, 0, 0);
}

public partial class MainViewModel
{
    /// <summary>Real PDF comments of the document (Comments panel), threads in page order with replies under their parent.</summary>
    [ObservableProperty] private IReadOnlyList<CommentItem> _commentList = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReplyToCommentCommand), nameof(EditCommentCommand), nameof(DeleteCommentCommand))]
    private CommentItem? _selectedComment;

    public bool IsCommentsPanel => IsToolPanelOpen && RibbonTab == RibbonTab.Comments;

    /// <summary>Set by the view: asks for a text (title, initial text, multi-line); null = cancelled.</summary>
    public Func<string, string, bool, Task<string?>>? PromptTextAsync { get; set; }

    private static string CommentAuthor => Environment.UserName;
    private long _commentsVersion = -1;

    partial void OnSelectedCommentChanged(CommentItem? value)
    {
        if (value is not null && !ReferenceEquals(SelectedPage, value.Page)) SelectedPage = value.Page;
    }

    /// <summary>Re-reads the comments when the panel is showing and the document changed since the last read.</summary>
    private async Task RefreshCommentsAsync(bool force = false)
    {
        if (_session is null) { CommentList = []; return; }
        if (!IsCommentsPanel || (!force && _commentsVersion == _session.Version)) return;
        _commentsVersion = _session.Version;
        try
        {
            var items = new List<CommentItem>();
            foreach (var id in _session.Pages.Select(p => p.SourceDocumentId).Distinct().ToList())
            {
                var comments = await _commentService.ReadCommentsAsync(_session.GetSource(id));
                foreach (var page in Pages.Where(p => p.SourceDocumentId == id))
                {
                    var onPage = comments.Where(c => c.PageIndex == page.SourcePageIndex).ToList();
                    void Add(PdfComment c, bool reply)
                    {
                        items.Add(new CommentItem(c, page, reply));
                        foreach (var r in onPage.Where(x => x.InReplyTo == c.AnnotIndex)) Add(r, true);
                    }
                    foreach (var c in onPage.Where(c => c.InReplyTo is null)) Add(c, false);
                }
            }
            CommentList = items.OrderBy(i => i.Page.PageNumber).ToList(); // stable: threads stay together
            var keep = SelectedComment;
            SelectedComment = keep is null ? null
                : CommentList.FirstOrDefault(c => ReferenceEquals(c.Page, keep.Page) && c.Comment.AnnotIndex == keep.Comment.AnnotIndex);
        }
        catch (Exception ex)
        {
            StatusMessage = _loc.Format("Status.Error", ex.Message);
        }
    }

    public Task AddCommentAsync(PageItemViewModel page, PdfRect at, string text) =>
        EditCommentsAsync(page, src => _commentService.AddCommentAsync(src, page.SourcePageIndex, at, text, CommentAuthor));

    public Task MoveCommentAsync(CommentItem item, double dx, double dy) =>
        EditCommentsAsync(item.Page, src => _commentService.MoveAsync(src, item.Page.SourcePageIndex, item.Comment.AnnotIndex, dx, dy));

    [RelayCommand(CanExecute = nameof(HasSelectedComment))]
    private async Task ReplyToCommentAsync()
    {
        if (SelectedComment is not { } item || PromptTextAsync is null) return;
        var text = await PromptTextAsync(_loc["Comments.Reply"], "", true);
        if (string.IsNullOrWhiteSpace(text)) return;
        var parent = item.Comment.InReplyTo ?? item.Comment.AnnotIndex; // replies thread under the top comment
        await EditCommentsAsync(item.Page, src => _commentService.ReplyAsync(src, item.Page.SourcePageIndex, parent, text, CommentAuthor));
    }

    [RelayCommand(CanExecute = nameof(HasSelectedComment))]
    private async Task EditCommentAsync()
    {
        if (SelectedComment is not { } item || PromptTextAsync is null) return;
        var text = await PromptTextAsync(_loc["Comments.Edit"], item.Comment.Text, true);
        if (text is null || text == item.Comment.Text) return;
        await EditCommentsAsync(item.Page, src => _commentService.SetTextAsync(src, item.Page.SourcePageIndex, item.Comment.AnnotIndex, text));
    }

    [RelayCommand(CanExecute = nameof(HasSelectedComment))]
    private async Task DeleteCommentAsync()
    {
        if (SelectedComment is not { } item) return;
        SelectedComment = null;
        await EditCommentsAsync(item.Page, src => _commentService.DeleteAsync(src, item.Page.SourcePageIndex, item.Comment.AnnotIndex));
    }

    private bool HasSelectedComment => SelectedComment is not null;

    private async Task EditCommentsAsync(PageItemViewModel page, Func<byte[], Task<byte[]>> edit)
    {
        if (_session is null) return;
        await _fieldGate.WaitAsync(); // same ordering as field edits: both rewrite the source in place
        try
        {
            var id = page.SourceDocumentId;
            _session.UpdateSource(id, await edit(_session.GetSource(id)));
            InvalidatePageCache(id, page.SourcePageIndex);
            await RenderThumbnailsAsync();
            if (ReferenceEquals(page, SelectedPage)) await UpdatePreviewAsync(page);
            else EnsurePagePreview(page);
            RaiseCanExecute();
            await RefreshCommentsAsync();
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
