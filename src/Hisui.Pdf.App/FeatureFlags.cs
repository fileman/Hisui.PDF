namespace Hisui.Pdf.App;

/// <summary>Features built but not offered yet.</summary>
public static class FeatureFlags
{
    /// <summary>
    /// PAdES signing (tool + invisible signature). Hidden until signing uses an incremental update: today the file is
    /// rewritten, which invalidates signatures already present. Verification stays available.
    /// </summary>
    public static bool DigitalSigning => false;
}
