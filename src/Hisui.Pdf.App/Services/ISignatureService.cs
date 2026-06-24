namespace Hisui.Pdf.App.Services;

/// <summary>Manages the reusable signature library: a set of PNG images persisted under
/// <see cref="AppDataPaths.SignaturesDir"/> so they survive across documents and sessions.</summary>
public interface ISignatureService
{
    /// <summary>Saved signatures, most-recent first.</summary>
    IReadOnlyList<SavedSignature> Signatures { get; }

    /// <summary>Persists <paramref name="pngBytes"/> as a new signature and returns its entry, or null
    /// if writing the image failed (best-effort, like the rest of the service).</summary>
    SavedSignature? Add(byte[] pngBytes, string name);

    /// <summary>Deletes the signature (manifest entry + PNG file). No-op if the id is unknown.</summary>
    void Remove(string id);

    /// <summary>Reads the PNG bytes for a signature, or null if the file is missing/unreadable.</summary>
    byte[]? LoadImage(SavedSignature signature);
}
