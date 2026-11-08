using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Zenith.Sessions.App;

/// <summary>Passwords stay in PasswordBox/SecureString and owned mutable UTF-8 buffers, never the view model.</summary>
public sealed class VaultPasswordDialog : Window
{
    private readonly Action _cancel;
    private bool _busy, _closeWhenDone;
    public VaultPasswordDialog(VaultPasswordAction action, Func<ReadOnlyMemory<byte>, Task<bool>> apply, Func<string> problem, Action cancel)
    {
        _cancel = cancel;
        Title = action == VaultPasswordAction.Unlock ? "Unlock saved sign-ins" : action == VaultPasswordAction.Disable ? "Disable master password" : "Protect saved sign-ins";
        Width = 500; SizeToContent = SizeToContent.Height; MinWidth = 420; MaxHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "CardBackground"); SetResourceReference(ForegroundProperty, "TextPrimary");
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 23, FontWeight = FontWeights.SemiBold });
        var explanation = new TextBlock { Text = action == VaultPasswordAction.Disable
            ? "Your saved sign-ins will return to Windows-account protection only. Enter the current master password to confirm."
            : action == VaultPasswordAction.Unlock ? "Enter the master password for this library. This is your vault password, not a Roblox password."
            : "Choose a master password, or use the same password to resume interrupted encryption. A forgotten password means the saved sign-ins are gone. Profiles can be kept and signed in again. Use at least 12 characters.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 18) };
        explanation.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); panel.Children.Add(explanation);
        panel.Children.Add(new TextBlock { Text = "Master password", Margin = new Thickness(0, 0, 0, 6) });
        var password = new PasswordBox { MaxLength = 256, MinHeight = 36 };
        System.Windows.Automation.AutomationProperties.SetName(password, "Master password"); panel.Children.Add(password);
        PasswordBox? repeated = null;
        if (action == VaultPasswordAction.EnableOrResume)
        {
            panel.Children.Add(new TextBlock { Text = "Repeat master password", Margin = new Thickness(0, 14, 0, 6) });
            repeated = new PasswordBox { MaxLength = 256, MinHeight = 36 };
            System.Windows.Automation.AutomationProperties.SetName(repeated, "Repeat master password"); panel.Children.Add(repeated);
        }
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        error.SetResourceReference(TextBlock.ForegroundProperty, "AccentDanger"); panel.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var submit = new Button { Content = action == VaultPasswordAction.Unlock ? "Unlock" : action == VaultPasswordAction.Disable ? "Disable password" : "Apply protection", IsDefault = true, MinWidth = 130 };
        submit.SetResourceReference(StyleProperty, "PrimaryButton"); actions.Children.Add(cancelButton); actions.Children.Add(submit); panel.Children.Add(actions);
        cancelButton.Click += (_, _) => RequestStop();
        submit.Click += async (_, _) =>
        {
            if (_busy || _closeWhenDone) return;
            byte[] bytes = [], confirmation = [];
            try
            {
                using var secret = password.SecurePassword;
                if (secret.Length == 0 || repeated is not null && secret.Length < 12) { error.Text = "Enter at least 12 characters when enabling protection."; return; }
                bytes = CopyUtf8(secret);
                if (repeated is not null)
                {
                    using var second = repeated.SecurePassword; confirmation = CopyUtf8(second);
                    if (!CryptographicOperations.FixedTimeEquals(bytes, confirmation)) { error.Text = "The passwords do not match."; return; }
                }
                _busy = true; submit.IsEnabled = false; password.IsEnabled = false; if (repeated is not null) repeated.IsEnabled = false;
                password.Clear(); repeated?.Clear(); error.Text = "Working…";
                bool success = await apply(bytes);
                if (success) { _busy = false; DialogResult = true; }
                else error.Text = problem();
            }
            catch (Exception) { error.Text = "The password operation could not finish. Keep the vault for recovery and check its current protection state."; }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(confirmation);
                _busy = false; submit.IsEnabled = true; password.IsEnabled = true; if (repeated is not null) repeated.IsEnabled = true;
                if (_closeWhenDone) Close();
            }
        };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) => password.Focus();
        Closing += (_, args) => { password.Clear(); repeated?.Clear(); if (_busy) { args.Cancel = true; _closeWhenDone = true; _cancel(); } };
    }
    public void RequestStop() { _closeWhenDone = true; _cancel(); if (!_busy) Close(); }
    private static byte[] CopyUtf8(System.Security.SecureString value)
    {
        nint pointer = 0; char[] chars = new char[value.Length];
        try { pointer = Marshal.SecureStringToGlobalAllocUnicode(value); Marshal.Copy(pointer, chars, 0, chars.Length); return Encoding.UTF8.GetBytes(chars); }
        finally { Array.Clear(chars); if (pointer != 0) Marshal.ZeroFreeGlobalAllocUnicode(pointer); }
    }
}
