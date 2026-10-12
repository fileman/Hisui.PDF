using System.IO;
using CommunityToolkit.Mvvm.Input;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.ViewModels;

/// <summary>Save as a PDF version or standard (PDF/A, PDF/UA, PDF/X) after a preflight.</summary>
public partial class MainViewModel
{
    /// <summary>Set by the view: profile picker with live preflight (initial profile, title, check) → choice or null.</summary>
    public Func<PdfProfile, string?, Func<PdfProfile, Task<ConformanceReport>>, Task<(PdfProfile, ConversionOptions)?>>? PromptProfileAsync { get; set; }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private Task SaveAsProfileAsync() => ConvertToProfileAsync(PdfProfile.PdfA2b);

    /// <summary>Accessibility check: the same dialog on PDF/UA-1.</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private Task CheckAccessibilityAsync() => ConvertToProfileAsync(PdfProfile.PdfUA1);

    private async Task ConvertToProfileAsync(PdfProfile initial)
    {
        if (_session is null || PromptProfileAsync is null) return;
        await FlushFieldEditAsync();
        byte[]? current = null;
        await RunBusyAsync(_loc["Status.Preparing"], async ct => current = await _pageService.BuildFromSessionAsync(_session, ct));
        if (current is null) return;

        var title = _currentPath is null ? null : Path.GetFileNameWithoutExtension(_currentPath);
        if (await PromptProfileAsync(initial, title, p => _conformance.CheckAsync(current, p)) is not { } choice) return;
        var (profile, options) = choice;

        var suffix = profile.DisplayName().Replace("/", "").Replace(" ", "").Replace(".", "");
        var path = await _dialogs.SavePdfAsync($"{title ?? "documento"}-{suffix}.pdf");
        if (path is null) return;

        ConformanceReport? remaining = null;
        await RunBusyAsync(_loc.Format("Conf.Converting", profile.DisplayName()), async ct =>
        {
            var (pdf, report) = await _conformance.ConvertAsync(current, profile, options, ct);
            await File.WriteAllBytesAsync(path, pdf, ct);
            remaining = report;
            StatusMessage = _loc.Format(report.Passes ? "Conf.SavedPasses" : "Conf.SavedWithIssues", Path.GetFileName(path), profile.DisplayName());
        });
        if (remaining is { Passes: false } && ShowReportAsync is not null)
            await ShowReportAsync(_loc["Conf.Remaining"], Views.ConformanceDialog.Format(remaining));
    }
}
