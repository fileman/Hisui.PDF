using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfPageServiceTests
{
    private readonly PdfPageService _sut = new();

    [Fact]
    public async Task GetPageCount_ReturnsActualCount()
    {
        var pdf = PdfFixtureBuilder.Create(5);
        Assert.Equal(5, await _sut.GetPageCountAsync(pdf));
    }

    [Fact]
    public async Task Merge_ConcatenatesAllPages()
    {
        var a = PdfFixtureBuilder.Create(2);
        var b = PdfFixtureBuilder.Create(3);

        var merged = await _sut.MergeAsync([a, b]);

        Assert.Equal(5, await _sut.GetPageCountAsync(merged));
    }

    [Fact]
    public async Task ExtractPages_KeepsOnlyRequestedPages()
    {
        var pdf = PdfFixtureBuilder.Create(5);

        var extracted = await _sut.ExtractPagesAsync(pdf, [3, 1]);

        Assert.Equal(2, await _sut.GetPageCountAsync(extracted));
    }

    [Fact]
    public async Task ExtractPages_OutOfRange_Throws()
    {
        var pdf = PdfFixtureBuilder.Create(2);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _sut.ExtractPagesAsync(pdf, [5]));
    }

    [Fact]
    public async Task SplitEvery_ChunksByCount()
    {
        var pdf = PdfFixtureBuilder.Create(5);

        var chunks = await _sut.SplitEveryAsync(pdf, 2);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(2, await _sut.GetPageCountAsync(chunks[0]));
        Assert.Equal(2, await _sut.GetPageCountAsync(chunks[1]));
        Assert.Equal(1, await _sut.GetPageCountAsync(chunks[2]));
    }

    [Fact]
    public async Task BuildFromSession_AppliesRemovalReorderAndRotation()
    {
        var pdf = PdfFixtureBuilder.Create(4);
        var session = new PdfDocumentSession();
        session.AddSource(pdf, 4);                       // [p0, p1, p2, p3]
        session.RemoveAt(1);                             // [p0, p2, p3]
        session.Rotate(0, PageRotation.Clockwise90);     // [p0(90), p2, p3]
        session.Move(0, 2);                              // [p2, p3, p0(90)]

        var built = await _sut.BuildFromSessionAsync(session);

        Assert.Equal(3, await _sut.GetPageCountAsync(built));
        Assert.Equal([0, 0, 90], ReadRotations(built));
    }

    [Fact]
    public async Task BuildFromSession_MergesMultipleSources()
    {
        var a = PdfFixtureBuilder.Create(2);
        var b = PdfFixtureBuilder.Create(1);
        var session = new PdfDocumentSession();
        session.AddSource(a, 2);
        session.AddSource(b, 1);

        var built = await _sut.BuildFromSessionAsync(session);

        Assert.Equal(3, await _sut.GetPageCountAsync(built));
    }

    [Fact]
    public void Session_UndoRedo_RestoresPageList()
    {
        var session = new PdfDocumentSession();
        session.AddSource(PdfFixtureBuilder.Create(3), 3);
        session.RemoveAt(0);
        Assert.Equal(2, session.Pages.Count);

        Assert.True(session.Undo());
        Assert.Equal(3, session.Pages.Count);

        Assert.True(session.Redo());
        Assert.Equal(2, session.Pages.Count);
    }

    private static int[] ReadRotations(byte[] pdf)
    {
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        return [.. Enumerable.Range(0, doc.PageCount).Select(i => doc.Pages[i].Rotate)];
    }
}
