using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

// Architecture test: the app is MIT-licensed, so no resolved NuGet dependency (direct or transitive)
// may carry an AGPL/GPL license. PDFsharp >= 6.5 is AGPL-3.0-only; we are pinned to 6.2.4 (MIT).
public class DependencyLicenseTests
{
    private sealed record ResolvedPackage(string Id, string Version, string? Folder);

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Hisui.Pdf.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Hisui.Pdf.slnx not found above " + AppContext.BaseDirectory);
    }

    // Standard (non-redirected) layout: <project>/obj/project.assets.json.
    private static List<string> FindAssetsFiles()
    {
        var root = FindRepoRoot();
        var files = new List<string>();
        foreach (var top in new[] { "src", "tests" })
        {
            var dir = Path.Combine(root, top);
            if (!Directory.Exists(dir)) continue;
            foreach (var project in Directory.GetDirectories(dir))
            {
                var assets = Path.Combine(project, "obj", "project.assets.json");
                if (File.Exists(assets)) files.Add(assets);
            }
        }
        return files;
    }

    private static List<ResolvedPackage> LoadResolvedPackages()
    {
        var assetsFiles = FindAssetsFiles();
        Assert.True(assetsFiles.Count > 0,
            "No project.assets.json found under src/ or tests/ (expected <project>/obj/project.assets.json). Run a restore before the tests.");

        var result = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in assetsFiles)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var roots = doc.RootElement.TryGetProperty("packageFolders", out var pf)
                ? pf.EnumerateObject().Select(p => p.Name).ToList()
                : new List<string>();
            if (!doc.RootElement.TryGetProperty("libraries", out var libs)) continue;

            foreach (var lib in libs.EnumerateObject())
            {
                if (!lib.Value.TryGetProperty("type", out var type) || type.GetString() != "package") continue;
                var slash = lib.Name.IndexOf('/');
                if (slash < 0) continue;
                var id = lib.Name[..slash];
                var version = lib.Name[(slash + 1)..];
                var key = lib.Name;
                if (result.ContainsKey(key)) continue;

                string? folder = null;
                if (lib.Value.TryGetProperty("path", out var path) && path.GetString() is { } rel)
                    folder = roots.Select(r => Path.Combine(r, rel)).FirstOrDefault(Directory.Exists);
                result[key] = new ResolvedPackage(id, version, folder);
            }
        }
        return [.. result.Values];
    }

    private static string? ReadLicenseExpression(ResolvedPackage package)
    {
        if (package.Folder is null) return null;
        var nuspec = Path.Combine(package.Folder, package.Id.ToLowerInvariant() + ".nuspec");
        if (!File.Exists(nuspec))
            nuspec = Directory.EnumerateFiles(package.Folder, "*.nuspec").FirstOrDefault() ?? "";
        if (!File.Exists(nuspec)) return null;

        var metadata = XDocument.Load(nuspec).Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");
        var license = metadata?.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "license" && (string?)e.Attribute("type") == "expression");
        var value = license?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    // True when any SPDX identifier in the expression is AGPL-* or GPL-* (LGPL-* is allowed).
    internal static bool IsForbidden(string expression)
    {
        var separators = new[] { ' ', '\t', '\r', '\n', '(', ')' };
        var operators = new[] { "AND", "OR", "WITH" };
        foreach (var token in expression.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (operators.Contains(token, StringComparer.OrdinalIgnoreCase)) continue;
            if (token.StartsWith("AGPL-", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("GPL-", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    [Fact]
    public void Dependencies_HaveNoCopyleftLicense()
    {
        var packages = LoadResolvedPackages();
        var offenders = new List<string>();
        foreach (var package in packages)
        {
            var expression = ReadLicenseExpression(package);
            if (expression is not null && IsForbidden(expression))
                offenders.Add($"{package.Id} {package.Version}: {expression}");
        }

        Assert.True(offenders.Count == 0,
            "Forbidden AGPL/GPL dependencies resolved:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));

        // Guard against a vacuous pass: at least one nuspec license must have been readable.
        Assert.True(packages.Any(p => ReadLicenseExpression(p) is not null),
            "No nuspec license expression could be read for any resolved package (NuGet packages folder not found?).");
    }

    [Fact]
    public void PdfSharp_IsPinnedToMitVersion()
    {
        var pdfSharp = LoadResolvedPackages()
            .Where(p => string.Equals(p.Id, "PDFsharp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(pdfSharp.Count > 0, "PDFsharp is not present in the resolved package set.");
        Assert.Equal(["6.2.4"], pdfSharp.Select(p => p.Version).Distinct().ToList());

        // Proves the nuspec-reading path works on the pinned package.
        foreach (var package in pdfSharp)
            Assert.Equal("MIT", ReadLicenseExpression(package));
    }

    [Theory]
    [InlineData("AGPL-3.0-only", true)]
    [InlineData("GPL-2.0-or-later", true)]
    [InlineData("MIT OR GPL-3.0-only", true)]
    [InlineData("MIT", false)]
    [InlineData("Apache-2.0", false)]
    [InlineData("LGPL-2.1-only", false)]
    [InlineData("BSD-3-Clause", false)]
    [InlineData("GPL-3.0+", true)]
    [InlineData("GPL-2.0-only WITH Classpath-exception-2.0", true)]
    [InlineData("(MIT OR Apache-2.0)", false)]
    public void IsForbidden_ClassifiesSpdxExpressions(string expression, bool expected)
    {
        Assert.Equal(expected, IsForbidden(expression));
    }
}
