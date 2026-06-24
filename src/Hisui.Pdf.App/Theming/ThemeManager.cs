using Avalonia;
using Avalonia.Styling;

namespace Hisui.Pdf.App.Theming;

/// <summary>
/// Maps a persisted theme code ("System" / "Light" / "Dark") to an Avalonia
/// <see cref="ThemeVariant"/> and applies it to the running application. "System" leaves the variant
/// at <see cref="ThemeVariant.Default"/> so Avalonia follows the OS light/dark setting.
/// </summary>
public static class ThemeManager
{
    public const string DefaultTheme = "System";

    public static IReadOnlyList<ThemeOption> AvailableThemes { get; } =
    [
        new("System", "Theme.System"),
        new("Light", "Theme.Light"),
        new("Dark", "Theme.Dark"),
    ];

    /// <summary>Clamps an unknown/tampered code back to <see cref="DefaultTheme"/> and returns the
    /// canonical casing of a supported code.</summary>
    public static string Normalize(string? code) =>
        AvailableThemes.FirstOrDefault(
            t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase))?.Code
        ?? DefaultTheme;

    /// <summary>Applies the theme to the running application. No-op before the app is initialized.</summary>
    public static void Apply(string? code)
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = Normalize(code) switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
