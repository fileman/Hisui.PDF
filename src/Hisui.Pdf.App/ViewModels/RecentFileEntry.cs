using System.IO;

namespace Hisui.Pdf.App.ViewModels;

public sealed record RecentFileEntry(string FullPath)
{
    public string DisplayName => Path.GetFileName(FullPath);
}
