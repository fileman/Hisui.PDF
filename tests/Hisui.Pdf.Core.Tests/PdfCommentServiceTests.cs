using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfCommentServiceTests
{
    private readonly PdfCommentService _sut = new();

    [Fact]
    public async Task AddReplyEditMoveDelete_RoundTrip()
    {
        var pdf = PdfFixtureBuilder.Create(2);
        pdf = await _sut.AddCommentAsync(pdf, 1, PdfRect.FromLTWH(0.2, 0.3, 0, 0), "Controllare", "Ada");

        var c = Assert.Single(await _sut.ReadCommentsAsync(pdf));
        Assert.Equal((1, "Text", "Controllare", "Ada"), (c.PageIndex, c.Subtype, c.Text, c.Author));
        Assert.Equal(0.2, c.Rect.Left, 3);

        pdf = await _sut.ReplyAsync(pdf, 1, c.AnnotIndex, "Fatto", "Bob");
        pdf = await _sut.SetTextAsync(pdf, 1, c.AnnotIndex, "Controllare totale");
        pdf = await _sut.MoveAsync(pdf, 1, c.AnnotIndex, 0.1, 0.05);

        var all = await _sut.ReadCommentsAsync(pdf);
        Assert.Equal(2, all.Count);
        Assert.Equal("Controllare totale", all[0].Text);
        Assert.Equal(0.3, all[0].Rect.Left, 3);
        Assert.Equal(0.35, all[0].Rect.Top, 3);
        Assert.Equal(all[0].AnnotIndex, all[1].InReplyTo);
        Assert.NotEmpty(await new PdfiumRenderer().RenderPageToPngAsync(pdf, 1, 72));

        pdf = await _sut.DeleteAsync(pdf, 1, c.AnnotIndex);
        Assert.Empty(await _sut.ReadCommentsAsync(pdf)); // the reply goes with it
    }
}
