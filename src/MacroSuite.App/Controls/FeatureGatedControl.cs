using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MacroSuite.Core.Licensing;

namespace MacroSuite.App.Controls;

/// <summary>
/// UI container that visually gates controls according to <see cref="IFeatureGate.IsAvailable(FeatureId)"/>.
/// Note: Actual enforcement is always executed strictly at the execution boundary (Rule 18).
/// </summary>
public class FeatureGatedControl : ContentControl
{
    public static readonly DependencyProperty FeatureProperty =
        DependencyProperty.Register(nameof(Feature), typeof(FeatureId), typeof(FeatureGatedControl),
            new PropertyMetadata(FeatureId.BasicMacroRecording, OnStateChanged));

    public static readonly DependencyProperty FeatureGateProperty =
        DependencyProperty.Register(nameof(FeatureGate), typeof(IFeatureGate), typeof(FeatureGatedControl),
            new PropertyMetadata(null, OnStateChanged));

    public FeatureId Feature
    {
        get => (FeatureId)GetValue(FeatureProperty);
        set => SetValue(FeatureProperty, value);
    }

    public IFeatureGate? FeatureGate
    {
        get => (IFeatureGate?)GetValue(FeatureGateProperty);
        set => SetValue(FeatureGateProperty, value);
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FeatureGatedControl control)
        {
            control.UpdateVisualState();
        }
    }

    public FeatureGatedControl()
    {
        Loaded += (s, e) => UpdateVisualState();
    }

    private void UpdateVisualState()
    {
        bool isLicensed = FeatureGate?.IsAvailable(Feature) ?? true;

        if (isLicensed)
        {
            Opacity = 1.0;
            IsEnabled = true;
            ToolTip = null;
        }
        else
        {
            Opacity = 0.45;
            IsEnabled = false;
            ToolTip = $"🔒 Feature '{Feature}' requires an upgraded license tier.";
        }
    }
}
