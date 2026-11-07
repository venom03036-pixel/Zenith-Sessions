using System.Globalization;
using System.Windows.Data;

namespace Zenith.Sessions.App;

/// <summary>
/// Card grid columns from the grid's available width: two at or above <see cref="TwoColumnMinimumWidth"/>
/// device-independent pixels, otherwise one. Presentation only; it never touches view-model state.
/// </summary>
public sealed class CardColumnsConverter : IValueConverter
{
    public const double TwoColumnMinimumWidth = 600;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double width && width >= TwoColumnMinimumWidth ? 2 : 1;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("Column counts are derived from width only.");
}
