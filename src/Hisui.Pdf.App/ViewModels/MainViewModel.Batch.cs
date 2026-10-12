using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.ViewModels;

/// <summary>What a batch run does to each file.</summary>
public enum BatchAction { Ocr, Compress, PdfA, Word, Excel, PowerPoint, Text }

/// <summary>Files, action and output folder chosen in the batch dialog.</summary>
public sealed record BatchRequest(IReadOnlyList<string> Files, BatchAction Action, string OutputFolder);

/// <summary>OCR language settings and batch processing of many files.</summary>
public partial class MainViewModel
{
    /// <summary>Set by the view: the OCR languages dialog; returns the languages to use (null = cancelled).</summary>
    public Func<IOcrLanguageStore, IReadOnlyList<string>, Task<IReadOnlyList<string>?>>? PromptOcrLanguagesAsync { get; set; }

    /// <summary>Set by the view: files, action and output folder for a batch run (null = cancelled).</summary>
    public Func<Task<BatchRequest?>>? PromptBatchAsync { get; set; }

    /// <summary>OCR languages in use, e.g. ["ita", "eng"]: only installed ones (falls back to the bundled pair).</summary>
    private IReadOnlyList<string> OcrLanguages
    {
        get
        {
            var installed = _ocrLanguages.List().Where(l => l.Installed).Select(l => l.Code).ToHashSet();
            var chosen = (_settings.Settings.OcrLanguages ?? "ita+eng").Split('+', StringSplitOptions.RemoveEmptyEntries).Where(installed.Contains).ToList();
            return chosen.Count > 0 ? chosen : installed.Intersect(["ita", "eng"]).ToList();
        }
    }

    private OcrOptions CurrentOcrOptions()
    {
        var langs = OcrLanguages;
        return new OcrOptions { Language = string.Join('+', langs), TessDataPath = langs.Count > 0 ? _ocrLanguages.DataPathFor(langs) : null };
    }

    [RelayCommand]
    private async Task ChooseOcrLanguagesAsync()
    {
        if (PromptOcrLanguagesAsync is null || await PromptOcrLanguagesAsync(_ocrLanguages, OcrLanguages) is not { Count: > 0 } chosen) return;
        _settings.Settings.OcrLanguages = string.Join('+', chosen);
        _settings.Save();
        StatusMessage = _loc.Format("Ocr.LanguagesSet", string.Join(", ", chosen));
    }

    /// <summary>Runs one action over many files, writing the results into a folder; a failure skips only that file.</summary>
    [RelayCommand]
    private async Task BatchAsync()
    {
        if (PromptBatchAsync is null || await PromptBatchAsync() is not { Files.Count: > 0 } request) return;
        var report = new StringBuilder();
        var done = 0;
        await RunBusyAsync(_loc["Batch.Running"], async ct =>
        {
            for (var i = 0; i < request.Files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = request.Files[i];
                var name = Path.GetFileNameWithoutExtension(file);
                StatusMessage = _loc.Format("Batch.Progress", i + 1, request.Files.Count, Path.GetFileName(file));
                try
                {
                    var bytes = await File.ReadAllBytesAsync(file, ct);
                    if (_security.IsEncrypted(bytes)) { report.AppendLine("✖ " + Path.GetFileName(file) + ": " + _loc["Batch.Encrypted"]); continue; }
                    var (output, ext, note) = await RunBatchActionAsync(request.Action, bytes, ct);
                    var target = UniquePath(Path.Combine(request.OutputFolder, name + ext));
                    await File.WriteAllBytesAsync(target, output, ct);
                    report.AppendLine("✔ " + Path.GetFileName(file) + " → " + Path.GetFileName(target) + (note is null ? "" : "  (" + note + ")"));
                    done++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    report.AppendLine("✖ " + Path.GetFileName(file) + ": " + ex.Message);
                }
            }
        });
        StatusMessage = _loc.Format("Batch.Done", done, request.Files.Count);
        if (ShowReportAsync is not null && report.Length > 0) await ShowReportAsync(_loc["Batch.Title"], report.ToString());
    }

    private async Task<(byte[] Output, string Extension, string? Note)> RunBatchActionAsync(BatchAction action, byte[] pdf, CancellationToken ct)
    {
        switch (action)
        {
            case BatchAction.Ocr:
                var scanned = await _ocr.DetectScannedPagesAsync(pdf, ct);
                return scanned.Count == 0
                    ? (pdf, "-ocr.pdf", _loc["Batch.NoScans"])
                    : (await _ocr.MakeSearchableAsync(pdf, CurrentOcrOptions(), ct), "-ocr.pdf", null);
            case BatchAction.Compress:
                var small = await _optimizer.OptimizeAsync(pdf, options: null, ct);
                return small.Length < pdf.Length ? (small, "-compresso.pdf", $"{100 * small.Length / pdf.Length}%") : (pdf, "-compresso.pdf", _loc["Batch.NoGain"]);
            case BatchAction.PdfA:
                var (pdfa, report) = await _conformance.ConvertAsync(pdf, PdfProfile.PdfA2b, new ConversionOptions(), ct);
                return (pdfa, "-PDFA2b.pdf", report.Passes ? null : _loc.Format("Batch.PdfAIssues", report.Issues.Count(x => x.Severity == IssueSeverity.Error)));
            case BatchAction.Word:
                return (await _wordExporter.ExportAsync(pdf, ct), ".docx", null);
            case BatchAction.Excel:
                return (await _office.ExportXlsxAsync(pdf, ct), ".xlsx", null);
            case BatchAction.PowerPoint:
                return (await _office.ExportPptxAsync(pdf, 150, ct), ".pptx", null);
            default:
                var text = await _textExtractor.ExtractTextAsync(pdf, null, ct);
                return (Encoding.UTF8.GetBytes(text.Replace("\f", "\r\n\r\n")), ".txt", null);
        }
    }

    /// <summary>"name.ext", or "name (2).ext"… when it exists, so a batch never overwrites.</summary>
    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
