using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using FluentAssertions;
using MacroSuite.App.Themes;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Themes;

public sealed class ThemeTests
{
    [Fact]
    public void Presets_HaveFiveDistinctCompletePalettesAndSafeFallback()
    {
        ThemeCatalog.Presets.Should().HaveCount(5);
        ThemeCatalog.Presets.Select(p => p.Id).Should().OnlyHaveUniqueItems();
        ThemeCatalog.Presets.Select(p => p.Colors["AccentPrimary"]).Should().OnlyHaveUniqueItems();
        ThemeCatalog.Presets.Select(p => p.Colors["AppBackground"]).Should().OnlyHaveUniqueItems();
        ThemeCatalog.Resolve(null).Id.Should().Be(ThemeCatalog.DefaultId);
        ThemeCatalog.Resolve("retired-or-invalid-theme").Id.Should().Be(ThemeCatalog.DefaultId);
        ThemeCatalog.Resolve("  AMBER-NIGHT ").Id.Should().Be("amber-night");
        foreach (var theme in ThemeCatalog.Presets)
        {
            theme.Colors.Keys.Should().BeEquivalentTo(ThemeCatalog.Presets[0].Colors.Keys);
            theme.Colors.Values.Should().OnlyContain(color => color.A == 255, "opaque surfaces make contrast deterministic");
        }
    }

    [Fact]
    public void EveryPalette_KeepsTextButtonsAndControlStatesLegible()
    {
        foreach (var theme in ThemeCatalog.Presets)
        {
            string[] surfaces = ["AppBackground", "CardBackground", "SurfaceBackground", "HoverBackground", "ActiveBackground", "SelectionBackground"];
            foreach (string background in surfaces)
            {
                foreach (string foreground in new[] { "TextPrimary", "TextSecondary", "TextMuted" })
                    AssertContrast(theme, foreground, background, 4.5);
                foreach (string indicator in new[] { "AccentSuccess", "AccentWarning", "AccentDanger", "ActionMouse", "ActionKeyboard", "ActionWait", "ActionFlow", "ActionImage", "ActionAI", "ActionColor" })
                    AssertContrast(theme, indicator, background, 4.5);
                AssertContrast(theme, "BorderBrushFocus", background, 3);
            }
            foreach (string background in new[] { "AccentPrimary", "PrimaryHoverBackground", "PrimaryPressedBackground" })
                AssertContrast(theme, "PrimaryForeground", background, 4.5);
            foreach (string background in new[] { "DangerGradient", "DangerHoverBackground", "DangerPressedBackground" })
                AssertContrast(theme, "DangerForeground", background, 4.5);
            foreach (string background in new[] { "CardBackground", "SurfaceBackground", "HoverBackground", "ActiveBackground" })
                AssertContrast(theme, "ControlBorder", background, 3);
            AssertContrast(theme, "SelectionBorder", "SelectionBackground", 3);
            AssertContrast(theme, "SelectionForeground", "SelectionBackground", 4.5);
            AssertContrast(theme, "DisabledForeground", "DisabledBackground", 4.5);
            AssertContrast(theme, "AccentSuccess", "BadgeBgSuccess", 4.5);
            AssertContrast(theme, "AccentWarning", "BadgeBgWarning", 4.5);
            AssertContrast(theme, "AccentDanger", "BadgeBgDanger", 4.5);
        }
    }

    [Fact]
    public void StatusAndActionTypeColors_KeepTheirMeaningAcrossThemes()
    {
        string[] independentKeys = ["AccentSuccess", "AccentWarning", "AccentDanger", "DangerGradient", "ActionMouse", "ActionKeyboard", "ActionWait", "ActionFlow", "ActionImage", "ActionAI", "ActionColor"];
        foreach (string key in independentKeys)
            ThemeCatalog.Presets.Select(p => p.Colors[key]).Distinct().Should().ContainSingle(key);
    }

