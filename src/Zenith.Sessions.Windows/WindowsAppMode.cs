using System.Security;
using Microsoft.Win32;

namespace Zenith.Sessions.Windows;

/// <summary>What Windows reported for its app mode: light, dark, or null for no usable answer, with the error type when the read failed.</summary>
public readonly record struct WindowsAppModeReading(bool? AppsUseLightTheme, Type? Failure);

/// <summary>
/// Reads the signed-in user's "Choose your default app mode" (Windows Settings, Personalization, Colors). The key is opened
/// read-only and nothing is ever written: the manager follows the Windows choice, it never changes it.
/// </summary>
public static class WindowsAppMode
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    public const string ValueName = "AppsUseLightTheme";

    public static WindowsAppModeReading Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            return new(Interpret(key?.GetValue(ValueName)), null);
        }
        // Only the type leaves here (Rule 10); the caller records it and keeps the default palette.
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException) { return new(null, ex.GetType()); }
    }

    /// <summary>Windows stores the choice as a DWORD: 0 for dark and any other number for light. A missing or other value is no answer.</summary>
    public static bool? Interpret(object? value) => value is int mode ? mode != 0 : null;
}
