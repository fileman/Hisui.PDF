using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class TesseractOcrEngineTests
{
    private readonly TesseractOcrEngine _sut = new();

    [Fact]
    public async Task Recognize_WithMissingTessDataDirectory_ThrowsOcrUnavailable()
    {
        var options = new OcrOptions { TessDataPath = Path.Combine(Path.GetTempPath(), "hisui-no-such-tessdata-" + Guid.NewGuid().ToString("N")) };

        var ex = await Assert.ThrowsAsync<OcrUnavailableException>(
            () => _sut.RecognizeAsync([1, 2, 3], 0, options));
        Assert.Contains("language data", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Recognize_WithMissingLanguageFile_ThrowsOcrUnavailable()
    {
        // A real directory but without the requested traineddata file.
        var dir = Directory.CreateTempSubdirectory("hisui-tessdata-").FullName;
        try
        {
            var options = new OcrOptions { TessDataPath = dir, Language = "zzz" };

            var ex = await Assert.ThrowsAsync<OcrUnavailableException>(
                () => _sut.RecognizeAsync([1, 2, 3], 0, options));
            Assert.Contains("zzz", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Recognize_NullImage_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _sut.RecognizeAsync(null!, 0, new OcrOptions()));
    }
}
