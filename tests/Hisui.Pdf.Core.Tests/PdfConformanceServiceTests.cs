using System.Text;
using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfConformanceServiceTests
{
    private readonly PdfConformanceService _sut = new(new PdfFormService(new PdfiumRenderer()));

    private static async Task<byte[]> DocWithCommentAndFieldAsync()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Contratto di prova", 2);
        pdf = await new PdfCommentService().AddCommentAsync(pdf, 0, PdfRect.FromLTWH(0.5, 0.5, 0, 0), "nota", "Ada");
        var forms = new PdfFormService(new PdfiumRenderer());
        pdf = await forms.AddFieldAsync(pdf, 1, new PdfRect(0.1, 0.1, 0.5, 0.14), PdfFormFieldType.Text, "Nome");
        return await forms.FillFieldsAsync(pdf, new Dictionary<string, string> { ["Nome"] = "Ada" });
    }

    [Theory]
    [InlineData(PdfProfile.PdfA1b)]
    [InlineData(PdfProfile.PdfA2b)]
    [InlineData(PdfProfile.PdfA3u)]
    public async Task PdfA_CheckFindsProblems_ConvertFixesThem(PdfProfile profile)
    {
        var pdf = await DocWithCommentAndFieldAsync();

        var before = await _sut.CheckAsync(pdf, profile);
        Assert.False(before.Passes);
        Assert.Contains(before.Issues, i => i.Code == "OutputIntent");
        Assert.Contains(before.Issues, i => i.Code == "Xmp");

        var (converted, remaining) = await _sut.ConvertAsync(pdf, profile, new ConversionOptions());

        Assert.True(remaining.Passes, string.Join("; ", remaining.Issues.Select(i => i.Code + " " + i.Detail)));
        using var doc = PdfReader.Open(new MemoryStream(converted), PdfDocumentOpenMode.Import);
        Assert.Equal(profile.Version(), doc.Version);
        var xmp = Encoding.UTF8.GetString(((PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)doc.Internals.Catalog.Elements["/Metadata"]!).Value).Stream!.UnfilteredValue);
        Assert.Contains($"<pdfaid:part>{profile.PdfAPart()}</pdfaid:part>", xmp);
        Assert.Contains("Ada", await new PdfTextExtractor().ExtractTextAsync(converted, 1)); // field value burnt in
        Assert.NotEmpty(await new PdfiumRenderer().RenderPageToPngAsync(converted, 0, 72));
    }

    [Fact]
    public async Task PdfUA_FixesLanguageTitleAndTabs_ButReportsMissingTags()
    {
        var pdf = await DocWithCommentAndFieldAsync();
        var (_, remaining) = await _sut.ConvertAsync(pdf, PdfProfile.PdfUA1, new ConversionOptions { Title = "Contratto", Language = "it-IT" });

        Assert.Contains(remaining.Issues, i => i.Code == "NotTagged" && i.Severity == IssueSeverity.Error);
        Assert.DoesNotContain(remaining.Issues, i => i.Code is "NoLanguage" or "NoTitle" or "DisplayDocTitle" or "TabOrder");
    }

    [Theory]
    [InlineData(PdfProfile.Pdf14, "%PDF-1.4")]
    [InlineData(PdfProfile.Pdf20, "%PDF-2.0")]
    public async Task Version_IsWrittenInTheHeader(PdfProfile profile, string header)
    {
        var (converted, remaining) = await _sut.ConvertAsync(PdfFixtureBuilder.Create(1), profile, new ConversionOptions());
        Assert.StartsWith(header, Encoding.ASCII.GetString(converted, 0, 8));
        Assert.True(remaining.Passes);
    }

    [Fact]
    public async Task PdfX4_AddsTrimBoxes_ButNeedsAPrintOutputIntent()
    {
        var (_, remaining) = await _sut.ConvertAsync(PdfFixtureBuilder.CreateWithText("x"), PdfProfile.PdfX4, new ConversionOptions());
        Assert.DoesNotContain(remaining.Issues, i => i.Code == "TrimBox");
        Assert.Contains(remaining.Issues, i => i.Code == "PrintOutputIntent" && !i.Fixable);
    }
}
