using System.Xml.Linq;
using FluentAssertions;
using MacroSuite.App.ViewModels;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

// Command reachability contracts. The offscreen WPF renderer checks layout and bindings.
public class BuildVerificationTests
{
    private static string AppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MacroSuite.slnx"))) dir = dir.Parent;
        dir.Should().NotBeNull();
        return Path.Combine(dir!.FullName, "src", "MacroSuite.App");
    }

    [Fact]
    public void MainAndMiniToolbar_ExposeSameEmergencyCommand_AndShellHasWindowControls()
    {
        var main = XDocument.Load(Path.Combine(AppRoot(), "MainWindow.xaml"));
        var mini = XDocument.Load(Path.Combine(AppRoot(), "Overlays", "MiniModeOverlay.xaml"));
        foreach (var view in new[] { main, mini })
            view.Descendants().Should().Contain(e => (string?)e.Attribute("Command") == "{Binding PanicCommand}");
        mini.Descendants().Should().Contain(e => (string?)e.Attribute("Text") == "{Binding PanicHotkey}");
        var controls = main.Descendants().Where(e => e.Name.LocalName == "Button").ToArray();
        controls.Should().Contain(e => (string?)e.Attribute("Command") == "{Binding SettingsVM.SwitchToMiniModeCommand}");
        foreach (string handler in new[] { "MinimizeButton_Click", "MaximizeButton_Click", "CloseButton_Click" })
            controls.Should().Contain(e => (string?)e.Attribute("Click") == handler && e.Attribute("AutomationProperties.Name") != null);
    }

    [Fact]
    public void Theme_ProvidesKeyboardFocusAndUsableControlHeight()
    {
        var theme = XDocument.Load(Path.Combine(AppRoot(), "Themes", "ModernControls.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        theme.Descendants().Should().Contain(e => (string?)e.Attribute(x + "Key") == "KeyboardFocusRing");
        foreach (string name in new[] { "ModernButton", "{x:Type TextBox}", "{x:Type ComboBox}" })
        {
            var style = theme.Root!.Elements().Single(e => e.Name.LocalName == "Style"
                && ((string?)e.Attribute(x + "Key") == name
                    || (e.Attribute(x + "Key") is null && (string?)e.Attribute("TargetType") == name)));
            style.Elements().Should().Contain(e => (string?)e.Attribute("Property") == "FocusVisualStyle");
            style.Elements().Should().Contain(e => (string?)e.Attribute("Property") == "MinHeight"
                && (double?)e.Attribute("Value") >= 32);
        }
    }

    [Fact]
    public void Settings_CredentialsAreMaskedAndGlobalPanicCaptureRemainsReachable()
    {
        var view = XDocument.Load(Path.Combine(AppRoot(), "Views", "SettingsView.xaml"));
        view.Descendants().Where(e => e.Name.LocalName == "TabItem").Select(e => (string?)e.Attribute("Header"))
            .Should().Equal("General", "Appearance", "Recording", "Playback", "Shortcuts", "Detection & AI", "Advanced");
        view.Descendants().Should().Contain(e => e.Name.LocalName == "PasswordBox");
        view.Descendants().Should().Contain(e => (string?)e.Attribute("CommandParameter") == "panic"
            && (string?)e.Attribute("Command") == "{Binding BeginHotkeyCaptureCommand}");
    }

    [Fact]
    public void CompiledAssembly_ExposesShellAndAuthoringContracts()
    {
        var assembly = typeof(MainViewModel).Assembly;
        assembly.GetType("MacroSuite.App.Overlays.AIMacroAuthoringWindow").Should().NotBeNull();
        assembly.GetType("MacroSuite.App.Overlays.MiniModeOverlay").Should().NotBeNull();
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.PanicCommand)).Should().NotBeNull();
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.GenerateMacroWithAICommand)).Should().NotBeNull();
    }
}
