using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Tests.Fixtures;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfDocumentSessionTests
{
    // ── AddSource / GetSource ─────────────────────────────────────────────────

    [Fact]
    public void AddSource_RegistersSourceAndPages()
    {
        var session = new PdfDocumentSession();
        var bytes = PdfFixtureBuilder.Create(3);

        var id = session.AddSource(bytes, 3);

        Assert.Equal(0, id);
        Assert.Equal(3, session.Pages.Count);
        Assert.Equal(bytes, session.GetSource(id));
    }

    [Fact]
    public void AddSource_PushesUndoSnapshot()
    {
        var session = new PdfDocumentSession();
        Assert.False(session.CanUndo);

        session.AddSource(PdfFixtureBuilder.Create(1), 1);

        Assert.True(session.CanUndo);
    }

    // ── UpdateSource ──────────────────────────────────────────────────────────

    [Fact]
    public void UpdateSource_ReplacesBytes()
    {
        var session = new PdfDocumentSession();
        var original = PdfFixtureBuilder.Create(1);
        var id = session.AddSource(original, 1);

        var updated = PdfFixtureBuilder.Create(1);
        session.UpdateSource(id, updated);

        Assert.Equal(updated, session.GetSource(id));
        Assert.NotEqual(original, session.GetSource(id));
    }

    [Fact]
    public void UpdateSource_DoesNotChangePageList()
    {
        var session = new PdfDocumentSession();
        var id = session.AddSource(PdfFixtureBuilder.Create(2), 2);
        var pageCountBefore = session.Pages.Count;
        var undoCountBefore = session.CanUndo;

        session.UpdateSource(id, PdfFixtureBuilder.Create(2));

        Assert.Equal(pageCountBefore, session.Pages.Count);
    }

    [Fact]
    public void UpdateSource_PushesUndoSnapshot()
    {
        var session = new PdfDocumentSession();
        var original = PdfFixtureBuilder.Create(1);
        var id = session.AddSource(original, 1);

        session.UpdateSource(id, PdfFixtureBuilder.Create(1));

        // Content edits (annotation/text burn-in) are undoable: one undo reverts the edit while the
        // source stays registered — proving UpdateSource recorded a snapshot distinct from AddSource's.
        Assert.True(session.Undo());
        Assert.Same(original, session.GetSource(id));
    }

    [Fact]
    public void UpdateSource_ThrowsForUnknownId()
    {
        var session = new PdfDocumentSession();
        Assert.Throws<KeyNotFoundException>(() => session.UpdateSource(99, [0x25, 0x50, 0x44, 0x46]));
        // Failed validation runs before Snapshot(), so it must not record a phantom undo entry.
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void UpdateSource_ThrowsForNullBytes()
    {
        var session = new PdfDocumentSession();
        var id = session.AddSource(PdfFixtureBuilder.Create(1), 1);
        Assert.Throws<ArgumentNullException>(() => session.UpdateSource(id, null!));
    }

    // ── content undo / redo (annotation & text-edit burn-in) ──────────────────

    [Fact]
    public void Undo_AfterUpdateSource_RestoresOriginalBytes()
    {
        var session = new PdfDocumentSession();
        var original = PdfFixtureBuilder.Create(1);
        var id = session.AddSource(original, 1);
        var annotated = PdfFixtureBuilder.Create(2);
        session.UpdateSource(id, annotated);
        Assert.Same(annotated, session.GetSource(id));

        Assert.True(session.Undo());

        Assert.Same(original, session.GetSource(id));
    }

    [Fact]
    public void Redo_AfterUndoOfUpdateSource_RestoresUpdatedBytes()
    {
        var session = new PdfDocumentSession();
        var original = PdfFixtureBuilder.Create(1);
        var id = session.AddSource(original, 1);
        var annotated = PdfFixtureBuilder.Create(2);
        session.UpdateSource(id, annotated);
        session.Undo();

        Assert.True(session.Redo());

        Assert.Same(annotated, session.GetSource(id));
    }

    [Fact]
    public void Undo_AfterUpdateSource_PreservesPageList()
    {
        var session = new PdfDocumentSession();
        var id = session.AddSource(PdfFixtureBuilder.Create(3), 3);
        var pages = session.Pages.ToList();
        session.UpdateSource(id, PdfFixtureBuilder.Create(3));

        session.Undo();

        // A content edit never touches the page list — undoing it must leave the pages intact.
        Assert.Equal(3, session.Pages.Count);
        Assert.Equal(pages.Select(p => (p.SourceDocumentId, p.SourcePageIndex)),
                     session.Pages.Select(p => (p.SourceDocumentId, p.SourcePageIndex)));
    }

    [Fact]
    public void UpdateSource_ClearsRedoStack()
    {
        var session = new PdfDocumentSession();
        var id = session.AddSource(PdfFixtureBuilder.Create(1), 1);
        session.UpdateSource(id, PdfFixtureBuilder.Create(1));
        session.Undo();
        Assert.True(session.CanRedo);

        session.UpdateSource(id, PdfFixtureBuilder.Create(2));

        // A fresh content edit invalidates the redo branch.
        Assert.False(session.CanRedo);
    }

    [Fact]
    public void UpdateSource_WithoutTransaction_AreSeparateUndoSteps()
    {
        var session = new PdfDocumentSession();
        var v0 = PdfFixtureBuilder.Create(1);
        var id = session.AddSource(v0, 1);
        var v1 = PdfFixtureBuilder.Create(1);
        var v2 = PdfFixtureBuilder.Create(2);
        session.UpdateSource(id, v1);
        session.UpdateSource(id, v2);

        session.Undo();
        Assert.Same(v1, session.GetSource(id));
        session.Undo();
        Assert.Same(v0, session.GetSource(id));
    }

    // ── transactions (coalesced undo) ──────────────────────────────────────────

    [Fact]
    public void BeginTransaction_CoalescesUpdatesIntoSingleUndoStep()
    {
        var session = new PdfDocumentSession();
        var a0 = PdfFixtureBuilder.Create(1);
        var b0 = PdfFixtureBuilder.Create(1);
        var idA = session.AddSource(a0, 1);
        var idB = session.AddSource(b0, 1);

        var a1 = PdfFixtureBuilder.Create(2);
        var b1 = PdfFixtureBuilder.Create(2);
        using (session.BeginTransaction())
        {
            session.UpdateSource(idA, a1);
            session.UpdateSource(idB, b1);
        }
        Assert.Same(a1, session.GetSource(idA));
        Assert.Same(b1, session.GetSource(idB));

        // One undo reverts BOTH edits…
        Assert.True(session.Undo());
        Assert.Same(a0, session.GetSource(idA));
        Assert.Same(b0, session.GetSource(idB));

        // …and one redo re-applies BOTH, proving it was a single coalesced step.
        Assert.True(session.Redo());
        Assert.Same(a1, session.GetSource(idA));
        Assert.Same(b1, session.GetSource(idB));
    }

    [Fact]
    public void BeginTransaction_NestedScopes_CoalesceIntoSingleUndoStep()
    {
        var session = new PdfDocumentSession();
        var v0 = PdfFixtureBuilder.Create(1);
        var id = session.AddSource(v0, 1);
        var v1 = PdfFixtureBuilder.Create(2);
        var v2 = PdfFixtureBuilder.Create(3);
        var v3 = PdfFixtureBuilder.Create(4);

        using (session.BeginTransaction())
        {
            session.UpdateSource(id, v1);
            using (session.BeginTransaction()) { session.UpdateSource(id, v2); }
            // The inner dispose must NOT reset the snapshot flag while the outer scope is open.
            session.UpdateSource(id, v3);
        }

        // All three edits across both nesting levels collapse into one undo step.
        Assert.True(session.Undo());
        Assert.Same(v0, session.GetSource(id));
        Assert.True(session.Redo());
        Assert.Same(v3, session.GetSource(id));
    }

    [Fact]
    public void BeginTransaction_WithNoMutations_PushesNothing()
    {
        var session = new PdfDocumentSession();
        session.AddSource(PdfFixtureBuilder.Create(1), 1);
        session.Undo(); // drain the AddSource snapshot
        Assert.False(session.CanUndo);

        using (session.BeginTransaction()) { /* no mutations */ }

        Assert.False(session.CanUndo);
    }

    // ── multi-source composition ──────────────────────────────────────────────

    [Fact]
    public void MultipleSources_PagesAreConcatenated()
    {
        var session = new PdfDocumentSession();
        session.AddSource(PdfFixtureBuilder.Create(2), 2);
        session.AddSource(PdfFixtureBuilder.Create(3), 3);

        Assert.Equal(5, session.Pages.Count);
        Assert.Equal(2, session.SourceCount);
    }
}
