using Avalonia;
using Avalonia.Media;
using Hisui.Pdf.App.Services;

namespace Hisui.Pdf.App;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!SingleInstance.TryBecomePrimary(args)) return;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
        // The linux-x64 SkiaSharp native build has no fontconfig, so Skia reports no system default font and
        // Avalonia aborts at startup ("Default font family name can't be null or empty"). Use the bundled Inter.
        return OperatingSystem.IsLinux()
            ? builder.With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
            : builder;
    }
}
