using CommunityToolkit.Mvvm.Input;

namespace Hisui.Pdf.App.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// Finds the blanks, tick boxes and empty table cells of the open document and turns them into fillable
    /// form fields. One undo step for the whole document.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task DetectFormFieldsAsync()
    {
        if (_session is null) return;

        await RunBusyAsync(_loc["Status.DetectingFields"], async ct =>
        {
            // Fields live in the source documents, so detect per source (a merged document has several).
            var updates = new List<(int Id, byte[] Bytes)>();
            var total = 0;
            foreach (var id in _session.Pages.Select(p => p.SourceDocumentId).Distinct().ToList())
            {
                ct.ThrowIfCancellationRequested();
                var source = _session.GetSource(id);
                var specs = await _formDetector.DetectFieldsAsync(source, ct);
                if (specs.Count == 0) continue;
                updates.Add((id, await _forms.AddFieldsAsync(source, specs, ct)));
                total += specs.Count;
            }

            if (updates.Count == 0)
            {
                StatusMessage = _loc["Status.NoFieldsFound"];
                return;
            }

            using (_session.BeginTransaction())
                foreach (var (id, bytes) in updates)
                    _session.UpdateSource(id, bytes);

            foreach (var (id, _) in updates) InvalidateSourceCache(id);
            foreach (var page in Pages) page.Thumbnail = null;
            await RenderThumbnailsAsync(ct);
            await UpdatePreviewAsync(SelectedPage);
            RaiseCanExecute(); // the new fields just became undoable
            StatusMessage = _loc.Format("Status.FieldsCreated", total);
        });
    }
}
