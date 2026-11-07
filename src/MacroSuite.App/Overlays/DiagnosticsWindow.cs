using System.Windows;
using System.Windows.Controls;
using MacroSuite.App.ViewModels;

namespace MacroSuite.App.Overlays;

/// <summary>On-demand settings diagnostics; opening this view starts no capture or telemetry loop.</summary>
public sealed class DiagnosticsWindow : Window
{
    public DiagnosticsWindow(MainViewModel vm)
    {
        Title = "Zenith Macros — Diagnostics";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "CardBackground");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Diagnostics", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        foreach (string line in new[]
        {
            $"Build: {(Common.BuildEnvironment.IsDeveloperBuild ? "Developer edition" : "Standard edition")}",
            $"Account role: {vm.LicenseTier}",
            $"AI provider: {vm.ActiveProvider}",
            $"Configured capture limit: {vm.CurrentFps} frames/s (not a measured FPS)",
            $"Input backend: {vm.SettingsVM.InputBackendStatus}",
            $"Runtime: .NET {Environment.Version}",
            $"Playback: {vm.TopStatusText}"
        }) panel.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) });
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        close.Click += (_, _) => Close();
        panel.Children.Add(close);
        Content = panel;
    }
}
