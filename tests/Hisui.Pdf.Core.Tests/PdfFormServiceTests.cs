using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfFormServiceTests
{
    private readonly PdfFormService _sut = new(new PdfiumRenderer());

    [Fact]
    public async Task AddField_CreatesReadableFieldsOfEachType()
    {
        var pdf = PdfFixtureBuilder.Create(2);
        var r = new PdfRect(0.1, 0.1, 0.5, 0.15);
        pdf = await _sut.AddFieldAsync(pdf, 0, r, PdfFormFieldType.Text, "Name");
        pdf = await _sut.AddFieldAsync(pdf, 1, r, PdfFormFieldType.CheckBox);
        pdf = await _sut.AddFieldAsync(pdf, 1, r, PdfFormFieldType.ComboBox, "Pick", ["a", "b"]);

        var fields = await _sut.ReadFieldsAsync(pdf);
        Assert.Equal(["Name", "CheckBox1", "Pick"], fields.Select(f => f.Name));
        Assert.Equal([PdfFormFieldType.Text, PdfFormFieldType.CheckBox, PdfFormFieldType.ComboBox], fields.Select(f => f.Type));
        Assert.Equal(["a", "b"], fields[2].Options);

        // Created fields are fillable and the document still renders.
        var filled = await _sut.FillFieldsAsync(pdf, new Dictionary<string, string> { ["Name"] = "Ada", ["CheckBox1"] = "true" });
        var read = await _sut.ReadFieldsAsync(filled);
        Assert.Equal("Ada", read.First(f => f.Name == "Name").Value);
        Assert.Equal("true", read.First(f => f.Name == "CheckBox1").Value);
        Assert.NotEmpty(await new PdfiumRenderer().RenderPageToPngAsync(filled, 0, 72));
    }

    [Fact]
    public async Task AddField_RadioGroupSharesNameAndGainsChoices()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        pdf = await _sut.AddFieldAsync(pdf, 0, new PdfRect(0.1, 0.1, 0.15, 0.13), PdfFormFieldType.RadioButton, "Size", ["Small"]);
        pdf = await _sut.AddFieldAsync(pdf, 0, new PdfRect(0.1, 0.2, 0.15, 0.23), PdfFormFieldType.RadioButton, "Size", ["Large"]);
        pdf = await _sut.AddFieldAsync(pdf, 0, new PdfRect(0.5, 0.1, 0.9, 0.15), PdfFormFieldType.ListBox, "L", ["x", "y"]);
        pdf = await _sut.AddFieldAsync(pdf, 0, new PdfRect(0.5, 0.3, 0.9, 0.4), PdfFormFieldType.Signature);

        var fields = await _sut.ReadFieldsAsync(pdf);
        Assert.Equal(["Size", "L", "Signature1"], fields.Select(f => f.Name));
        Assert.Equal(PdfFormFieldType.RadioButton, fields[0].Type);
        Assert.Equal(PdfFormFieldType.ListBox, fields[1].Type);
        Assert.Equal(["x", "y"], fields[1].Options);

        // Same choice twice in a group is rejected.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.AddFieldAsync(pdf, 0, new PdfRect(0.1, 0.5, 0.15, 0.53), PdfFormFieldType.RadioButton, "Size", ["Small"]));
        Assert.NotEmpty(await new PdfiumRenderer().RenderPageToPngAsync(pdf, 0, 72));
    }

    [Fact]
    public async Task Fields_SurviveRebuildThroughSession_IncludingRadioGroups()
    {
        var pdf = PdfFixtureBuilder.Create(2);
        pdf = await _sut.AddFieldAsync(pdf, 0, new PdfRect(0.1, 0.1, 0.4, 0.15), PdfFormFieldType.Text, "A");
        pdf = await _sut.AddFieldAsync(pdf, 1, new PdfRect(0.1, 0.1, 0.15, 0.13), PdfFormFieldType.RadioButton, "R", ["x"]);
        pdf = await _sut.AddFieldAsync(pdf, 1, new PdfRect(0.1, 0.2, 0.15, 0.23), PdfFormFieldType.RadioButton, "R", ["y"]);

        var session = new PdfDocumentSession();
        session.AddSource(pdf, 2);
        var built = await new PdfPageService().BuildFromSessionAsync(session);

        var fields = await _sut.ReadFieldsAsync(built);
        Assert.Equal(["A", "R"], fields.Select(f => f.Name));
    }

    [Fact]
    public async Task AddField_DuplicateName_Throws()
    {
        var pdf = await _sut.AddFieldAsync(PdfFixtureBuilder.Create(1), 0, new PdfRect(0, 0, 0.3, 0.1), PdfFormFieldType.Text, "X");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.AddFieldAsync(pdf, 0, new PdfRect(0, 0.2, 0.3, 0.3), PdfFormFieldType.Text, "X"));
    }

    [Fact]
    public async Task ReadFields_OnNonFormPdf_ReturnsEmptyList()
    {
        var pdf = PdfFixtureBuilder.Create(2);
        var fields = await _sut.ReadFieldsAsync(pdf);
        Assert.Empty(fields);
    }

    [Fact]
    public async Task FillFields_OnNonFormPdf_ReturnsOriginalReference()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        // No values supplied → short-circuit before even opening the document.
        var result = await _sut.FillFieldsAsync(pdf, new Dictionary<string, string>());
        Assert.Same(pdf, result);
    }

    [Fact]
    public async Task FlattenAsync_PreservesPageCount()
    {
        var pdf = PdfFixtureBuilder.Create(3);
        var flattened = await _sut.FlattenAsync(pdf);

        using var doc = PdfReader.Open(new MemoryStream(flattened), PdfDocumentOpenMode.Import);
        Assert.Equal(3, doc.PageCount);
    }

    [Fact]
    public async Task FlattenAsync_ProducesValidPdf()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        var result = await _sut.FlattenAsync(pdf);
        Assert.True(result[0] == '%' && result[1] == 'P' && result[2] == 'D' && result[3] == 'F');
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public async Task Widgets_ReadFillMoveDelete_RoundTrip(int rotate)
    {
        var pdf = PdfFixtureBuilder.Create(1);
        if (rotate != 0)
        {
            using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Modify);
            doc.Pages[0].Rotate = rotate;
            using var ms = new MemoryStream();
            doc.Save(ms);
            pdf = ms.ToArray();
        }
        var r = new PdfRect(0.1, 0.2, 0.5, 0.25);
        pdf = await _sut.AddFieldAsync(pdf, 0, r, PdfFormFieldType.Text, "Name");
        pdf = await _sut.AddFieldAsync(pdf, 0, new PdfRect(0.6, 0.2, 0.65, 0.23), PdfFormFieldType.CheckBox, "Ok");

        var widgets = await _sut.ReadWidgetsAsync(pdf, 0);
        Assert.Equal(["Name", "Ok"], widgets.Select(w => w.Name));
        AssertClose(r, widgets[0].Rect);
        Assert.Equal("false", widgets[1].Value);

        var blank = await new PdfiumRenderer().RenderPageToPngAsync(pdf, 0, 72);
        pdf = await _sut.FillFieldsAsync(pdf, new Dictionary<string, string> { ["Name"] = "Ada Lovelace", ["Ok"] = "true" });
        widgets = await _sut.ReadWidgetsAsync(pdf, 0);
        Assert.Equal("Ada Lovelace", widgets[0].Value);
        Assert.Equal("true", widgets[1].Value);
        Assert.NotEqual(blank, await new PdfiumRenderer().RenderPageToPngAsync(pdf, 0, 72)); // the value is drawn

        var moved = new PdfRect(0.2, 0.5, 0.7, 0.58);
        pdf = await _sut.SetWidgetRectAsync(pdf, 0, widgets[0].AnnotIndex, moved);
        AssertClose(moved, (await _sut.ReadWidgetsAsync(pdf, 0))[0].Rect);

        pdf = await _sut.DeleteWidgetAsync(pdf, 0, widgets[0].AnnotIndex);
        Assert.Equal(["Ok"], (await _sut.ReadWidgetsAsync(pdf, 0)).Select(w => w.Name));
        Assert.Equal(["Ok"], (await _sut.ReadFieldsAsync(pdf)).Select(f => f.Name));

        static void AssertClose(PdfRect expected, PdfRect actual)
        {
            Assert.Equal(expected.Left, actual.Left, 3);
            Assert.Equal(expected.Top, actual.Top, 3);
            Assert.Equal(expected.Right, actual.Right, 3);
            Assert.Equal(expected.Bottom, actual.Bottom, 3);
        }
    }

    [Fact]
    public async Task FillFields_SelectsRadioChoice()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        pdf = await _sut.AddFieldAsync(pdf, 0, new PdfRect(0.1, 0.1, 0.15, 0.13), PdfFormFieldType.RadioButton, "Size", ["Small"]);
        pdf = await _sut.AddFieldAsync(pdf, 0, new PdfRect(0.1, 0.2, 0.15, 0.23), PdfFormFieldType.RadioButton, "Size", ["Large"]);

        pdf = await _sut.FillFieldsAsync(pdf, new Dictionary<string, string> { ["Size"] = "Large" });

        var widgets = await _sut.ReadWidgetsAsync(pdf, 0);
        Assert.All(widgets, w => Assert.Equal("Large", w.Value));
        Assert.Equal(["Small", "Large"], widgets.Select(w => w.OnState));
    }
}
