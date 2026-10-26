using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Hisui.Pdf.App.Services;

/// <summary>
/// Checks the GitHub "latest release" endpoint and compares its tag (vX.Y.Z) to the running assembly version.
/// Failures (offline, rate-limited, malformed) are swallowed: an update check must never disturb the user.
/// </summary>
internal sealed class UpdateService : IUpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/fileman/Hisui.PDF/releases/latest";
    // Only pages and assets of this repository are ever opened or run, whatever the API response says.
    private const string ReleasePagePrefix = "https://github.com/fileman/Hisui.PDF/releases/";
    private const string AssetPrefix = "https://github.com/fileman/Hisui.PDF/releases/download/";

    private static readonly HttpClient Http = CreateClient();
    private readonly ILogger<UpdateService> _log;

    public UpdateService(ILogger<UpdateService> log) => _log = log;

    public Version CurrentVersion { get; } =
        typeof(UpdateService).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : new Version(1, 0, 0);

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await Http.GetAsync(LatestReleaseUrl, cts.Token);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

            var tag = root.GetProperty("tag_name").GetString();
            var url = root.GetProperty("html_url").GetString();
            var msi = FindMsi(root);
            // MSI name (HisuiPDF-X.Y.<commits>-win-x64.msi) carries the real version; the tag only has X.Y.Z
            var versionSource = msi is { } m ? m.Name[(m.Name.IndexOf('-') + 1)..] : tag;
            if (!TryParseVersion(versionSource, out var latest) || url is null || !url.StartsWith(ReleasePagePrefix, StringComparison.Ordinal))
                return null;
            if (latest <= CurrentVersion) return null;

            var notes = root.TryGetProperty("body", out var body) ? body.GetString() : null;
            return new UpdateInfo(latest, tag!, url, notes, msi?.Url, msi?.Sha256);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Update check failed");
            return null;
        }
    }

    public async Task<string?> DownloadInstallerAsync(UpdateInfo update, CancellationToken ct = default)
    {
        if (update.MsiUrl is null || !update.MsiUrl.StartsWith(AssetPrefix, StringComparison.Ordinal)) return null;
        try
        {
            var path = Path.Combine(Path.GetTempPath(), Path.GetFileName(new Uri(update.MsiUrl).LocalPath));
            using var response = await Http.GetAsync(update.MsiUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using (var file = File.Create(path))
                await response.Content.CopyToAsync(file, ct);

            // The file is about to run elevated: it must be byte-for-byte the asset GitHub published.
            if (update.MsiSha256 is { } expected)
            {
                string actual;
                await using (var file = File.OpenRead(path))
                    actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogWarning("Installer digest mismatch: expected {Expected}, got {Actual}", expected, actual);
                    File.Delete(path);
                    return null;
                }
            }
            else
            {
                _log.LogWarning("Release asset has no digest; the installer could not be verified");
            }
            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Installer download failed");
            return null;
        }
    }

    private static (string Name, string Url, string? Sha256)? FindMsi(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets)) return null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.GetProperty("name").GetString();
            var url = a.GetProperty("browser_download_url").GetString();
            if (name is not null && url is not null && name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                && url.StartsWith(AssetPrefix, StringComparison.Ordinal))
            {
                // GitHub publishes "sha256:<hex>" for uploaded assets.
                var digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;
                return (name, url, sha);
            }
        }
        return null;
    }

    internal static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var s = tag.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(['-', '+']); // drop pre-release / build suffix
        if (cut >= 0) s = s[..cut];
        if (!Version.TryParse(s, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HisuiPDF-UpdateCheck");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
