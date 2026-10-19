using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfCompareAndPatternTests
{
    [Fact]
    public async Task Compare_FindsRemovedAndAddedWords()
    {
        var a = PdfFixtureBuilder.CreateWithText("Il prezzo totale è 100 euro");
        var b = PdfFixtureBuilder.CreateWithText("Il prezzo finale è 120 euro");

        var result = await new PdfCompareService(new PdfTextExtractor()).CompareAsync(a, b);

        var page = Assert.Single(result.Pages);
        Assert.Contains("totale", page.Removed);
        Assert.Contains("100", page.Removed);
        Assert.Contains("finale", page.Added);
        Assert.Contains("120", page.Added);
        Assert.NotEmpty(result.Marks);
        Assert.True((await new PdfCompareService(new PdfTextExtractor()).CompareAsync(a, a)).IsIdentical);
    }

    [Theory]
    [InlineData("CodiceFiscale", "Codice RSSMRA85T10A562S cliente", "RSSMRA85T10A562S")]
    [InlineData("Iban", "IBAN IT60 X054 2811 1010 0000 0123 456 grazie", "IT60 X054 2811 1010 0000 0123 456")]
    [InlineData("Email", "scrivi a mario.rossi@example.it oggi", "mario.rossi@example.it")]
    public async Task Patterns_FindIdentifiersWithTheirBoxes(string pattern, string text, string expected)
    {
        var pdf = PdfFixtureBuilder.CreateWithText(text);
        var hits = await new PdfTextExtractor().SearchPatternAsync(pdf, RedactionPatterns.Builtin[pattern]);
        var hit = Assert.Single(hits);
        Assert.Equal(expected, hit.MatchedText);
        Assert.NotEmpty(hit.Boxes);
    }
}
