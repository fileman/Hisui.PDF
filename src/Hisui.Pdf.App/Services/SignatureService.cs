using System.IO;
using System.Text.Json;

namespace Hisui.Pdf.App.Services;

/// <summary>
/// File-backed signature library. The manifest (<see cref="AppDataPaths.SignaturesManifest"/>) lists
/// the signatures; each image is a sibling PNG named by its id. All I/O is best-effort, mirroring
/// <see cref="SettingsService"/>: failures degrade to an empty/unchanged library rather than throwing.
/// </summary>
internal sealed class SignatureService : ISignatureService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly List<SavedSignature> _signatures = Load();

    public IReadOnlyList<SavedSignature> Signatures => _signatures;

    public SavedSignature? Add(byte[] pngBytes, string name)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);

        var id = Guid.NewGuid().ToString("N");
        var entry = new SavedSignature
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? id : name.Trim(),
            FileName = id + ".png",
            CreatedUtc = DateTime.UtcNow,
        };

        // Best-effort, consistent with the rest of the service: a write failure (disk full, ACL,
        // locked file) must not throw on the UI thread, and must not leave a phantom manifest entry
        // pointing at a missing PNG.
        try
        {
            Directory.CreateDirectory(AppDataPaths.SignaturesDir);
            File.WriteAllBytes(Path.Combine(AppDataPaths.SignaturesDir, entry.FileName), pngBytes);
        }
        catch
        {
            return null;
        }

        _signatures.Insert(0, entry); // most-recent first
        Save();
        return entry;
    }

    public void Remove(string id)
    {
        var entry = _signatures.FirstOrDefault(s => s.Id == id);
        if (entry is null) return;

        _signatures.Remove(entry);
        try
        {
            var path = Path.Combine(AppDataPaths.SignaturesDir, entry.FileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* best-effort: the manifest entry is gone regardless */ }
        Save();
    }

    public byte[]? LoadImage(SavedSignature signature)
    {
        try
        {
            var path = Path.Combine(AppDataPaths.SignaturesDir, signature.FileName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch { return null; }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataPaths.SignaturesDir);
            File.WriteAllText(AppDataPaths.SignaturesManifest,
                JsonSerializer.Serialize(_signatures, JsonOptions));
        }
        catch { /* best-effort */ }
    }

    private static List<SavedSignature> Load()
    {
        try
        {
            if (!File.Exists(AppDataPaths.SignaturesManifest)) return [];
            return JsonSerializer.Deserialize<List<SavedSignature>>(
                File.ReadAllText(AppDataPaths.SignaturesManifest)) ?? [];
        }
        catch { return []; }
    }
}
