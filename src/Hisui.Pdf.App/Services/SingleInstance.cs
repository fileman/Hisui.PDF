using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace Hisui.Pdf.App.Services;

/// <summary>
/// Keeps one app process per user session: later launches (e.g. Explorer's "Open with" on several PDFs)
/// forward their file paths to the running instance over a named pipe and exit, so all files end up as tabs.
/// </summary>
internal static class SingleInstance
{
    private static readonly string Id = $"HisuiPDF.{Environment.UserName}.{Process.GetCurrentProcess().SessionId}";
    private static readonly object Gate = new();
    private static List<string>? _pending = [];
    private static Action<IReadOnlyList<string>>? _received;
    private static Mutex? _mutex;

    /// <summary>Set once the UI is ready; paths that arrived earlier are delivered immediately.</summary>
    public static Action<IReadOnlyList<string>>? Received
    {
        set
        {
            List<string>? backlog;
            lock (Gate) { _received = value; backlog = _pending; _pending = null; }
            if (value is not null && backlog is { Count: > 0 }) value(backlog);
        }
    }

    /// <summary>Returns true when this process is the primary instance and should continue starting up.
    /// Returns false when the arguments were handed to the running instance.</summary>
    public static bool TryBecomePrimary(string[] args)
    {
        _mutex = new Mutex(true, Id, out var created);
        if (created)
        {
            _ = Task.Run(ListenAsync);
            return true;
        }

        _mutex.Dispose();
        _mutex = null;
        // The primary may still be starting its pipe server (Explorer launches all copies at once): retry briefly.
        // If it never answers, run as an independent instance rather than dropping the files.
        return !Forward(args);
    }

    private static bool Forward(string[] args)
    {
        var paths = args.Select(Path.GetFullPath);
        try
        {
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out);
            client.Connect(10_000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            foreach (var p in paths) writer.WriteLine(p);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return false;
        }
    }

    private static async Task ListenAsync()
    {
        while (true)
        {
            try
            {
                await using var server = new NamedPipeServerStream(Id, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server, Encoding.UTF8);
                var paths = new List<string>();
                while (await reader.ReadLineAsync() is { Length: > 0 } line) paths.Add(line);
                Deliver(paths);
            }
            catch (IOException) { /* client vanished mid-message: keep listening */ }
        }
    }

    private static void Deliver(List<string> paths)
    {
        Action<IReadOnlyList<string>>? handler;
        lock (Gate)
        {
            handler = _received;
            if (handler is null) { _pending!.AddRange(paths); return; }
        }
        handler(paths);
    }
}
