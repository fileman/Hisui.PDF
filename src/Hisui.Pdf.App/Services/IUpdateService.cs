namespace Hisui.Pdf.App.Services;

/// <summary>A published release that is newer than the running build.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Url, string? Notes);

public interface IUpdateService
{
    /// <summary>The running build's version.</summary>
    Version CurrentVersion { get; }

    /// <summary>Returns the latest release if it is newer than the running build; null if up to date or the check failed.</summary>
    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default);
}
