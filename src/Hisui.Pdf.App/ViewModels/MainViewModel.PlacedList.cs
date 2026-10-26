using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.ViewModels;

/// <summary>One row of the "annotations on this page" list of the Annotate panel.</summary>
public sealed record PlacedItem(PlacedAnnotation Placed, PageItemViewModel Page, string Kind, string Summary)
{
    // Four glyphs toggled by IsVisible in the item template.
    public bool IsFreeText => Placed.Tool == AnnotationTool.FreeText;
    public bool IsHighlight => Placed.Tool == AnnotationTool.Highlight;
    public bool IsRectangle => Placed.Tool == AnnotationTool.Rectangle;
    public bool IsImage => Placed.Tool == AnnotationTool.ImageOverlay;
}

public partial class MainViewModel
{
    /// <summary>The selected page's placed annotations (Annotate panel list), in z-order.</summary>
    public ObservableCollection<PlacedItem> PlacedList { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditSelectedPlacedCommand), nameof(DeleteSelectedPlacedCommand))]
    private PlacedItem? _selectedPlaced;

    /// <summary>Id of the selected annotation, shared by the list and the page layers: each side writes it on a user
    /// selection and follows its changes without writing back.</summary>
    [ObservableProperty] private string? _selectedPlacedId;

    private PageItemViewModel? _placedWatched;
    private bool _syncingPlaced;

    partial void OnSelectedPlacedChanged(PlacedItem? value)
    {
        if (!_syncingPlaced) SelectedPlacedId = value?.Placed.Id; // the list drives the layers
    }

    partial void OnSelectedPlacedIdChanged(string? value)
    {
        var item = value is null ? null : PlacedList.FirstOrDefault(i => i.Placed.Id == value);
        if (ReferenceEquals(item, SelectedPlaced)) return;
        _syncingPlaced = true; // a layer drives the list: do not echo the id back
        try { SelectedPlaced = item; }
        finally { _syncingPlaced = false; }
    }

    // The two-argument overload: the one-argument partial is taken by MainViewModel.cs.
    partial void OnSelectedPageChanged(PageItemViewModel? oldValue, PageItemViewModel? newValue)
    {
        if (_placedWatched is not null) _placedWatched.PropertyChanged -= OnWatchedPageChanged;
        _placedWatched = newValue;
        if (newValue is not null) newValue.PropertyChanged += OnWatchedPageChanged;
        RefreshPlacedList();
    }

    private void OnWatchedPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageItemViewModel.Placed)) RefreshPlacedList();
    }

    /// <summary>Brings <see cref="PlacedList"/> in line with the selected page, touching it only when something changed.</summary>
    private void RefreshPlacedList()
    {
        var page = SelectedPage;
        var placed = page?.Placed ?? [];
        if (placed.SequenceEqual(PlacedList.Select(i => i.Placed)) && PlacedList.All(i => ReferenceEquals(i.Page, page))) return;

        _syncingPlaced = true; // clearing the list makes the ListBox null the selection: not a user choice
        try
        {
            PlacedList.Clear();
            foreach (var a in placed) PlacedList.Add(new PlacedItem(a, page!, KindOf(a), SummaryOf(a)));
            SelectedPlaced = SelectedPlacedId is { } id ? PlacedList.FirstOrDefault(i => i.Placed.Id == id) : null;
        }
        finally { _syncingPlaced = false; }
    }

    private string KindOf(PlacedAnnotation a) => _loc[a.Tool switch
    {
        AnnotationTool.FreeText => "Tool.FreeText",
        AnnotationTool.Highlight => "Tool.Highlight",
        AnnotationTool.Rectangle => "Tool.Rectangle",
        _ => "Tool.ImageOverlay",
    }];

    private static string SummaryOf(PlacedAnnotation a)
    {
        if (a.Tool == AnnotationTool.FreeText)
        {
            var line = a.Text.Split('\n', 2)[0].Trim();
            return line.Length > 40 ? line[..40] + "…" : line;
        }
        return $"{a.Rect.Width:P0} × {a.Rect.Height:P0}"; // share of the page
    }

    private bool HasSelectedPlaced => SelectedPlaced is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedPlaced))]
    private Task EditSelectedPlacedAsync() =>
        SelectedPlaced is { } i ? BeginEditPlacedAsync(i.Page, i.Placed) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSelectedPlaced))]
    private Task DeleteSelectedPlacedAsync() =>
        SelectedPlaced is { } i ? DeletePlacedAsync(i.Page, i.Placed) : Task.CompletedTask;
}
