using Avalonia;
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

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
