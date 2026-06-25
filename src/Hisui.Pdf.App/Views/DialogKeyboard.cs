using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Hisui.Pdf.App.Views;

/// <summary>
/// Reliable Enter-to-accept / Esc-to-cancel for dialogs. Avalonia's <c>Button.IsDefault</c> /
/// <c>IsCancel</c> don't fire while a focused text box swallows the key, and KeyDown is bubble-only (no
/// tunnel phase), so dialogs handle the key on bubble with <c>handledEventsToo</c> — that sees it even
/// after the text box marks it handled. Enter is left alone while a multi-line text box has focus, so it
/// still inserts a newline there.
/// </summary>
internal static class DialogKeyboard
{
    public static void WireAcceptCancel(this Window window, Action accept, Action cancel)
    {
        window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter when !IsInMultilineTextBox(window):
                    accept();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    cancel();
                    e.Handled = true;
                    break;
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static bool IsInMultilineTextBox(Window window) =>
        window.FocusManager?.GetFocusedElement() is TextBox { AcceptsReturn: true };
}
