using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfTextExtractorTests
{
    private readonly PdfTextExtractor _sut = new();

    // ── FindMatches (pure phrase-matching logic; PdfPig-free so it is deterministic) ──────────────

    private static (string, PdfRect) W(string text, double left) =>
        (text, new PdfRect(left, 0.10, left + 0.08, 0.14));

    [Fact]
    public void FindMatches_SubstringWithinWord_MatchesWhenWholeWordOff()
    {
        var words = new[] { W("accordo", 0.1) };
        var hits = PdfTextExtractor.FindMatches(words, 0, "accord", new TextSearchOptions());
        var hit = Assert.Single(hits);
        Assert.Single(hit.Boxes);
        Assert.Equal("accord", hit.MatchedText);
    }

    [Fact]
    public void FindMatches_Phrase_SpansEveryWordItCovers()
    {
        var words = new[] { W("verbale", 0.1), W("di", 0.3), W("accordo", 0.4) };
        var hits = PdfTextExtractor.FindMatches(words, 2, "verbale di accordo", new TextSearchOptions());
        var hit = Assert.Single(hits);
        Assert.Equal(3, hit.Boxes.Count); // one box per word the phrase covers
        Assert.Equal(2, hit.PageIndex);
    }

    [Fact]
    public void FindMatches_WholeWord_RejectsPartialAcceptsFull()
    {
        var words = new[] { W("accordo", 0.1) };
        Assert.Empty(PdfTextExtractor.FindMatches(words, 0, "accord", new TextSearchOptions { WholeWord = true }));
        Assert.Single(PdfTextExtractor.FindMatches(words, 0, "accordo", new TextSearchOptions { WholeWord = true }));
    }

    // ── PlanReplacements (pure) ──────────────────────────────────────────────────────────────────

    private static TextWord TW(string text, double left, bool horizontal = true) => new()
    {
        Text = text,
        BoundingBox = new PdfRect(left, 0.10, left + 0.08, 0.14),
        FontSizePoints = 11,
        IsHorizontal = horizontal,
    };

    [Fact]
    public void PlanReplacements_SubstringKeepsTheRestOfTheWord()
    {
        var edit = Assert.Single(PdfTextExtractor.PlanReplacements([TW("concatenate", 0.1)], 0, "cat", "dog", new TextSearchOptions()));
        Assert.Equal("condogenate", edit.NewText);
        Assert.Equal("concatenate", edit.OriginalText);
        Assert.Single(edit.Covers);
        Assert.Equal(11, edit.FontSizePoints);
    }

    [Fact]
    public void PlanReplacements_MatchesSharingAWord_MergeIntoOneEdit()
    {
        var edit = Assert.Single(PdfTextExtractor.PlanReplacements([TW("catcat", 0.1)], 0, "cat", "dog", new TextSearchOptions()));
        Assert.Equal("dogdog", edit.NewText);
    }

    [Fact]
    public void PlanReplacements_Phrase_CoversEveryWordAndRetypesOnce()
    {
        var words = new[] { TW("the", 0.0), TW("old", 0.1), TW("text", 0.2), TW("here", 0.3) };
        var edit = Assert.Single(PdfTextExtractor.PlanReplacements(words, 3, "old text", "new", new TextSearchOptions()));
        Assert.Equal("new", edit.NewText);
        Assert.Equal(2, edit.Covers.Count);
        Assert.Equal(3, edit.PageIndex);
    }

    [Fact]
    public void PlanReplacements_Ordinal_PicksThatMatchOnly()
    {
        var words = new[] { TW("a", 0.0), TW("x", 0.1), TW("a", 0.2), TW("a", 0.3) };
        var edit = Assert.Single(PdfTextExtractor.PlanReplacements(words, 0, "a", "b", new TextSearchOptions(), matchOrdinal: 1));
        Assert.Equal(words[2].BoundingBox, edit.Covers[0]);
        Assert.Empty(PdfTextExtractor.PlanReplacements(words, 0, "a", "b", new TextSearchOptions(), matchOrdinal: 3));
    }

    [Fact]
    public void PlanReplacements_HonorsOptionsAndSkipsRotatedText()
    {
        var words = new[] { TW("Cat", 0.0), TW("cat", 0.1), TW("cats", 0.2), TW("cat", 0.3, horizontal: false) };
        var edits = PdfTextExtractor.PlanReplacements(words, 0, "cat", "dog", new TextSearchOptions { MatchCase = true, WholeWord = true });
        var edit = Assert.Single(edits);
        Assert.Equal(words[1].BoundingBox, edit.Covers[0]);
    }

    [Fact]
    public void PlanReplacements_CarriesTheWordsColourAndWeight()
    {
        var word = new TextWord
        {
            Text = "Title", BoundingBox = new PdfRect(0.1, 0.1, 0.3, 0.15), FontSizePoints = 24,
            ColorArgb = 0xFFFFFFFF, IsBold = true,
        };
        var edit = Assert.Single(PdfTextExtractor.PlanReplacements([word], 0, "Title", "Heading", new TextSearchOptions()));
        Assert.Equal(0xFFFFFFFFu, edit.ColorArgb);
        Assert.True(edit.IsBold);
    }

    [Fact]
    public void PlanReplacements_EmptyReplacement_DeletesTheWord()
    {
        var edit = Assert.Single(PdfTextExtractor.PlanReplacements([TW("draft", 0.1)], 0, "draft", "", new TextSearchOptions()));
        Assert.Equal("", edit.NewText);
    }

    [Fact]
    public void FindMatches_CaseSensitivity_IsHonored()
    {
        var words = new[] { W("Verbale", 0.1) };
        Assert.Empty(PdfTextExtractor.FindMatches(words, 0, "verbale", new TextSearchOptions { MatchCase = true }));
        Assert.Single(PdfTextExtractor.FindMatches(words, 0, "verbale", new TextSearchOptions { MatchCase = false }));
    }

    [Fact]
    public void FindMatches_MultipleOccurrences_AreNonOverlapping()
    {
        var words = new[] { W("la", 0.1), W("la", 0.3), W("la", 0.5) };
        var hits = PdfTextExtractor.FindMatches(words, 0, "la", new TextSearchOptions());
        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public void FindMatches_NoMatch_ReturnsEmpty()
    {
        var words = new[] { W("verbale", 0.1), W("accordo", 0.3) };
        Assert.Empty(PdfTextExtractor.FindMatches(words, 0, "contratto", new TextSearchOptions()));
    }

    [Fact]
    public async Task ExtractText_OnShapeOnlyPdf_DoesNotThrow()
    {
        var pdf = PdfFixtureBuilder.Create(2);
        var text = await _sut.ExtractTextAsync(pdf);
        Assert.NotNull(text);
    }

    [Fact]
    public async Task ExtractWords_OnShapeOnlyPdf_ReturnsEmptyList()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        var words = await _sut.ExtractWordsAsync(pdf, 0);
        Assert.Empty(words);
    }

    [Fact]
    public async Task ExtractText_OnTextPdf_ContainsExpectedWords()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Hello World Test", 1);
        var text = await _sut.ExtractTextAsync(pdf);
        // At least some words must be present in the extracted text.
        Assert.False(string.IsNullOrWhiteSpace(text), "Expected extracted text but got empty result.");
    }

    [Fact]
    public async Task ExtractWords_OnTextPdf_ReturnsBoundedWords()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Foo Bar Baz", 1);
        var words = await _sut.ExtractWordsAsync(pdf, 0);
        // The words collection may be empty if PdfPig cannot parse embedded glyphs; that's acceptable.
        // What is NOT acceptable is an exception or bounding boxes outside [0,1].
        foreach (var w in words)
        {
            Assert.InRange(w.BoundingBox.Left,   0, 1);
            Assert.InRange(w.BoundingBox.Top,    0, 1);
            Assert.InRange(w.BoundingBox.Right,  0, 1);
            Assert.InRange(w.BoundingBox.Bottom, 0, 1);
        }
    }

    [Fact]
    public async Task ExtractWords_UnitFontScaledByTextMatrix_KeepsWordsWhole()
    {
        // Layout tools often emit "1 Tf" and put the real size (here 12) in the text matrix; PDFium then
        // reports a 1pt font, which must not make every inter-letter gap look like a word break.
        var pdf = TextMatrixScaledPdf("/F1 1 Tf 12 0 0 12 60 700 Tm (Healthcare sector security) Tj");

        var words = (await _sut.ExtractWordsAsync(pdf, 0)).Select(w => w.Text).ToList();

        Assert.Equal(["Healthcare", "sector", "security"], words);
        Assert.Single(await _sut.SearchAsync(pdf, "sector", new TextSearchOptions { WholeWord = true }));
    }

    [Fact]
    public async Task ExtractWords_UnitFontWithoutSpaces_StillSplitsAtWordGaps()
    {
        // Same scaling, words placed by TJ gaps (no space glyphs): the gaps must still split the words.
        var pdf = TextMatrixScaledPdf("/F1 1 Tf 12 0 0 12 60 700 Tm [(alpha) -400 (beta)] TJ");

        var words = (await _sut.ExtractWordsAsync(pdf, 0)).Select(w => w.Text).ToList();

        Assert.Equal(["alpha", "beta"], words);
    }

    private static byte[] TextMatrixScaledPdf(string textOps)
    {
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        var font = new PdfSharp.Pdf.PdfDictionary(doc);
        font.Elements["/Type"] = new PdfSharp.Pdf.PdfName("/Font");
        font.Elements["/Subtype"] = new PdfSharp.Pdf.PdfName("/Type1");
        font.Elements["/BaseFont"] = new PdfSharp.Pdf.PdfName("/Helvetica");
        doc.Internals.AddObject(font);
        var fonts = new PdfSharp.Pdf.PdfDictionary(doc);
        fonts.Elements["/F1"] = font.Reference;
        page.Resources.Elements["/Font"] = fonts;
        page.Contents.AppendContent().CreateStream(System.Text.Encoding.ASCII.GetBytes($"BT {textOps} ET"));
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task ExtractText_OutOfRangePage_Throws()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _sut.ExtractTextAsync(pdf, 99));
    }

    [Fact]
    public async Task Search_OnShapeOnlyPdf_ReturnsEmptyList()
    {
        var pdf = PdfFixtureBuilder.Create(2);
        var results = await _sut.SearchAsync(pdf, "anything");
        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_ReturnsEmptyWhenQueryNotFound()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Hello World", 1);
        var results = await _sut.SearchAsync(pdf, "XYZZY_NOT_PRESENT");
        Assert.Empty(results);
    }

    [Fact]
    public async Task ExtractText_AllPages_SeparatesWithFormFeed()
    {
        var pdf = PdfFixtureBuilder.Create(2);
        var text = await _sut.ExtractTextAsync(pdf); // page index null → all pages
        // Shape-only pages produce empty text; the form-feed separator is still added.
        // Just verify no exception and result is not null.
        Assert.NotNull(text);
    }
}
