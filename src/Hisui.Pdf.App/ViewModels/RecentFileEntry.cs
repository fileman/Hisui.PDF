using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Hisui.Pdf.App.ViewModels;

/// <summary>A row of the recent-files list: name, folder, when it was last opened, size and a first-page thumbnail.</summary>
public sealed partial class RecentFileEntry(string fullPath, bool isPinned = false, DateTime? opened = null, string openedText = "", string missingText = "")
    : ObservableObject
{
    private readonly FileInfo _info = new(fullPath);

    public string FullPath { get; } = fullPath;
    public bool IsPinned { get; } = isPinned;
    public DateTime? Opened { get; } = opened;
    /// <summary>"Today, 17:46", "Yesterday, 22:54" or the date; empty when unknown.</summary>
    public string OpenedText { get; } = openedText;
    public string DisplayName => Path.GetFileName(FullPath);
    public string Folder => Path.GetDirectoryName(FullPath) ?? "";
    public bool Exists => _info.Exists;
    public DateTime LastWriteTime => _info.LastWriteTime;
    /// <summary>Size, or <c>missingText</c> ("Not found") when the file is gone.</summary>
    public string SizeText => Exists ? FormatSize(_info.Length) : missingText;

    /// <summary>First page, small; null until rendered (or when the file cannot be rendered): the view shows an icon.</summary>
    [ObservableProperty] private Bitmap? _thumbnail;

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };
}
