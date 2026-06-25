using Avalonia.Controls;
using Avalonia.Input;
using Hisui.Pdf.App.Localization;

namespace Hisui.Pdf.App.Views;

/// <summary>
/// Single-field password prompt, reused to set a protection password and to unlock an encrypted PDF.
/// The entered password is exposed via <see cref="Password"/> when the dialog closes with true.
/// </summary>
public partial class PasswordDialog : Window
{
    public string? Password { get; private set; }

    public PasswordDialog() : this("Password.EnterPrompt") { }

    public PasswordDialog(string promptKey)
    {
        InitializeComponent();

        Title = Localizer.Instance["Password.Title"];
        PromptLabel.Text = Localizer.Instance[promptKey];

        RevealBox.IsCheckedChanged += (_, _) => PasswordBox.PasswordChar = RevealBox.IsChecked == true ? '\0' : '●';
        OkButton.Click += (_, _) => OnOk();
        CancelButton.Click += (_, _) => Close(false);
        PasswordBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) OnOk(); };
        Opened += (_, _) => PasswordBox.Focus();
    }

    private void OnOk()
    {
        if (string.IsNullOrEmpty(PasswordBox.Text))
        {
            ErrorLabel.Text = Localizer.Instance["Password.Required"];
            ErrorLabel.IsVisible = true;
            return;
        }

        Password = PasswordBox.Text;
        Close(true);
    }
}
