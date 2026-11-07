using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MacroSuite.App.Common;

/// <summary>
/// Pushes in-progress TextBox edits into bindings before Apply.
/// LostFocus bindings do not update when Apply is clicked.
/// </summary>
internal static class EditorBindingFlush
{
    public static void FlushFocusedText()
    {
        // Keyboard input state is only available on an STA UI thread. View-model
        // commands remain testable when invoked without a WPF dispatcher.
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            return;

        if (Keyboard.FocusedElement is TextBox tb)
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    public static void FlushTextBoxes(DependencyObject? root)
    {
        if (root is null)
            return;

        if (root is TextBox box)
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            FlushTextBoxes(VisualTreeHelper.GetChild(root, i));
    }
}
