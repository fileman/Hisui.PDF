namespace Hisui.Pdf.App.Localization;

/// <summary>A selectable UI language: its culture <paramref name="Code"/> (e.g. "it") and the
/// endonym shown in the language menu.</summary>
public sealed record LanguageOption(string Code, string DisplayName);
