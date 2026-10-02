using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Hisui.Pdf.App.Localization;
using Hisui.Pdf.App.Logging;
using Hisui.Pdf.App.Services;
using Hisui.Pdf.App.ViewModels;
using Hisui.Pdf.App.Views;
using Hisui.Pdf.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hisui.Pdf.App;

public partial class App : Application
{
    private IHost? _host;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var builder = Host.CreateApplicationBuilder();

            builder.Services.AddPdfCore();
            builder.Services.AddSingleton<IFileDialogService, FileDialogService>();
            builder.Services.AddSingleton<ISettingsService, SettingsService>();
            builder.Services.AddSingleton<ISignatureService, SignatureService>();
            builder.Services.AddSingleton<IPrintService, PrintService>();
            builder.Services.AddSingleton<IWindowService, WindowService>();
            builder.Services.AddSingleton<IUpdateService, UpdateService>();
            builder.Services.AddSingleton<ILocalizer>(Localizer.Instance);
            // Transient so each window owns an independent view model + document session (multi-window).
            builder.Services.AddTransient<MainViewModel>();
            builder.Services.AddTransient<MainWindow>();

            builder.Logging.ClearProviders();
            builder.Logging.AddDebug();
            builder.Logging.AddProvider(new FileLoggerProvider());

            _host = builder.Build();
            await _host.StartAsync();

            // Make crashes and fire-and-forget failures visible in the log viewer.
            var crashLog = _host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Unhandled");
            AppDomain.CurrentDomain.UnhandledException += (_, e) => crashLog.LogCritical(e.ExceptionObject as Exception, "Unhandled exception");
            TaskScheduler.UnobservedTaskException += (_, e) => crashLog.LogError(e.Exception, "Unobserved task exception");

            // Apply the saved language before any view is built so the first render is localized.
            var settings = _host.Services.GetRequiredService<ISettingsService>();
            Localizer.Instance.SetLanguage(settings.Settings.Language ?? Localizer.BaseLanguage);

            // Apply the saved theme (Light / Dark / System) before the first window is shown.
            Theming.ThemeManager.Apply(settings.Settings.Theme);

            var window = _host.Services.GetRequiredService<MainWindow>();
            desktop.MainWindow = window;

            // Silent startup check (first window only); only prompts if a newer, non-skipped release exists.
            if (settings.Settings.CheckForUpdates)
                window.Opened += (_, _) => _ = window.CheckForUpdatesAsync(manual: false);

            // Support "Open with" / command-line: open a file passed as the first arg into this window's VM.
            if (desktop.Args?.Length > 0 && File.Exists(desktop.Args[0]) && window.DataContext is MainViewModel vm)
                await vm.OpenPathAsync(desktop.Args[0]);

            desktop.Exit += async (_, _) =>
            {
                await _host.StopAsync();
                _host.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
