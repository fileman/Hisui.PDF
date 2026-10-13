using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Hisui.Pdf.App.ViewModels;

public partial class MainViewModel
{
    private const int RecentPageSize = 8;

    /// <summary>Pinned files (in pin order) followed by the most recent ones: the whole list.</summary>
    public IReadOnlyList<RecentFileEntry> AllRecentFiles { get; private set; } = [];

    /// <summary>The page of <see cref="AllRecentFiles"/> shown in the empty state.</summary>
    public ObservableCollection<RecentFileEntry> RecentFiles { get; } = [];

    [ObservableProperty] private int _recentPage;

    public int RecentPageCount => Math.Max(1, (AllRecentFiles.Count + RecentPageSize - 1) / RecentPageSize);
    public string RecentPageLabel => $"{RecentPage + 1} / {RecentPageCount}";
    public bool HasRecentFiles => AllRecentFiles.Count > 0;
    public bool HasRecentPages => RecentPageCount > 1;
    public bool HasUnpinnedRecentFiles => AllRecentFiles.Any(r => !r.IsPinned);

    partial void OnRecentPageChanged(int value) => ShowRecentPage();

    /// <summary>Re-reads the list from the settings, which every tab and window shares (called when a view shows it).</summary>
    public void RefreshRecentFiles()
    {
        var settings = _settings.Settings;
        AllRecentFiles = settings.PinnedFiles.Select(p => new RecentFileEntry(p, IsPinned: true))
            .Concat(settings.RecentFiles.Where(p => !settings.PinnedFiles.Contains(p)).Select(p => new RecentFileEntry(p)))
            .ToList();
        OnPropertyChanged(nameof(AllRecentFiles));
        OnPropertyChanged(nameof(RecentPageCount));
        OnPropertyChanged(nameof(HasRecentFiles));
        OnPropertyChanged(nameof(HasRecentPages));
        OnPropertyChanged(nameof(HasUnpinnedRecentFiles));

        var page = Math.Clamp(RecentPage, 0, RecentPageCount - 1);
        if (page != RecentPage) RecentPage = page; // shows that page
        else ShowRecentPage();
    }

    private void ShowRecentPage()
    {
        RecentFiles.Clear();
        foreach (var entry in AllRecentFiles.Skip(RecentPage * RecentPageSize).Take(RecentPageSize))
            RecentFiles.Add(entry);
        OnPropertyChanged(nameof(RecentPageLabel));
        PreviousRecentPageCommand.NotifyCanExecuteChanged();
        NextRecentPageCommand.NotifyCanExecuteChanged();
    }

    private bool CanPreviousRecentPage() => RecentPage > 0;
    private bool CanNextRecentPage() => RecentPage < RecentPageCount - 1;

    [RelayCommand(CanExecute = nameof(CanPreviousRecentPage))]
    private void PreviousRecentPage() => RecentPage--;

    [RelayCommand(CanExecute = nameof(CanNextRecentPage))]
    private void NextRecentPage() => RecentPage++;

    /// <summary>Pins a file to the top of the list (like a favourite), or unpins it back among the recent ones.</summary>
    [RelayCommand]
    private void TogglePinRecent(string path)
    {
        _settings.SetPinned(path, !_settings.Settings.PinnedFiles.Contains(path));
        RefreshRecentFiles();
    }

    [RelayCommand]
    private void RemoveRecent(string path)
    {
        _settings.RemoveRecentFile(path);
        RefreshRecentFiles();
    }

    /// <summary>Empties the recent list after confirmation; pinned files stay.</summary>
    [RelayCommand]
    private async Task ClearRecentAsync()
    {
        if (ChooseAsync is not null && await ChooseAsync(_loc["Recent.ClearConfirm"], [_loc["Dialog.Cancel"], _loc["Recent.Clear"]]) != 1) return;
        _settings.ClearRecentFiles();
        RecentPage = 0;
        RefreshRecentFiles();
    }

    [RelayCommand]
    private async Task OpenRecentAsync(string path)
    {
        if (!File.Exists(path))
        {
            // A pinned file stays (its drive may just be offline); a recent one that is gone leaves the list.
            if (!_settings.Settings.PinnedFiles.Contains(path)) _settings.RemoveRecentFile(path);
            RefreshRecentFiles();
            StatusMessage = _loc["Status.FileNotFound"];
            return;
        }
        await OpenPathAsync(path);
    }
}
