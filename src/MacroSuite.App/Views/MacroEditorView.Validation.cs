using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using MacroSuite.App.ViewModels;

namespace MacroSuite.App.Views;

public partial class MacroEditorView
{
    private bool CommitRecordingFields()
    {
        // Updating the actual control expression catches conversion errors that have
        // deliberately not replaced the last valid view-model value.
        foreach (var element in RecordingFields(this))
        {
            if (!element.IsEnabled) continue;
            if (element is TextBox text)
            {
                var binding = text.GetBindingExpression(TextBox.TextProperty);
                if (binding?.ParentBinding.Path?.Path == nameof(MacroEditorViewModel.RepeatCountInputText)
                    && (!int.TryParse(text.Text, out int count) || count < 1))
                {
                    if (DataContext is MacroEditorViewModel invalid) invalid.ReportRecordingFieldError("Repeat count", "enter a whole number of at least 1");
                    text.Focus();
                    return false;
                }
                binding?.UpdateSource();
            }
            else if (element is Selector selector) selector.GetBindingExpression(Selector.SelectedValueProperty)?.UpdateSource();
            if (!Validation.GetHasError(element)) continue;
            var error = Validation.GetErrors(element).FirstOrDefault();
            string field = element is TextBox fieldText
                ? fieldText.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path ?? "the highlighted field"
                : "the highlighted field";
            if (DataContext is MacroEditorViewModel vm) vm.ReportRecordingFieldError(field, error?.ErrorContent?.ToString() ?? "enter a valid value");
            element.Focus();
            return false;
        }
        return true;
    }

    private static IEnumerable<FrameworkElement> RecordingFields(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element)
            {
                if (element.Visibility != Visibility.Visible) continue;
                yield return element;
            }
            foreach (var nested in RecordingFields(child)) yield return nested;
        }
    }
}
