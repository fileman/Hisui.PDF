using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Hisui.Pdf.App.Localization;

namespace Hisui.Pdf.App.Views;

/// <summary>
/// Small code-built dialog: a column of captioned controls plus Cancel/OK, or a message with a row of choices.
/// Saves a XAML file per simple prompt; anything with real layout keeps its own dialog.
/// </summary>
internal sealed class FormDialog : Window
{
    private readonly StackPanel _rows = new() { Spacing = 10 };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };

    public FormDialog(string title, double width = 460)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, $Default");
        this.Bind(BackgroundProperty, this.GetResourceObservable("RibbonBarBrush"));
        // Buttons stay visible below the (scrollable) rows however tall the form gets.
        _buttons.Margin = new Thickness(24, 6, 24, 18);
        DockPanel.SetDock(_buttons, Dock.Bottom);
        Content = new DockPanel
        {
            Children =
            {
                _buttons,
                new ScrollViewer { MaxHeight = 560, Content = new StackPanel { Margin = new Thickness(24, 20, 24, 6), Children = { _rows } } },
            },
        };
        this.WireAcceptCancel(() => Accept?.Invoke(), () => Close(null));
    }

    private Action? Accept;

    /// <summary>Adds <paramref name="control"/> under an optional caption and returns it.</summary>
    public T Add<T>(string? caption, T control) where T : Control
    {
        if (caption is null) _rows.Children.Add(control);
        else _rows.Children.Add(new StackPanel { Spacing = 3, Children = { new TextBlock { Classes = { "caption" }, Text = caption }, control } });
        return control;
    }

    public TextBlock AddText(string text, bool hint = false) =>
        Add(null, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Classes = { hint ? "dialog-hint" : "body" } });

    /// <summary>Shows Cancel/OK; true when accepted and <paramref name="validate"/> (if any) agrees.</summary>
    public async Task<bool> ShowAsync(Window owner, string? okText = null, Func<bool>? validate = null, bool showCancel = true)
    {
        var cancel = new Button { Content = Localizer.Instance["Dialog.Cancel"], Padding = new Thickness(16, 6) };
        var ok = new Button { Content = okText ?? Localizer.Instance["Dialog.Apply"], Classes = { "primary" } };
        cancel.Click += (_, _) => Close(null);
        Accept = () => { if (validate?.Invoke() ?? true) Close(true); };
        ok.Click += (_, _) => Accept();
        if (showCancel) _buttons.Children.Add(cancel);
        _buttons.Children.Add(ok);
        return await ShowDialog<object?>(owner) is true;
    }

    /// <summary>Message with one button per choice; returns the chosen index, or -1 when dismissed (Esc / close).
    /// The last choice is the default (Enter).</summary>
    public static async Task<int> ChooseAsync(Window owner, string title, string message, params string[] choices)
    {
        var dialog = new FormDialog(title);
        dialog.AddText(message);
        for (var i = 0; i < choices.Length; i++)
        {
            var index = i;
            var button = new Button { Content = choices[i], Padding = new Thickness(16, 6) };
            if (i == choices.Length - 1) button.Classes.Add("primary");
            button.Click += (_, _) => dialog.Close(index);
            dialog._buttons.Children.Add(button);
        }
        dialog.Accept = () => dialog.Close(choices.Length - 1);
        return await dialog.ShowDialog<object?>(owner) is int chosen ? chosen : -1;
    }

    /// <summary>Message with a single OK.</summary>
    public static Task InfoAsync(Window owner, string title, string message) =>
        ChooseAsync(owner, title, message, "OK");
}
