namespace Hisui.Pdf.Core.Model;

/// <summary>
/// In-memory, non-destructive editing model for an open PDF — or a composition of several. Source
/// documents are kept as byte buffers keyed by id; the visible document is the ordered
/// <see cref="Pages"/> list. Structural operations (move/remove/rotate/reorder/merge) edit the page
/// list; content operations (annotation/text-edit burn-in) replace a source's bytes via
/// <see cref="UpdateSource"/>. Both kinds are individually undoable: each snapshot captures the page
/// list and the source map together. Turning the session back into bytes is the page-service's job
/// (BuildFromSession), which imports each referenced page in order and applies its rotation.
/// </summary>
public sealed class PdfDocumentSession
{
    private readonly Dictionary<int, byte[]> _sources = new();
    private readonly Dictionary<int, int> _sourcePageCounts = new();
    private readonly List<PdfPageRef> _pages = new();
    private readonly Stack<HistoryState> _undo = new();
    private readonly Stack<HistoryState> _redo = new();
    private int _nextSourceId;
    private int _transactionDepth;
    private bool _transactionSnapshotted;

    /// <summary>The ordered, visible pages of the working document.</summary>
    public IReadOnlyList<PdfPageRef> Pages => _pages;

    public int SourceCount => _sources.Count;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Registers a source document and appends all of its pages. Returns the new source id.</summary>
    public int AddSource(byte[] pdfBytes, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);

        Snapshot();
        var id = _nextSourceId++;
        _sources[id] = pdfBytes;
        _sourcePageCounts[id] = pageCount;
        for (var i = 0; i < pageCount; i++)
            _pages.Add(new PdfPageRef(id, i));
        return id;
    }

    /// <summary>Bytes of a source document previously registered with <see cref="AddSource"/>.</summary>
    public byte[] GetSource(int sourceDocumentId) => _sources[sourceDocumentId];

    /// <summary>
    /// Replaces the bytes of an existing source (e.g. after burn-in annotation or text edit) and
    /// pushes an undo snapshot so the content change can be reverted. Inside a
    /// <see cref="BeginTransaction"/> scope several replacements coalesce into a single undo step.
    /// </summary>
    public void UpdateSource(int sourceDocumentId, byte[] newBytes)
    {
        ArgumentNullException.ThrowIfNull(newBytes);
        if (!_sources.ContainsKey(sourceDocumentId))
            throw new KeyNotFoundException($"Source {sourceDocumentId} not found.");
        Snapshot();
        _sources[sourceDocumentId] = newBytes;
    }

    /// <summary>
    /// Opens a scope in which several mutations collapse into one undo step. The snapshot is taken
    /// lazily on the first mutation inside the scope; dispose the returned token to close it. Used for
    /// document-wide operations (e.g. watermarking every source) so one user action is one undo.
    /// Nested scopes share the outermost snapshot.
    /// </summary>
    public IDisposable BeginTransaction() => new Transaction(this);

    /// <summary>Moves the page at <paramref name="fromIndex"/> to <paramref name="toIndex"/>.</summary>
    public void Move(int fromIndex, int toIndex)
    {
        Snapshot();
        var item = _pages[fromIndex];
        _pages.RemoveAt(fromIndex);
        _pages.Insert(toIndex, item);
    }

    /// <summary>Removes the pages at the given indices (deduplicated).</summary>
    public void RemoveAt(params IReadOnlyList<int> indices)
    {
        Snapshot();
        foreach (var i in indices.Distinct().OrderByDescending(x => x))
            _pages.RemoveAt(i);
    }

    /// <summary>Applies <paramref name="delta"/> rotation (cumulative) to a single page.</summary>
    public void Rotate(int index, PageRotation delta)
    {
        Snapshot();
        _pages[index].Rotation = Combine(_pages[index].Rotation, delta);
    }

    /// <summary>Reorders the whole page list to match <paramref name="newOrder"/> (a permutation of indices).</summary>
    public void Reorder(IReadOnlyList<int> newOrder)
    {
        Snapshot();
        var reordered = newOrder.Select(i => _pages[i]).ToList();
        _pages.Clear();
        _pages.AddRange(reordered);
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(Capture());
        Restore(_undo.Pop());
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Push(Capture());
        Restore(_redo.Pop());
        return true;
    }

    private void Snapshot()
    {
        // Inside a transaction only the first mutation records a snapshot; the rest coalesce into it.
        if (_transactionDepth > 0)
        {
            if (_transactionSnapshotted) return;
            _transactionSnapshotted = true;
        }
        _undo.Push(Capture());
        _redo.Clear();
    }

    // A snapshot captures the page list and the source map together. The page refs are cloned (they
    // are mutable — see Rotate); the source map is a shallow copy, which is enough because source
    // bytes are immutable (UpdateSource swaps the reference, never edits the array in place).
    private HistoryState Capture() => new(Clone(_pages), new Dictionary<int, byte[]>(_sources));

    private void Restore(HistoryState state)
    {
        _pages.Clear();
        _pages.AddRange(state.Pages);
        _sources.Clear();
        foreach (var (id, bytes) in state.Sources)
            _sources[id] = bytes;
    }

    private static List<PdfPageRef> Clone(IEnumerable<PdfPageRef> pages) =>
        pages.Select(p => new PdfPageRef(p.SourceDocumentId, p.SourcePageIndex, p.Rotation)).ToList();

    private static PageRotation Combine(PageRotation current, PageRotation delta) =>
        (PageRotation)(((int)current + (int)delta + 360) % 360);

    private sealed record HistoryState(List<PdfPageRef> Pages, Dictionary<int, byte[]> Sources);

    private sealed class Transaction : IDisposable
    {
        private readonly PdfDocumentSession _session;
        private bool _disposed;

        public Transaction(PdfDocumentSession session)
        {
            _session = session;
            _session._transactionDepth++;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (--_session._transactionDepth == 0)
                _session._transactionSnapshotted = false;
        }
    }
}
