using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.ViewModels;

/// <summary>Certificate (PAdES) signing and signature verification.</summary>
public partial class MainViewModel
{
    /// <summary>Set by the view: certificate, reason, time-stamp… for a signature (null = cancelled). The bool asks for an invisible signature.</summary>
    public Func<bool, Task<SignRequest?>>? PromptSignatureAsync { get; set; }

    /// <summary>Digital signature tool: the dragged area becomes the visible signature; then sign, save, reopen.</summary>
    private async Task SignAreaAsync(PdfRect area) => await SignCoreAsync(area);

    /// <summary>Signs an empty signature field of the document, or invisibly when there is none (or the user picks that).</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private Task SignInvisibleAsync() => SignCoreAsync(null, chooseField: true);

    /// <summary>Certificate signature into a given empty signature field (from its "Sign here" box).</summary>
    private Task SignIntoFieldAsync(string fieldName) => SignCoreAsync(null, fieldName: fieldName);

    private async Task SignCoreAsync(PdfRect? area, bool chooseField = false, string? fieldName = null)
    {
        if (_session is null || SelectedPage is null || PromptSignatureAsync is null) return;
        await FlushFieldEditAsync();
        var pageIndex = Pages.IndexOf(SelectedPage);
        // The signature is appended to the file as opened, so the signatures already in it stay valid; edits made
        // since opening need the document rebuilt first, which invalidates them (warned below).
        var current = _session.UnchangedSingleSource();
        if (current is null)
            await RunBusyAsync(_loc["Status.Preparing"], async ct => current = await _pageService.BuildFromSessionAsync(_session, ct));
        if (current is null) return;

        var broken = (await _signatures.VerifyAsync(current)).Count(s => !s.IntegrityOk);
        if (broken > 0 && (ChooseAsync is null ||
            await ChooseAsync(_loc.Format("Sign.ExistingWarning", broken), [_loc["Dialog.Cancel"], _loc["Sign.Continue"]]) != 1)) return;

        var field = fieldName;
        if (chooseField && await _signatures.ListEmptySignatureFieldsAsync(current) is { Count: > 0 } empty)
        {
            if (ChooseAsync is null) return;
            var pick = await ChooseAsync(_loc["Sign.ChooseField"], [_loc["Dialog.Cancel"], .. empty, _loc["Sign.Invisible"]]);
            if (pick <= 0) return;
            if (pick <= empty.Count) field = empty[pick - 1];
        }

        if (await PromptSignatureAsync(area is null && field is null) is not { } request) return;
        // The tool's area is relative to the page as shown; the built document has the session rotation baked in.
        request = request with { PageIndex = pageIndex, Rect = area?.RotateClockwise(PageTurns), FieldName = field };

        var name = _currentPath is null ? "documento" : Path.GetFileNameWithoutExtension(_currentPath);
        var path = await _dialogs.SavePdfAsync(name + "-firmato.pdf");
        if (path is null) return;

        var ok = false;
        await RunBusyAsync(_loc["Sign.Signing"], async ct =>
        {
            var signed = await _signatures.SignAsync(current, request, ct);
            await File.WriteAllBytesAsync(path, signed, ct);
            ok = true;
        });
        if (!ok) return;
        await OpenPathAsync(path); // continue on the signed file: further edits would have to be signed again
        StatusMessage = _loc.Format("Sign.Done", Path.GetFileName(path));
    }

    /// <summary>Lists the document's signatures with integrity, coverage, certificate trust and time-stamp.</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task VerifySignaturesAsync()
    {
        if (_session is null) return;
        IReadOnlyList<SignatureInfo>? infos = null;
        await RunBusyAsync(_loc["Sign.Verifying"], async ct =>
        {
            // Verify the file as opened: rebuilding would rewrite it and break every signature.
            var bytes = _session.UnchangedSingleSource() ?? await _pageService.BuildFromSessionAsync(_session, ct);
            infos = await _signatures.VerifyAsync(bytes, ct);
        });
        if (infos is null) return;
        if (infos.Count == 0) { StatusMessage = _loc["Sign.None"]; return; }

        var sb = new StringBuilder();
        foreach (var s in infos)
        {
            var valid = s.IntegrityOk && s.CoversWholeDocument && s.CertificateTrusted;
            sb.AppendLine((valid ? "✔ " : s.IntegrityOk ? "⚠ " : "✖ ") + _loc.Format("Sign.Line", s.SignerName, s.FieldName));
            if (s.SigningTime is { } t) sb.AppendLine("   " + _loc.Format(s.HasTimestamp ? "Sign.TimeStamped" : "Sign.TimeDeclared", t.ToString("g")));
            if (s.Issuer is not null) sb.AppendLine("   " + _loc.Format("Sign.Issuer", s.Issuer, s.CertificateNotAfter?.ToString("d") ?? "?"));
            if (s.Reason is not null) sb.AppendLine("   " + _loc.Format("Sign.Reason", s.Reason));
            sb.AppendLine("   " + _loc[s.IntegrityOk ? "Sign.IntegrityOk" : "Sign.IntegrityBad"]);
            if (s.IntegrityOk) sb.AppendLine("   " + _loc[s.CoversWholeDocument ? "Sign.Covers" : "Sign.ChangedAfter"]);
            sb.AppendLine("   " + (s.CertificateTrusted ? _loc["Sign.Trusted"] : _loc.Format("Sign.Untrusted", s.Problem ?? "")));
            sb.AppendLine("   " + _loc.Format("Sign.Format", s.SubFilter == "ETSI.CAdES.detached" ? "PAdES" : s.SubFilter));
            sb.AppendLine();
        }
        sb.AppendLine(_loc["Sign.VerifyNote"]);
        if (ShowReportAsync is not null) await ShowReportAsync(_loc["Sign.ReportTitle"], sb.ToString());
    }
}
