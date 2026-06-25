using System.IO;
using Hisui.Pdf.App.ViewModels;
using Hisui.Pdf.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Hisui.Pdf.App.Services;

/// <summary>
/// Resolves a fresh <see cref="MainWindow"/> (with its own transient <see cref="MainViewModel"/> and
/// document session) from the container and shows it, giving each open document an independent window.
/// </summary>
internal sealed class WindowService : IWindowService
{
    private readonly IServiceProvider _services;

    public WindowService(IServiceProvider services) => _services = services;

    public void OpenWindow(string? filePath = null)
    {
        var window = _services.GetRequiredService<MainWindow>();
        window.Show();

        if (filePath is not null && File.Exists(filePath) && window.DataContext is MainViewModel vm)
            _ = vm.OpenPathAsync(filePath);
    }
}
