using System.IO;
using System.Xml.Linq;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class ReleaseUpdatePresentationTests
{
    [Fact]
    public void SettingsExposeTheOwnedUpdateCommandsWithoutReplacingExistingSettings()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "Zenith.Sessions.App", "MainWindow.xaml"))) root = root.Parent;
        Assert.NotNull(root);
        var document = XDocument.Load(Path.Combine(root.FullName, "src", "Zenith.Sessions.App", "MainWindow.xaml"));
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation", x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var panel = Assert.Single(document.Descendants(ui + "Expander"), item => (string?)item.Attribute(x + "Name") == "SettingsReleaseUpdates");
        Assert.Equal("{Binding ReleaseUpdates}", (string?)panel.Attribute("DataContext"));
        Assert.NotEqual("True", (string?)panel.Attribute("IsExpanded"));
        var commands = panel.Descendants(ui + "Button").Select(item => (string?)item.Attribute("Command")).ToArray();
        Assert.Equal(new[] { "{Binding Save}", "{Binding CheckNow}", "{Binding Stop}", "{Binding ApproveNextStart}", "{Binding CancelApproval}", "{Binding DiscardPending}" }, commands);
        Assert.Contains(panel.Descendants(ui + "TextBox"), item => (string?)item.Attribute("Text") == "{Binding FeedUri, UpdateSourceTrigger=PropertyChanged}");
        Assert.Contains(panel.Descendants(ui + "TextBox"), item => (string?)item.Attribute("Text") == "{Binding PublisherCertificateSha256, UpdateSourceTrigger=PropertyChanged}");
        Assert.Contains(panel.Descendants(ui + "CheckBox"), item => (string?)item.Attribute("IsChecked") == "{Binding CheckDaily}");
        Assert.Contains(panel.Descendants(ui + "TextBlock"), item => (string?)item.Attribute("Text") == "{Binding Status}" && (string?)item.Attribute("AutomationProperties.LiveSetting") == "Polite");
        Assert.Contains(document.Descendants(ui + "Button"), item => (string?)item.Attribute("Command") == "{Binding SaveSettings}");
        Assert.Contains(document.Descendants(ui + "Button"), item => (string?)item.Attribute("Command") == "{Binding CleanupCredentials}");
    }
}
