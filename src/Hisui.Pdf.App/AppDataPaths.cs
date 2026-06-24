using System.IO;

namespace Hisui.Pdf.App;

internal static class AppDataPaths
{
    public static readonly string BaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Hisui.PDF");

    public static readonly string SettingsFile = Path.Combine(BaseDir, "settings.json");
    public static readonly string LogFile      = Path.Combine(BaseDir, "hisui.log");

    /// <summary>Folder holding the reusable signature library (PNG files + manifest).</summary>
    public static readonly string SignaturesDir      = Path.Combine(BaseDir, "signatures");
    public static readonly string SignaturesManifest = Path.Combine(SignaturesDir, "signatures.json");
}
