using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MacroSuite.App.Common;

/// <summary>
/// Shared eligibility for in-window keys and RegisterHotKey: never fire transport
/// commands while the user is typing.
/// </summary>
public static class HotkeyFocusGuard
{
    public static bool IsTextEditingContext(IInputElement? focused)
    {
        DependencyObject? current = focused as DependencyObject;
        while (current is not null)
        {
            if (current is TextBox or RichTextBox or PasswordBox)
                return true;
            if (current is ComboBox combo && combo.IsEditable)
                return true;
            current = current is FrameworkElement fe
                ? fe.Parent as DependencyObject
                : null;
        }

        return false;
    }

    public static bool ShouldSuppressTransportHotkeys()
        => IsTextEditingContext(Keyboard.FocusedElement);
}
