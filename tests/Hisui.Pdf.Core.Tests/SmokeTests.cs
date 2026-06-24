using Hisui.Pdf.Core.Tests.Fixtures;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void FixtureBuilder_ProducesNonEmptyPdf()
    {
        var bytes = PdfFixtureBuilder.Create(3);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        // Every PDF starts with the "%PDF-" header.
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
    }
}
