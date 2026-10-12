using Hisui.Pdf.Core.Services;

namespace Hisui.Pdf.Core.Abstractions;

/// <summary>Installed and installable OCR languages (downloads happen only on explicit request).</summary>
public interface IOcrLanguageStore
{
    IReadOnlyList<OcrLanguage> List();

    /// <summary>A tessdata folder holding every language in <paramref name="codes"/> (all must be installed).</summary>
    string DataPathFor(IEnumerable<string> codes);

    /// <summary>Where a language is downloaded from.</summary>
    Uri SourceFor(string code);

    /// <summary>Downloads and installs a catalog language.</summary>
    Task InstallAsync(string code, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>Installs a local "&lt;code&gt;.traineddata" file; returns its language code.</summary>
    Task<string> ImportAsync(string path, CancellationToken ct = default);

    /// <summary>Removes a language installed by the user (bundled languages stay).</summary>
    void Remove(string code);
}
