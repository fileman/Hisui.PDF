namespace Hisui.Pdf.Core.Model;

/// <summary>
/// Page rotation expressed in clockwise degrees. Values match the degrees PDFsharp applies to a
/// page's <c>Rotate</c> property, so they can be used directly when materialising a document.
/// </summary>
public enum PageRotation
{
    None = 0,
    Clockwise90 = 90,
    UpsideDown = 180,
    CounterClockwise90 = 270,
}
