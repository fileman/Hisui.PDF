using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Hisui.Pdf.App.Localization;

namespace Hisui.Pdf.App.Views;

/// <summary>Shows the tail of hisui.log; by default only warnings, errors and their stack traces.</summary>
public partial class LogViewerDialog : Window
{
    private const int TailBytes = 512 * 1024;

    public LogViewerDialog()
    {
        InitializeComponent();

        var errorsOnly = this.FindControl<CheckBox>("ErrorsOnlyBox")!;
        errorsOnly.IsChecked = true;
        errorsOnly.IsCheckedChanged += (_, _) => Reload();
        this.FindControl<Button>("RefreshButton")!.Click += (_, _) => Reload();
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
        this.FindControl<Button>("FolderButton")!.Click += (_, _) =>
            Process.Start(new ProcessStartInfo(AppDataPaths.BaseDir) { UseShellExecute = true });
        this.FindControl<Button>("CopyButton")!.Click += async (_, _) =>
        {
            if (Clipboard is { } c) await c.SetTextAsync(this.FindControl<TextBox>("LogText")!.Text ?? "");
        };

        Opened += (_, _) => Reload();
    }

    private void Reload()
    {
        var box = this.FindControl<TextBox>("LogText")!;
        var text = ReadTail();
        if (this.FindControl<CheckBox>("ErrorsOnlyBox")!.IsChecked == true) text = FilterProblems(text);
        box.Text = text.Length > 0 ? text : Localizer.Instance["Log.Empty"];
        box.CaretIndex = box.Text.Length; // jump to the newest entry
    }

    private static string ReadTail()
    {
        try
        {
            // The logger keeps the file open for writing, so open with ReadWrite sharing.
            using var fs = new FileStream(AppDataPaths.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length > TailBytes) fs.Seek(-TailBytes, SeekOrigin.End);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>Keeps entries whose level is Warning or worse; continuation lines (stack traces) follow their entry.</summary>
    internal static string FilterProblems(string log)
    {
        var keep = false;
        var lines = new List<string>();
        foreach (var line in log.Split('\n'))
        {
            if (line.StartsWith("[", StringComparison.Ordinal))
                keep = line.Contains("] [Warning", StringComparison.Ordinal)
                    || line.Contains("] [Error", StringComparison.Ordinal)
                    || line.Contains("] [Critical", StringComparison.Ordinal);
            else if (line.StartsWith("---", StringComparison.Ordinal))
                keep = false;
            if (keep) lines.Add(line.TrimEnd('\r'));
        }
        return string.Join('\n', lines);
    }
}
