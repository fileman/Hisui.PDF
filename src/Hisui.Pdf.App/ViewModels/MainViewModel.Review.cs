using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.Input;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.ViewModels;

/// <summary>Document comparison and find-and-redact by pattern.</summary>
public partial class MainViewModel
{
    /// <summary>Set by the view: shows a long read-only report (title, text).</summary>
    public Func<string, string, Task>? ShowReportAsync { get; set; }

    /// <summary>Set by the view: picks redaction patterns (built-in keys and/or a custom regex); null = cancelled.</summary>
    public Func<Task<IReadOnlyList<Regex>?>>? PromptRedactionPatternsAsync { get; set; }

    /// <summary>Compares the open document with another PDF and marks the differences (navigate with F3 / Shift+F3).</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task CompareAsync()
    {
        if (_session is null) return;
        var path = await _dialogs.OpenPdfAsync();
        if (path is null) return;

        DocumentComparison? result = null;
        await RunBusyAsync(_loc["Compare.Running"], async ct =>
        {
            var current = await _pageService.BuildFromSessionAsync(_session, ct);
            var other = await ReadAndDecryptAsync(path, ct);
            if (other is null) return;
            result = await _compare.CompareAsync(current, other.Value.Bytes, ct);
        });
        if (result is null) return;
        if (result.IsIdentical) { StatusMessage = _loc["Compare.Identical"]; return; }

        ShowMarks(result.Marks, _loc.Format("Compare.Summary", result.Pages.Count));
        if (ShowReportAsync is null) return;
        var report = new StringBuilder(_loc.Format("Compare.ReportHeader", Path.GetFileName(path))).AppendLine().AppendLine();
        foreach (var page in result.Pages)
        {
            report.AppendLine(_loc.Format("Compare.Page", page.PageIndex + 1) + (page.InOriginal ? "" : " " + _loc["Compare.OnlyRevised"])
                                                                              + (page.InRevised ? "" : " " + _loc["Compare.OnlyOriginal"]));
            if (page.Removed.Length > 0) report.Append("  − ").AppendLine(page.Removed);
            if (page.Added.Length > 0) report.Append("  + ").AppendLine(page.Added);
            report.AppendLine();
        }
        await ShowReportAsync(_loc["Compare.Title"], report.ToString());
    }

    /// <summary>Finds identifiers (tax codes, IBANs, e-mails…) or a custom pattern and, after a preview, redacts them.</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task RedactPatternsAsync()
    {
        if (_session is null || PromptRedactionPatternsAsync is null || await PromptRedactionPatternsAsync() is not { Count: > 0 } patterns) return;

        // Preview on the document as shown, then redact each source (whose own coordinates the redaction needs).
        var preview = new List<TextSearchResult>();
        await RunBusyAsync(_loc["Redact.Searching"], async ct =>
        {
            var current = await _pageService.BuildFromSessionAsync(_session, ct);
            foreach (var p in patterns) preview.AddRange(await _textExtractor.SearchPatternAsync(current, p, ct));
        });
        if (preview.Count == 0) { StatusMessage = _loc["Redact.NoneFound"]; return; }
        ShowMarks(preview.OrderBy(r => r.PageIndex).ToList(), _loc.Format("Redact.Found", preview.Count));
        if (ChooseAsync is null || await ChooseAsync(
                _loc.Format("Redact.Confirm", preview.Count, preview.Select(r => r.PageIndex).Distinct().Count()),
                [_loc["Dialog.Cancel"], _loc["Redact.Apply"]]) != 1) return;

        await EditAllSourcesAsync(_loc["Redact.Applying"], async (_, src, ct) =>
        {
            var regions = new List<(int, PdfRect)>();
            foreach (var p in patterns)
                foreach (var hit in await _textExtractor.SearchPatternAsync(src, p, ct))
                    regions.AddRange(hit.Boxes.Select(b => (hit.PageIndex, Inflate(b))));
            return regions.Count == 0 ? src : await _redaction.RedactAsync(src, regions, 300, ct);
        });
        InvalidateSearch();
        StatusMessage = _loc.Format("Redact.Done", preview.Count);

        static PdfRect Inflate(PdfRect r) => new(Math.Max(0, r.Left - 0.002), Math.Max(0, r.Top - 0.002), Math.Min(1, r.Right + 0.002), Math.Min(1, r.Bottom + 0.002));
    }
}
