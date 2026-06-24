using System.IO;
using Microsoft.Extensions.Logging;

namespace Hisui.Pdf.App.Logging;

/// <summary>
/// Appends log entries (Info and above) to a rolling text file in %APPDATA%\Hisui.PDF.
/// Thread-safe via a single shared lock; one StreamWriter per provider instance.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public FileLoggerProvider()
    {
        Directory.CreateDirectory(AppDataPaths.BaseDir);
        _writer = new StreamWriter(AppDataPaths.LogFile, append: true) { AutoFlush = true };
        _writer.WriteLine($"--- session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer, _lock);

    public void Dispose() => _writer.Dispose();
}

internal sealed class FileLogger(string category, StreamWriter writer, object @lock) : ILogger
{
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var line = $"[{DateTime.Now:HH:mm:ss}] [{logLevel,-11}] {category}: {formatter(state, exception)}";
        if (exception is not null) line += $"\n{exception}";
        lock (@lock) { writer.WriteLine(line); }
    }
}
