using System.Net;
using Hisui.Pdf.Core.Services;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class OcrLanguageStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hisui-ocr-test-" + Guid.NewGuid().ToString("N"));
    private string Bundled => Path.Combine(_root, "bundled");
    private string User => Path.Combine(_root, "user");
    // The app's bundled English model, found by walking up from the test binaries to the repository.
    private static readonly string RealEng = FindUp(Path.Combine("src", "Hisui.Pdf.App", "tessdata", "eng.traineddata"));

    private static string FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        return relative;
    }

    public OcrLanguageStoreTests()
    {
        Directory.CreateDirectory(Bundled);
        File.WriteAllBytes(Path.Combine(Bundled, "ita.traineddata"), new byte[10]);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private sealed class Handler(byte[] body) : HttpMessageHandler
    {
        public Uri? Requested;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    [Fact]
    public void List_MarksBundledAndOffersTheCatalog_NothingDownloaded()
    {
        var store = new OcrLanguageStore(Bundled, User, new HttpClient(new Handler([])));
        var list = store.List();
        Assert.True(list.Single(l => l.Code == "ita").Bundled);
        Assert.False(list.Single(l => l.Code == "deu").Installed);
        Assert.False(Directory.Exists(User));
        Assert.Equal(Bundled, store.DataPathFor(["ita"]));
    }

    [Fact]
    public async Task Install_DownloadsFromThePinnedSource_AndMixingCopiesBundled()
    {
        Assert.SkipUnless(File.Exists(RealEng), "bundled eng.traineddata not found");
        var handler = new Handler(File.ReadAllBytes(RealEng)); // any valid traineddata stands in for the download
        var store = new OcrLanguageStore(Bundled, User, new HttpClient(handler));

        await store.InstallAsync("deu");

        Assert.Equal("https://github.com/tesseract-ocr/tessdata_fast/raw/4.1.0/deu.traineddata", handler.Requested!.ToString());
        Assert.True(store.List().Single(l => l.Code == "deu").Installed);
        Assert.Equal(User, store.DataPathFor(["ita", "deu"]));
        Assert.True(File.Exists(Path.Combine(User, "ita.traineddata")));

        store.Remove("deu");
        Assert.False(store.List().Single(l => l.Code == "deu").Installed);
    }

    [Fact]
    public async Task Install_RejectsGarbage()
    {
        var store = new OcrLanguageStore(Bundled, User, new HttpClient(new Handler(new byte[200_000])));
        await Assert.ThrowsAnyAsync<Exception>(() => store.InstallAsync("fra"));
        Assert.False(File.Exists(Path.Combine(User, "fra.traineddata")));
    }
}
