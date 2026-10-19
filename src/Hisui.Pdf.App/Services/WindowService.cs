using System.IO;
using Hisui.Pdf.App.ViewModels;
using Hisui.Pdf.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Hisui.Pdf.App.Services;

/// <summary>Opens documents as tabs of the shell <see cref="MainWindow"/>; each tab has its own
/// <see cref="MainViewModel"/> and document session.</summary>
internal sealed class WindowService : IWindowService
{
    private readonly IServiceProvider _services;

    public WindowService(IServiceProvider services) => _services = services;

    public void OpenWindow(string? filePath = null) =>
        _services.GetRequiredService<MainWindow>().OpenInTab(filePath is not null && File.Exists(filePath) ? filePath : null);
}
