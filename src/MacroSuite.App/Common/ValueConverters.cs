using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MacroSuite.App.Common;

/// <summary>
/// Converts a string value to bool (true if value == parameter).
/// Used for segmented selector ToggleButton IsChecked binding.
/// </summary>
[ValueConversion(typeof(string), typeof(bool))]
public sealed class EqualityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.ToString()?.Equals(parameter?.ToString(), StringComparison.OrdinalIgnoreCase) ?? false;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Converts a bool to "▲" (expanded) or "▼" (collapsed) chevron text.
/// </summary>
[ValueConversion(typeof(bool), typeof(string))]
public sealed class BoolToExpandArrowConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "▲" : "▼";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Converts bool to Visibility (false = Collapsed, true = Visible).
/// Inverse of <see cref="BooleanToVisibilityConverter"/>.
/// </summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