    [Fact]
    public void Apply_UpdatesExistingDynamicResourceConsumersWithoutAccumulatingDictionaries()
    {
        RunSta(() =>
        {
            var panel = new StackPanel();
            panel.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/MacroSuite.App;component/Themes/Colors.xaml", UriKind.Absolute)
            });
            panel.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/MacroSuite.App;component/Themes/ModernControls.xaml", UriKind.Absolute)
            });
            panel.Resources["UnrelatedResource"] = "preserved";
            var label = new TextBlock { Text = "Theme-aware text", Style = (Style)panel.Resources[typeof(TextBlock)] };
            var button = new Button { Content = "Run", Style = (Style)panel.Resources["PrimaryButton"] };
            var secondary = new Button { Content = "Properties", Style = (Style)panel.Resources["PillButton"] };
            panel.Children.Add(label);
            panel.Children.Add(button);
            panel.Children.Add(secondary);
            button.ApplyTemplate();
            secondary.ApplyTemplate();
            for (int pass = 0; pass < 3; pass++)
            {
                foreach (var theme in ThemeCatalog.Presets)
                {
                    ThemeManager.Apply(panel.Resources, theme.Id);
                    ((SolidColorBrush)label.Foreground).Color.Should().Be(theme.Colors["TextPrimary"]);
                    ((SolidColorBrush)button.Background).Color.Should().Be(theme.Colors["AccentPrimary"]);
                    ((SolidColorBrush)button.Foreground).Color.Should().Be(theme.Colors["PrimaryForeground"]);
                    ((SolidColorBrush)secondary.Foreground).Color.Should().Be(theme.Colors["TextPrimary"]);
                    ((SolidColorBrush)secondary.Background).Color.Should().Be(theme.Colors["SurfaceBackground"]);
                    panel.Resources["UnrelatedResource"].Should().Be("preserved");
                    panel.Resources.Count.Should().Be(theme.Colors.Count + 5);
                    panel.Resources.MergedDictionaries.Should().HaveCount(2);
                    ((SolidColorBrush)panel.Resources["AccentPrimary"]).IsFrozen.Should().BeTrue();
                }
            }
            ThemeManager.Apply(panel.Resources, "unknown");
            ((SolidColorBrush)button.Background).Color.Should().Be(ThemeCatalog.Presets[0].Colors["AccentPrimary"]);
        });
    }

    [Fact]
    public void DesignerColors_MatchDefaultRuntimeTheme()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "MacroSuite.App", "Themes", "Colors.xaml")))
            directory = directory.Parent;
        directory.Should().NotBeNull();
        var document = XDocument.Load(Path.Combine(directory!.FullName, "src", "MacroSuite.App", "Themes", "Colors.xaml"));
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var brushes = document.Descendants(wpf + "SolidColorBrush").ToDictionary(
            element => (string)element.Attribute(x + "Key")!,
            element => (Color)ColorConverter.ConvertFromString((string)element.Attribute("Color")!));
        brushes.Should().BeEquivalentTo(ThemeCatalog.Presets[0].Colors);
    }

    [Theory]
    [InlineData("#101B28", "#1C2B3A")]
    [InlineData("#EFF4FC", "#FFFFFF")]
    public void CustomMaterialBrushes_ProtectTextOverWhiteAndBlackWallpapers(string canvas, string panelColor)
    {
        RunSta(() =>
        {
            var profile = new AppearanceProfile { Canvas = canvas, Panel = panelColor, AutomaticText = true,
                ColorsFromImage = false, BackgroundKind = AppearanceBackgroundKind.Image, PanelOpacity = .1 };
            var resolved = AppearancePalette.Resolve(profile);
            var panel = new StackPanel();
            panel.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("pack://application:,,,/MacroSuite.App;component/Themes/ModernControls.xaml", UriKind.Absolute) });
            panel.SetResourceReference(Panel.BackgroundProperty, "WorkspaceMaterial");
            var label = new TextBlock { Text = "Wallpaper-safe text" };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            panel.Children.Add(label);
            ThemeManager.Apply(panel.Resources, resolved.Palette, resolved.EffectivePanelOpacity);
            ((SolidColorBrush)panel.Background).Color.Should().Be(((SolidColorBrush)panel.Resources["WorkspaceMaterial"]).Color);
            ((SolidColorBrush)label.Foreground).Color.Should().Be(resolved.Palette.Colors["TextSecondary"]);
            foreach (string material in new[] { "WorkspaceMaterial", "PanelMaterial", "HeaderMaterial" })
            {
                Color surface = ((SolidColorBrush)panel.Resources[material]).Color;
                foreach (Color wallpaper in new[] { Colors.White, Colors.Black })
                {
                    Color composite = AppearancePalette.Mix(wallpaper, surface, surface.A / 255d);
                    foreach (string foreground in new[] { "TextPrimary", "TextSecondary", "TextMuted", "AccentDanger", "AccentSuccess", "AccentWarning",
                                 "ActionMouse", "ActionKeyboard", "ActionWait", "ActionFlow", "ActionImage", "ActionAI", "ActionColor" })
                        AppearancePalette.Contrast(resolved.Palette.Colors[foreground], composite).Should()
                            .BeGreaterThanOrEqualTo(4.5, $"{foreground} must remain legible over the actual {material} brush alpha");
                }
            }
        });
    }

    [Fact]
    public void CustomControlMarkersAndWheelFocus_StayVisibleWhenButtonColorsAreIndependent()
    {
        RunSta(() =>
        {
            var panel = new StackPanel();
            panel.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("pack://application:,,,/MacroSuite.App;component/Themes/ModernControls.xaml", UriKind.Absolute) });
            var radio = new RadioButton { Content = "Selected", IsChecked = true, Style = (Style)panel.Resources[typeof(RadioButton)] };
            var check = new CheckBox { Content = "Checked", IsChecked = true, Style = (Style)panel.Resources[typeof(CheckBox)] };
            var toggle = new CheckBox { IsChecked = true, Style = (Style)panel.Resources["ModernToggleSwitch"] };
            var text = new TextBox { Text = "Caret", Style = (Style)panel.Resources[typeof(TextBox)] };
            var password = new PasswordBox { Style = (Style)panel.Resources[typeof(PasswordBox)] };
            var tab = new Button { Content = "Active tab", Tag = true, Style = (Style)panel.Resources["TerminalTabButton"] };
            var wheel = new ColorWheel();
            foreach (UIElement control in new UIElement[] { radio, check, toggle, text, password, tab, wheel }) panel.Children.Add(control);
            var profiles = new[]
            {
                new AppearanceProfile { Canvas = "#EFF4FC", Panel = "#FFFFFF", Accent = "#83D8C1", Button = "#83D8C1" },
                new AppearanceProfile { Canvas = "#FFFFFF", Panel = "#FFFFFF", Accent = "#0000AA", Button = "#FFFFFF" },
                new AppearanceProfile { Canvas = "#101B28", Panel = "#1C2B3A", Accent = "#83D8C1", Button = "#000000" }
            };
            static Color ColorOf(Brush brush) => ((SolidColorBrush)brush).Color;
            static void Visible(Brush marker, Brush surface) => AppearancePalette.Contrast(ColorOf(marker), ColorOf(surface)).Should().BeGreaterThanOrEqualTo(3);
            foreach (var profile in profiles)
            {
                var resolved = AppearancePalette.Resolve(profile);
                ThemeManager.Apply(panel.Resources, resolved.Palette);
                panel.Measure(new Size(400, 700)); panel.Arrange(new Rect(0, 0, 400, 700)); panel.UpdateLayout();
                var outer = (System.Windows.Shapes.Ellipse)radio.Template.FindName("Outer", radio);
                var inner = (System.Windows.Shapes.Ellipse)radio.Template.FindName("Inner", radio);
                inner.Visibility.Should().Be(Visibility.Visible);
                Visible(inner.Fill, outer.Fill); Visible(outer.Stroke, (Brush)panel.Resources["CardBackground"]);
                var checkBorder = (Border)check.Template.FindName("Border", check);
                var checkMark = (System.Windows.Shapes.Path)check.Template.FindName("CheckMark", check);
                Visible(checkMark.Stroke, checkBorder.Background); Visible(checkBorder.BorderBrush, (Brush)panel.Resources["CardBackground"]);
                var track = (Border)toggle.Template.FindName("Track", toggle);
                var thumb = (System.Windows.Shapes.Ellipse)toggle.Template.FindName("Thumb", toggle);
                Visible(thumb.Fill, track.Background); Visible(track.BorderBrush, (Brush)panel.Resources["CardBackground"]);
                Visible(text.CaretBrush, text.Background); Visible(password.CaretBrush, password.Background);
                Visible(tab.BorderBrush, (Brush)panel.Resources["CardBackground"]);
                ColorOf(wheel.FocusIndicatorBrush).Should().Be(resolved.Palette.Colors["BorderBrushFocus"]);
                Visible(wheel.FocusIndicatorBrush, (Brush)panel.Resources["AppBackground"]);
                Visible(wheel.FocusIndicatorBrush, (Brush)panel.Resources["CardBackground"]);
            }
        });
    }

    private static void AssertContrast(ThemePreset theme, string foreground, string background, double minimum)
    {
        double first = Luminance(theme.Colors[foreground]), second = Luminance(theme.Colors[background]);
        double contrast = (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
        contrast.Should().BeGreaterThanOrEqualTo(minimum, $"{theme.Name}: {foreground} on {background}");
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            double value = channel / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10)).Should().BeTrue("the isolated visual check should complete promptly");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
