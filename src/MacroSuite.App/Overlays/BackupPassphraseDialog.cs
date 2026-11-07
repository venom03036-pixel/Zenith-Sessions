using System.Windows;
using System.Windows.Controls;
using MacroSuite.Infrastructure.Storage;

namespace MacroSuite.App.Overlays;

/// <summary>Passwords are never placed in view-model properties, settings, or logs.</summary>
public sealed class BackupPassphraseDialog : Window
{
    private readonly PasswordBox _password = new() { MinHeight = 36, MaxLength = 1024 };
    private readonly PasswordBox _confirmation = new() { MinHeight = 36, MaxLength = 1024 };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly bool _confirm;
    private string? _result;

    private BackupPassphraseDialog(bool confirm)
    {
        _confirm = confirm;
        Title = confirm ? "Protect encrypted backup" : "Unlock encrypted backup";
        Width = 470; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "CardBackground");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = confirm
            ? "Choose a passphrase of at least 12 characters. It is required to restore this backup on another PC or Windows account and cannot be reset."
            : "Enter the passphrase chosen when this backup was exported. The backup stays unchanged if unlocking fails.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        panel.Children.Add(new TextBlock { Text = "Passphrase", Margin = new Thickness(0, 0, 0, 6) }); panel.Children.Add(_password);
        if (confirm)
        {
            panel.Children.Add(new TextBlock { Text = "Confirm passphrase", Margin = new Thickness(0, 14, 0, 6) }); panel.Children.Add(_confirmation);
        }
        _error.SetResourceReference(TextBlock.ForegroundProperty, "AccentDanger"); panel.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinHeight = 36, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var accept = new Button { Content = confirm ? "Export encrypted backup" : "Unlock", IsDefault = true, MinHeight = 36, MinWidth = 100 };
        cancel.Click += (_, _) => { DialogResult = false; };
        accept.Click += (_, _) => Accept(); buttons.Children.Add(cancel); buttons.Children.Add(accept); panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => _password.Focus();
        Closed += (_, _) => { _password.Clear(); _confirmation.Clear(); };
    }

    private void Accept()
    {
        try
        {
            string value = _password.Password;
            if (_confirm)
            {
                ProtectedMacroStore.ValidatePassphrase(value);
                if (!StringComparer.Ordinal.Equals(value, _confirmation.Password)) { _error.Text = "The passphrases do not match."; return; }
            }
            else if (string.IsNullOrEmpty(value)) { _error.Text = "Enter your backup passphrase."; return; }
            _result = value; DialogResult = true;
        }
        catch (ArgumentException) { _error.Text = "Use a passphrase between 12 and 1024 characters."; }
    }

    public static string? Prompt(Window? owner, bool confirm)
    {
        var dialog = new BackupPassphraseDialog(confirm);
        OverlayWindow.Attach(dialog, owner);
        string? result = dialog.ShowDialog() == true ? dialog._result : null;
        dialog._result = null;
        return result;
    }
}
