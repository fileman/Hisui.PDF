namespace Hisui.Pdf.App.Services;

/// <summary>Metadata for one reusable signature in the library. The image itself lives next to the
/// manifest as <see cref="FileName"/> (a PNG with transparency) under
/// <see cref="AppDataPaths.SignaturesDir"/>.</summary>
public sealed class SavedSignature
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
}
