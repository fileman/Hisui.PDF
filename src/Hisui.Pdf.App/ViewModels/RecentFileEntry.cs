using System.IO;

namespace Hisui.Pdf.App.ViewModels;

public sealed record RecentFileEntry(string FullPath, bool IsPinned = false)
{
    public string DisplayName => Path.GetFileName(FullPath);
    public string Folder => Path.GetDirectoryName(FullPath) ?? "";
}
