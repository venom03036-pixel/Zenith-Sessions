using System.Globalization;
using System.Windows.Data;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;

namespace MacroSuite.App.ViewModels;

// Presentation metadata only: categories and vector outlines never alter stored actions.
public sealed partial class MacroStepItemViewModel
{
    public string VisualCategory => Step switch
    {
        ClickStep or MouseMoveStep or MouseWheelStep or MouseSweepStep or MouseTrajectoryStep => "Mouse",
        KeyPressStep => "Keyboard",
        AIStep => "AI",
        LoopStep or IfThenElseStep or BreakStep or ContinueStep => "Flow",
        WaitUntilStep { Condition: ImageCondition } => "Image",
        // VisualMatcher only uses a template for Region mode with image dimensions.
        // Pixel mode and incomplete Region setup use the color-matching path.
        VisualWaitStep visual when string.Equals(visual.Mode, "Region", StringComparison.OrdinalIgnoreCase)
            && visual.TemplateBgra is not null && visual.TemplateWidth > 0 && visual.TemplateHeight > 0 => "Image",
        VisualWaitStep or WaitUntilStep => "Color",
        _ => "Wait"
    };

    public string TypeIconData => VisualCategory switch
    {
        "Mouse" => "M 9,1 C 5,1 3,4 3,8 L 3,12 C 3,16 5,19 9,19 C 13,19 15,16 15,12 L 15,8 C 15,4 13,1 9,1 Z M 9,1 L 9,8 M 3,8 L 15,8",
        "Keyboard" => "M 1,3 L 19,3 L 19,16 L 1,16 Z M 4,6 L 5,6 M 8,6 L 9,6 M 12,6 L 13,6 M 16,6 L 17,6 M 4,9 L 5,9 M 8,9 L 9,9 M 12,9 L 13,9 M 16,9 L 17,9 M 5,13 L 15,13",
        "Flow" => "M 3,8 C 3,3 12,0 17,5 M 17,1 L 17,6 L 12,6 M 17,12 C 17,17 8,20 3,15 M 3,19 L 3,14 L 8,14",
        "AI" => "M 2,3 L 18,3 L 18,17 L 2,17 Z M 5,13 L 9,9 L 12,12 L 15,8 M 6,6 L 6,7 M 12,2 L 12,0 M 16,2 L 17,0",
        "Image" => "M 2,2 L 18,2 L 18,18 L 2,18 Z M 4,15 L 9,10 L 12,13 L 15,8 L 18,12 M 6,5 L 7,5 L 7,6 L 6,6 Z",
        "Color" => "M 14,1 L 19,6 L 16,9 L 14,7 L 6,15 L 2,17 L 3,13 L 11,5 L 9,3 Z M 5,12 L 8,15",
        _ => "M 10,1 A 9,9 0 1 1 9.99,1 M 10,5 L 10,10 L 14,12"
    };
}

/// <summary>Moves parameters below the label when the actual sequence pane becomes narrow.</summary>
public sealed class CompactActionLayoutConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not double width || width < 600;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
