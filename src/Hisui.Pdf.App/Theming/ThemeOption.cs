namespace Hisui.Pdf.App.Theming;

/// <summary>A selectable UI theme: its persisted <paramref name="Code"/> ("System" / "Light" /
/// "Dark") and the localization key for the label shown in the theme menu.</summary>
public sealed record ThemeOption(string Code, string DisplayNameKey);
