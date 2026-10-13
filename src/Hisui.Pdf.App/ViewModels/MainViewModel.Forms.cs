using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hisui.Pdf.Core.Model;

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

    // ── Fill and lay out existing fields ──────────────────────────────────────

    /// <summary>Forms panel open with no creation tool: fields are laid out (move/resize/align/delete) instead of filled.</summary>
    public bool IsFormLayoutMode => IsToolPanelOpen && RibbonTab == RibbonTab.Forms && !IsAnnotationActive;

    /// <summary>Layout mode shows each field's position in the tab order.</summary>
    [ObservableProperty] private bool _showTabOrder;

    partial void OnIsToolPanelOpenChanged(bool value) => RaiseFormLayoutModeChanged();

    private void RaiseFormLayoutModeChanged()
    {
        OnPropertyChanged(nameof(IsFormLayoutMode));
        OnPropertyChanged(nameof(IsCommentsPanel));
        OnPropertyChanged(nameof(IsBookmarksPanel));
        _ = RefreshCommentsAsync();
        _ = RefreshBookmarksAsync();
        if (!IsFormLayoutMode) ClearFieldSelection();
    }

    /// <summary>Set by the view: edits field properties in a dialog for the given number of selected fields
    /// (null = cancelled).</summary>
    public Func<FormFieldProperties, int, Task<FormFieldProperties?>>? ShowFieldPropertiesDialog { get; set; }

    /// <summary>Copies onto <paramref name="target"/> only what changed from <paramref name="before"/> to
    /// <paramref name="after"/>; the name stays the target's own (field names are unique).</summary>
    internal static FormFieldProperties ApplyChanges(FormFieldProperties before, FormFieldProperties after, FormFieldProperties target)
    {
        T Pick<T>(Func<FormFieldProperties, T> get) => EqualityComparer<T>.Default.Equals(get(before), get(after)) ? get(target) : get(after);
        return target with
        {
            Tooltip = Pick(p => p.Tooltip),
            Required = Pick(p => p.Required),
            ReadOnly = Pick(p => p.ReadOnly),
            Multiline = Pick(p => p.Multiline),
            MaxLength = Pick(p => p.MaxLength),
            FontName = Pick(p => p.FontName),
            FontSize = Pick(p => p.FontSize),
            Alignment = Pick(p => p.Alignment),
            Format = Pick(p => p.Format),
            Decimals = Pick(p => p.Decimals),
            DatePattern = Pick(p => p.DatePattern),
            Options = before.Options.SequenceEqual(after.Options) ? target.Options : after.Options,
        };
    }

    /// <summary>Set by the view: a message with choices; returns the chosen index or -1.</summary>
    public Func<string, string[], Task<int>>? ChooseAsync { get; set; }

    // Field edits rewrite the source without the busy overlay (it would steal focus while typing); the gate keeps
    // them in order.
    private readonly SemaphoreSlim _fieldGate = new(1, 1);
    private (PageItemViewModel Page, PdfFormWidget Field, string Value)? _pendingFieldEdit;

    /// <summary>Loads <paramref name="item"/>'s widgets, keeping the current list (and live editors) when unchanged.</summary>
    // Widgets per source page, valid while the source bytes are the same array (continuous view asks on every scroll).
    private readonly Dictionary<(int Source, int Page), (byte[] Bytes, IReadOnlyList<PdfFormWidget> Widgets)> _fieldCache = [];

    private async Task LoadFieldsAsync(PageItemViewModel item)
    {
        if (_session is null) return;
        var key = (item.SourceDocumentId, item.SourcePageIndex);
        var bytes = _session.GetSource(item.SourceDocumentId);
        IReadOnlyList<PdfFormWidget> widgets;
        if (_fieldCache.TryGetValue(key, out var cached) && ReferenceEquals(cached.Bytes, bytes)) widgets = cached.Widgets;
        else
        {
            try { widgets = await _forms.ReadWidgetsAsync(bytes, item.SourcePageIndex); }
            catch (Exception) { widgets = []; } // unreadable form structure: the page stays view-only
            _fieldCache[key] = (bytes, widgets);
        }
        if (!widgets.SequenceEqual(item.Fields, FieldEquality.Instance)) item.Fields = widgets;
        await LoadLinksAsync(item);
    }

    /// <summary>Records typed text; written into the PDF by <see cref="FlushFieldEditAsync"/> (focus leaves, Enter,
    /// or before any document operation).</summary>
    public void SetPendingFieldEdit(PageItemViewModel page, PdfFormWidget field, string value) =>
        _pendingFieldEdit = (page, field, value);

    public async Task FlushFieldEditAsync()
    {
        if (_pendingFieldEdit is not { } edit) return;
        _pendingFieldEdit = null;
        await SetFieldValueAsync(edit.Page, edit.Field, edit.Value);
    }

    public Task SetFieldValueAsync(PageItemViewModel page, PdfFormWidget field, string value) =>
        (field.Value ?? "") == value
            ? Task.CompletedTask
            // Text keeps its editor (rebuilding would drop the caret); buttons rebuild so a radio group re-syncs.
            : EditFieldsAsync(page, src => _forms.FillFieldsAsync(src, new Dictionary<string, string> { [field.Name] = value }),
                rebuild: field.Type is PdfFormFieldType.CheckBox or PdfFormFieldType.RadioButton);

    /// <summary>Set by the view: picks a handwritten signature image from the library (null = cancelled).</summary>
    public Func<Task<byte[]?>>? PickSignatureImageAsync { get; set; }

    /// <summary>
    /// Signs an empty signature field. Handwritten: the image from the library is drawn in the field's box and the
    /// field, now filled, is removed (one undo step). Digital: a certificate (PAdES) signature fills the field itself.
    /// </summary>
    public async Task SignFieldAsync(PageItemViewModel page, PdfFormWidget field)
    {
        if (ChooseAsync is null) return;
        var how = await ChooseAsync(_loc["Sign.FieldHow"], [_loc["Dialog.Cancel"], _loc["Sign.Handwritten"], _loc["Sign.Digital"]]);
        if (how == 2)
        {
            await SignIntoFieldAsync(field.Name);
            return;
        }
        if (how != 1 || PickSignatureImageAsync is null || await PickSignatureImageAsync() is not { } image) return;
        await EditFieldsAsync(page, async src =>
        {
            var signed = await _annotations.AddImageOverlayAsync(src, page.SourcePageIndex, image, field.Rect);
            return await _forms.DeleteWidgetsAsync(signed, page.SourcePageIndex, [field.AnnotIndex]);
        }, rebuild: true);
    }

    public Task MoveFieldsAsync(PageItemViewModel page, IReadOnlyDictionary<int, PdfRect> rects) =>
        rects.Count == 0 ? Task.CompletedTask
            : EditFieldsAsync(page, src => _forms.SetWidgetRectsAsync(src, page.SourcePageIndex, rects), rebuild: true);

    private async Task EditFieldsAsync(PageItemViewModel page, Func<byte[], Task<byte[]>> edit, bool rebuild)
    {
        if (_session is null) return;
        await _fieldGate.WaitAsync();
        try
        {
            var id = page.SourceDocumentId;
            var updated = await edit(_session.GetSource(id));
            _session.UpdateSource(id, updated);
            // ponytail: only this page re-renders; a field repeated on other pages refreshes there on the next render.
            InvalidatePageCache(id, page.SourcePageIndex);

            var widgets = await _forms.ReadWidgetsAsync(updated, page.SourcePageIndex);
            _fieldCache[(id, page.SourcePageIndex)] = (updated, widgets);
            if (rebuild) page.Fields = widgets;
            else page.SetFieldsSilently(widgets);
            await RenderThumbnailsAsync();
            if (ReferenceEquals(page, SelectedPage)) await UpdatePreviewAsync(page);
            else EnsurePagePreview(page);
            RaiseCanExecute(); // the edit just became undoable
        }
        catch (Exception ex)
        {
            StatusMessage = _loc.Format("Status.Error", ex.Message);
        }
        finally
        {
            _fieldGate.Release();
        }
    }

    // ── Selection (layout mode) ───────────────────────────────────────────────

    /// <summary>Page of the selected fields (selection never spans pages).</summary>
    public PageItemViewModel? SelectionPage { get; private set; }

    /// <summary>/Annots indices of the selected widgets, in selection order.</summary>
    public IReadOnlyList<int> SelectedFields { get; private set; } = [];

    /// <summary>Alignment follows the last selected field instead of the first (remembered across sessions).</summary>
    [ObservableProperty] private bool _alignToLast;

    partial void OnAlignToLastChanged(bool value)
    {
        _settings.Settings.AlignToLastSelected = value;
        _settings.Save();
        OnPropertyChanged(nameof(AlignAnchor));
    }

    /// <summary>The field that alignment and sizing follow: the first or the last selected.</summary>
    public int? AlignAnchor => SelectedFields.Count == 0 ? null : AlignToLast ? SelectedFields[^1] : SelectedFields[0];

    public bool HasFieldSelection => SelectedFields.Count > 0;
    public bool HasMultiFieldSelection => SelectedFields.Count > 1;

    public void SelectField(PageItemViewModel page, int annotIndex, bool toggle)
    {
        var current = ReferenceEquals(page, SelectionPage) ? SelectedFields.ToList() : [];
        if (!toggle) current = [annotIndex];
        else if (!current.Remove(annotIndex)) current.Add(annotIndex);
        SetSelection(current.Count > 0 ? page : null, current);
    }

    /// <summary>Selects <paramref name="annotIndices"/> (in page order), replacing the selection or adding to it.</summary>
    public void SelectFields(PageItemViewModel page, IEnumerable<int> annotIndices, bool add)
    {
        var current = add && ReferenceEquals(page, SelectionPage) ? SelectedFields.ToList() : [];
        current.AddRange(annotIndices.Where(i => !current.Contains(i)));
        SetSelection(current.Count > 0 ? page : null, current);
    }

    public void SelectAllFields(PageItemViewModel page) => SelectFields(page, page.Fields.Select(f => f.AnnotIndex), add: false);

    /// <summary>Selects every field of the current page (layout mode).</summary>
    [RelayCommand]
    private void SelectAllPageFields()
    {
        if (IsFormLayoutMode && (SelectionPage ?? SelectedPage) is { } page) SelectAllFields(page);
    }

    public void ClearFieldSelection()
    {
        if (SelectedFields.Count > 0) SetSelection(null, []);
    }

    private void SetSelection(PageItemViewModel? page, IReadOnlyList<int> fields)
    {
        SelectionPage = page;
        SelectedFields = fields;
        OnPropertyChanged(nameof(SelectedFields));
        OnPropertyChanged(nameof(AlignAnchor));
        OnPropertyChanged(nameof(HasFieldSelection));
        OnPropertyChanged(nameof(HasMultiFieldSelection));
        AlignFieldsCommand.NotifyCanExecuteChanged();
        DeleteSelectedFieldsCommand.NotifyCanExecuteChanged();
        MoveTabOrderCommand.NotifyCanExecuteChanged();
        EditFieldPropertiesCommand.NotifyCanExecuteChanged();
    }

    private bool CanAlign(string? mode) => HasMultiFieldSelection;

    /// <summary>Aligns / sizes the selection to its anchor (<see cref="AlignAnchor"/>): left, right, top, bottom,
    /// hcenter, vcenter, width, height, size.</summary>
    [RelayCommand(CanExecute = nameof(CanAlign))]
    private async Task AlignFieldsAsync(string mode)
    {
        if (SelectionPage is not { } page || SelectedFields.Count < 2 || AlignAnchor is not { } anchor) return;
        var byIndex = page.Fields.ToDictionary(f => f.AnnotIndex);
        if (!byIndex.TryGetValue(anchor, out var anchorField)) return;
        var a = anchorField.Rect;
        var rects = new Dictionary<int, PdfRect>();
        foreach (var index in SelectedFields.Where(i => i != anchor))
        {
            if (!byIndex.TryGetValue(index, out var f)) continue;
            var r = f.Rect;
            rects[index] = mode switch
            {
                "left" => PdfRect.FromLTWH(a.Left, r.Top, r.Width, r.Height),
                "right" => PdfRect.FromLTWH(a.Right - r.Width, r.Top, r.Width, r.Height),
                "top" => PdfRect.FromLTWH(r.Left, a.Top, r.Width, r.Height),
                "bottom" => PdfRect.FromLTWH(r.Left, a.Bottom - r.Height, r.Width, r.Height),
                "hcenter" => PdfRect.FromLTWH((a.Left + a.Right - r.Width) / 2, r.Top, r.Width, r.Height),
                "vcenter" => PdfRect.FromLTWH(r.Left, (a.Top + a.Bottom - r.Height) / 2, r.Width, r.Height),
                "width" => PdfRect.FromLTWH(r.Left, r.Top, a.Width, r.Height),
                "height" => PdfRect.FromLTWH(r.Left, r.Top, r.Width, a.Height),
                _ => PdfRect.FromLTWH(r.Left, r.Top, a.Width, a.Height),
            };
        }
        await MoveFieldsAsync(page, rects);
    }

    [RelayCommand(CanExecute = nameof(HasFieldSelection))]
    private async Task DeleteSelectedFieldsAsync()
    {
        if (SelectionPage is not { } page || SelectedFields.Count == 0) return;
        var indices = SelectedFields.ToList();
        ClearFieldSelection();
        await EditFieldsAsync(page, src => _forms.DeleteWidgetsAsync(src, page.SourcePageIndex, indices), rebuild: true);
    }

    private bool CanMoveTabOrder(string? direction) => SelectedFields.Count == 1;

    /// <summary>Moves the selected field one step earlier ("up") or later ("down") in the page's tab order.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveTabOrder))]
    private async Task MoveTabOrderAsync(string direction)
    {
        if (SelectionPage is not { } page || SelectedFields.Count != 1) return;
        var order = page.Fields.Select(f => f.AnnotIndex).ToList();
        var at = order.IndexOf(SelectedFields[0]);
        var to = at + (direction == "up" ? -1 : 1);
        if (at < 0 || to < 0 || to >= order.Count) return;
        (order[at], order[to]) = (order[to], order[at]);
        await ReorderAsync(page, order);
        // Widgets fill the same /Annots slots in the new order, so the moved field now sits in slot number "to".
        if (to < page.Fields.Count) SelectField(page, page.Fields[to].AnnotIndex, toggle: false);
    }

    /// <summary>Sets the tab order of the current page to reading order: rows top to bottom, left to right.</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task SortTabOrderAsync()
    {
        if ((SelectionPage ?? SelectedPage) is not { Fields.Count: > 1 } page) return;
        var rowTolerance = page.Fields.Min(f => f.Rect.Height) / 2;
        var order = page.Fields
            .OrderBy(f => Math.Round(f.Rect.Top / Math.Max(rowTolerance, 0.002)))
            .ThenBy(f => f.Rect.Left)
            .Select(f => f.AnnotIndex).ToList();
        ClearFieldSelection();
        await ReorderAsync(page, order);
        ShowTabOrder = true;
    }

    private Task ReorderAsync(PageItemViewModel page, List<int> order) =>
        EditFieldsAsync(page, src => _forms.ReorderWidgetsAsync(src, page.SourcePageIndex, order), rebuild: true);

    /// <summary>Opens the properties of the first selected field (double-click or the panel button). With several
    /// fields selected, what the user changes is applied to all of them — one undo step.</summary>
    [RelayCommand(CanExecute = nameof(HasFieldSelection))]
    private async Task EditFieldPropertiesAsync() => await EditFieldPropertiesCoreAsync();

    private async Task EditFieldPropertiesCoreAsync()
    {
        if (_session is null || SelectionPage is not { } page || SelectedFields.Count == 0 || ShowFieldPropertiesDialog is null) return;
        var index = SelectedFields[0];
        var indices = SelectedFields.ToList();
        try
        {
            var props = await _forms.ReadPropertiesAsync(_session.GetSource(page.SourceDocumentId), page.SourcePageIndex, index);
            if (await ShowFieldPropertiesDialog(props, indices.Count) is not { } edited || edited == props) return;
            if (indices.Count == 1)
            {
                await EditFieldsAsync(page, src => _forms.SetPropertiesAsync(src, page.SourcePageIndex, index, edited), rebuild: true);
                return;
            }
            await EditFieldsAsync(page, async src =>
            {
                foreach (var i in indices)
                {
                    var target = await _forms.ReadPropertiesAsync(src, page.SourcePageIndex, i);
                    src = await _forms.SetPropertiesAsync(src, page.SourcePageIndex, i, ApplyChanges(props, edited, target));
                }
                return src;
            }, rebuild: true);
        }
        catch (Exception ex)
        {
            StatusMessage = _loc.Format("Status.Error", ex.Message);
        }
    }

    // ── Form data (whole document) ────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task ExportFormDataAsync()
    {
        if (_session is null || ChooseAsync is null) return;
        var choice = await ChooseAsync(_loc["FormData.ExportTitle"], ["XFDF", "FDF", "CSV"]);
        if (choice < 0) return;
        var (format, ext) = choice switch { 0 => (FormDataFormat.Xfdf, ".xfdf"), 1 => (FormDataFormat.Fdf, ".fdf"), _ => (FormDataFormat.Csv, ".csv") };
        var baseName = _currentPath is null ? "modulo" : Path.GetFileNameWithoutExtension(_currentPath);
        var path = await _dialogs.SaveFileAsync(baseName + ext, ext, _loc["FormData.FileType"]);
        if (path is null) return;

        await RunBusyAsync(_loc["Status.Saving"], async ct =>
        {
            var current = await _pageService.BuildFromSessionAsync(_session, ct);
            var data = await _forms.ExportDataAsync(current, format, ct);
            await File.WriteAllBytesAsync(path, data, ct);
            StatusMessage = _loc.Format("Status.Saved", Path.GetFileName(path));
        });
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task ImportFormDataAsync()
    {
        if (_session is null) return;
        var path = await _dialogs.OpenFileAsync(_loc["FormData.FileType"], "*.xfdf", "*.fdf", "*.csv", "*.txt");
        if (path is null) return;
        var format = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".xfdf" => FormDataFormat.Xfdf, ".fdf" => FormDataFormat.Fdf, _ => FormDataFormat.Csv,
        };
        var data = await File.ReadAllBytesAsync(path);
        await EditAllSourcesAsync(_loc["FormData.Importing"], (_, src, ct) => _forms.ImportDataAsync(src, data, format, ct));
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task ResetFormAsync()
    {
        if (ChooseAsync is not null && await ChooseAsync(_loc["FormData.ResetConfirm"], [_loc["Dialog.Cancel"], _loc["FormData.Reset"]]) != 1) return;
        await EditAllSourcesAsync(_loc["FormData.Resetting"], (_, src, ct) => _forms.ResetFieldsAsync(src, null, ct));
    }

    /// <summary>Burns field values into the pages: the selected fields, or (after confirmation) all of them.</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task FlattenFieldsAsync()
    {
        if (SelectionPage is { } page && SelectedFields.Count > 0)
        {
            var names = page.Fields.Where(f => SelectedFields.Contains(f.AnnotIndex)).Select(f => f.Name).ToHashSet();
            ClearFieldSelection();
            await EditFieldsAsync(page, src => _forms.FlattenFieldsAsync(src, names), rebuild: true);
            return;
        }
        if (ChooseAsync is not null && await ChooseAsync(_loc["FormData.FlattenConfirm"], [_loc["Dialog.Cancel"], _loc["FormData.Flatten"]]) != 1) return;
        await EditAllSourcesAsync(_loc["FormData.Flattening"], (_, src, ct) => _forms.FlattenFieldsAsync(src, null, ct));
    }

    /// <summary>Applies <paramref name="edit"/> to every source document as one undo step and refreshes everything.</summary>
    private async Task EditAllSourcesAsync(string status, Func<int, byte[], CancellationToken, Task<byte[]>> edit)
    {
        if (_session is null) return;
        await RunBusyAsync(status, async ct =>
        {
            var updates = new List<(int Id, byte[] Bytes)>();
            foreach (var id in _session.Pages.Select(p => p.SourceDocumentId).Distinct().ToList())
            {
                var source = _session.GetSource(id);
                var updated = await edit(id, source, ct);
                if (!ReferenceEquals(updated, source)) updates.Add((id, updated));
            }
            if (updates.Count == 0) return;
            using (_session.BeginTransaction())
                foreach (var (id, bytes) in updates) _session.UpdateSource(id, bytes);
            foreach (var (id, _) in updates) InvalidateSourceCache(id);
            foreach (var page in Pages) { page.Thumbnail = null; page.Fields = []; }
            await RenderThumbnailsAsync(ct);
            await UpdatePreviewAsync(SelectedPage);
            RaiseCanExecute();
        });
    }

    /// <summary>Widget identity + value (the record would compare <see cref="PdfFormWidget.Options"/> by reference).</summary>
    private sealed class FieldEquality : IEqualityComparer<PdfFormWidget>
    {
        public static readonly FieldEquality Instance = new();
        public bool Equals(PdfFormWidget? a, PdfFormWidget? b) =>
            a is not null && b is not null && a.Name == b.Name && a.AnnotIndex == b.AnnotIndex && a.Rect == b.Rect &&
            a.Type == b.Type && a.Value == b.Value && a.IsRequired == b.IsRequired && a.IsReadOnly == b.IsReadOnly &&
            a.Format == b.Format && a.MaxLength == b.MaxLength && a.IsMultiline == b.IsMultiline && a.Alignment == b.Alignment &&
            a.Options.SequenceEqual(b.Options);
        public int GetHashCode(PdfFormWidget w) => HashCode.Combine(w.Name, w.AnnotIndex);
    }
}
