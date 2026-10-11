using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class ReadingOrderTests
{
    private static string Order(params (string Name, double L, double T, double R, double B)[] f) =>
        string.Join(",", ReadingOrder.Sort(f.Reverse(), x => new PdfRect(x.L, x.T, x.R, x.B)).Select(x => x.Name));

    [Fact]
    public void SingleColumn_TopToBottom() =>
        Assert.Equal("a,b,c", Order(("a", .1, .1, .5, .12), ("b", .1, .2, .5, .22), ("c", .1, .3, .5, .32)));

    [Fact]
    public void SameRow_LeftToRight() =>
        Assert.Equal("a,b,c", Order(("a", .1, .1, .2, .12), ("b", .3, .1, .4, .12), ("c", .5, .1, .6, .12)));

    [Fact]
    public void TwoColumns_LeftColumnFirstThenRight() =>
        Assert.Equal("l1,l2,l3,r1,r2,r3", Order(
            ("l1", .05, .10, .45, .12), ("r1", .55, .10, .95, .12),
            ("l2", .05, .20, .45, .22), ("r2", .55, .20, .95, .22),
            ("l3", .05, .30, .45, .32), ("r3", .55, .30, .95, .32)));

    [Fact]
    public void FullWidthFieldSeparatesBands_ColumnsInsideEachBand() =>
        Assert.Equal("l1,l2,r1,r2,wide,l3,r3", Order(
            ("l1", .05, .10, .45, .12), ("r1", .55, .10, .95, .12),
            ("l2", .05, .20, .45, .22), ("r2", .55, .20, .95, .22),
            ("wide", .05, .40, .95, .42),
            ("l3", .05, .60, .45, .62), ("r3", .55, .60, .95, .62)));

    [Fact]
    public void SideBySideGrids_LeftGridRowByRowThenRightGrid() =>
        Assert.Equal("a1,a2,b1,b2,c1,c2,d1,d2", Order(
            ("a1", .05, .10, .10, .12), ("a2", .105, .10, .155, .12), ("c1", .60, .10, .65, .12), ("c2", .655, .10, .705, .12),
            ("b1", .05, .125, .10, .145), ("b2", .105, .125, .155, .145), ("d1", .60, .125, .65, .145), ("d2", .655, .125, .705, .145)));

    [Fact]
    public void LabelCrossingTheGutter_MeansSameRow_NotColumn()
    {
        // Narrow tick boxes on the left, wide blanks on the right: their labels fill the gutter, so rows win.
        var items = new[]
        {
            (Name: "box1", Rect: new PdfRect(.07, .10, .085, .12), Extent: new PdfRect(.07, .10, .85, .12)),
            (Name: "wide", Rect: new PdfRect(.24, .20, .93, .22), Extent: new PdfRect(.05, .20, .93, .22)),
            (Name: "box2", Rect: new PdfRect(.07, .30, .085, .32), Extent: new PdfRect(.07, .30, .85, .32)),
        };
        Assert.Equal("box1,wide,box2", string.Join(",", ReadingOrder.Sort(items.Reverse(), x => x.Extent, x => x.Rect).Select(x => x.Name)));
    }

    [Fact]
    public void WideExtent_KeepsRowOrderByOwnRect()
    {
        var items = new[]
        {
            (Name: "a", Rect: new PdfRect(.20, .10, .30, .12), Extent: new PdfRect(.05, .10, .95, .12)),
            (Name: "b", Rect: new PdfRect(.40, .10, .50, .12), Extent: new PdfRect(.05, .10, .95, .12)),
            (Name: "c", Rect: new PdfRect(.60, .10, .70, .12), Extent: new PdfRect(.05, .10, .95, .12)),
        };
        Assert.Equal("a,b,c", string.Join(",", ReadingOrder.Sort(items.Reverse(), x => x.Extent, x => x.Rect).Select(x => x.Name)));
    }

    [Fact]
    public async Task AddedFields_KeepSpecOrderInAnnotsAndSetTabsToStructure()
    {
        var forms = new PdfFormService(new PdfiumRenderer());
        var specs = new[] { "z", "m", "a" }.Select((n, i) => new FormFieldSpec
        {
            PageIndex = 0, Type = PdfFormFieldType.Text, Name = n,
            Rect = new PdfRect(.1, .1 + i * .1, .5, .15 + i * .1),
        }).ToList();

        var pdf = await forms.AddFieldsAsync(PdfFixtureBuilder.Create(1), specs);

        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        Assert.Equal("/S", doc.Pages[0].Elements.GetName("/Tabs"));
        Assert.Equal(["z", "m", "a"], (await forms.ReadFieldsAsync(pdf)).Select(f => f.Name));
    }
}
