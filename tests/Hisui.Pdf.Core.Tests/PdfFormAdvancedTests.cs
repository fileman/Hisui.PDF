using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfFormAdvancedTests
{
    private readonly PdfFormService _sut = new(new PdfiumRenderer());

    private async Task<byte[]> TwoFieldsAsync()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        pdf = await _sut.AddFieldAsync(pdf, 0, new PdfRect(0.1, 0.1, 0.5, 0.14), PdfFormFieldType.Text, "Nome");
        pdf = await _sut.AddFieldAsync(pdf, 0, new PdfRect(0.1, 0.2, 0.14, 0.23), PdfFormFieldType.CheckBox, "Ok");
        return await _sut.AddFieldAsync(pdf, 0, new PdfRect(0.1, 0.3, 0.5, 0.34), PdfFormFieldType.Text, "Città");
    }

    [Fact]
    public async Task Properties_RoundTrip_IncludingRenameAndFormat()
    {
        var pdf = await TwoFieldsAsync();
        var props = await _sut.ReadPropertiesAsync(pdf, 0, 0);
        Assert.Equal("Nome", props.Name);
        Assert.Equal(FieldFormat.None, props.Format);

        pdf = await _sut.SetPropertiesAsync(pdf, 0, 0, props with
        {
            Name = "Importo", Required = true, MaxLength = 10, FontName = "Cour", FontSize = 9,
            Alignment = FieldAlignment.Right, Format = FieldFormat.Currency, Decimals = 2, Tooltip = "Totale",
        });

        var back = await _sut.ReadPropertiesAsync(pdf, 0, 0);
        Assert.Equal(("Importo", true, 10, "Cour", 9.0, FieldAlignment.Right, FieldFormat.Currency, "Totale"),
            (back.Name, back.Required, back.MaxLength, back.FontName, back.FontSize, back.Alignment, back.Format, back.Tooltip));
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.SetPropertiesAsync(pdf, 0, 0, back with { Name = "Ok" }));
    }

    [Fact]
    public async Task Reorder_ChangesTabOrder_AndDeleteMany_RemovesFields()
    {
        var pdf = await TwoFieldsAsync();
        pdf = await _sut.ReorderWidgetsAsync(pdf, 0, [2, 0, 1]);
        Assert.Equal(["Città", "Nome", "Ok"], (await _sut.ReadWidgetsAsync(pdf, 0)).Select(w => w.Name));

        pdf = await _sut.DeleteWidgetsAsync(pdf, 0, [0, 2]);
        Assert.Equal(["Nome"], (await _sut.ReadWidgetsAsync(pdf, 0)).Select(w => w.Name));
    }

    [Theory]
    [InlineData(FormDataFormat.Xfdf)]
    [InlineData(FormDataFormat.Fdf)]
    [InlineData(FormDataFormat.Csv)]
    public async Task ExportImport_RoundTrip(FormDataFormat format)
    {
        var pdf = await TwoFieldsAsync();
        var filled = await _sut.FillFieldsAsync(pdf, new Dictionary<string, string> { ["Nome"] = "Zoë (test); \"q\"", ["Ok"] = "true", ["Città"] = "Ancona" });

        var data = await _sut.ExportDataAsync(filled, format);
        var imported = await _sut.ImportDataAsync(pdf, data, format);

        var w = await _sut.ReadWidgetsAsync(imported, 0);
        Assert.Equal(["Zoë (test); \"q\"", "true", "Ancona"], w.Select(x => x.Value));
    }

    [Fact]
    public async Task Reset_ClearsValues()
    {
        var pdf = await TwoFieldsAsync();
        pdf = await _sut.FillFieldsAsync(pdf, new Dictionary<string, string> { ["Nome"] = "Ada", ["Ok"] = "true" });
        pdf = await _sut.ResetFieldsAsync(pdf);
        var w = await _sut.ReadWidgetsAsync(pdf, 0);
        Assert.Null(w[0].Value);
        Assert.Equal("false", w[1].Value);
    }

    [Fact]
    public async Task FlattenFields_BurnsSelectedValuesAsTextAndRemovesThem()
    {
        var pdf = await TwoFieldsAsync();
        pdf = await _sut.FillFieldsAsync(pdf, new Dictionary<string, string> { ["Nome"] = "Lovelace", ["Città"] = "Ancona" });

        pdf = await _sut.FlattenFieldsAsync(pdf, ["Nome"]);

        Assert.Equal(["Ok", "Città"], (await _sut.ReadWidgetsAsync(pdf, 0)).Select(w => w.Name));
        Assert.Contains("Lovelace", await new PdfTextExtractor().ExtractTextAsync(pdf, 0));
    }

    [Theory]
    [InlineData(FieldFormat.Currency, "1.234,5", "1234.5", "€ 1.234,50")]
    [InlineData(FieldFormat.Number, "1234.5", "1234.5", "1.234,50")]
    [InlineData(FieldFormat.Percent, "25", "0.25", "25,00%")]
    [InlineData(FieldFormat.Date, "3/2/2026", "03/02/2026", "03/02/2026")]
    public void Formatter_ParsesAndDisplays(FieldFormat format, string typed, string raw, string shown)
    {
        Assert.Equal(raw, FieldValueFormatter.ToRaw(format, "dd/mm/yyyy", typed));
        Assert.Equal(shown, FieldValueFormatter.ToDisplay(format, 2, raw));
        Assert.Null(FieldValueFormatter.ToRaw(format, "dd/mm/yyyy", "abc"));
    }
}
