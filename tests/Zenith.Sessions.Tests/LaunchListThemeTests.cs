using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using MacroSuite.App.Themes;
using Xunit;

namespace Zenith.Sessions.Tests;

[Collection(WpfProductionResourceCollection.Name)]
public sealed class LaunchListThemeTests
{
    [Theory]
    [InlineData("graphite-mint", false)]
    [InlineData("graphite-mint", true)]
    [InlineData("midnight", false)]
    [InlineData("midnight", true)]
    [InlineData("violet-dusk", false)]
    [InlineData("violet-dusk", true)]
    public Task ActualPickerAndQueueRenderThemeSelectionWithoutWindowsWhiteOrBlue(string theme, bool queue) => SearchTest.OnUi(() =>
    {
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var source = XDocument.Load(ClientWindowIntegrationTests.LaunchUiSource());
        var element = new XElement(source.Descendants().Single(node => queue
            ? node.Name == ui + "ListView" && (string?)node.Attribute("AutomationProperties.Name") == "Launch requests"
            : (string?)node.Attribute(x + "Name") == "JoinAccountPicker"));
        element.SetAttributeValue("ItemsSource", null); element.SetAttributeValue("SelectedItem", null);
        foreach (var node in element.Descendants()) node.Attribute("Click")?.Remove(); // Offscreen host has no window code-behind.
        // The production item template and all its text styles remain intact.
        element.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
        XNamespace common = "clr-namespace:MacroSuite.App.Common;assembly=Zenith.Sessions.App";
        element.SetAttributeValue(XNamespace.Xmlns + "common", common.NamespaceName);
        element.AddFirst(new XElement(ui + (queue ? "ListView.Resources" : "ListBox.Resources"),
            new XElement(ui + "ResourceDictionary",
                new XElement(ui + "ResourceDictionary.MergedDictionaries", new XElement(ui + "ResourceDictionary", new XAttribute("Source", "/Zenith.Sessions.App;component/Themes/SessionsControls.xaml"))),
                new XElement(ui + "BooleanToVisibilityConverter", new XAttribute(x + "Key", "VisibleIf")),
                new XElement(common + "InverseBoolToVisibilityConverter", new XAttribute(x + "Key", "HiddenIf")))));
        // The queue's header style lives at Window scope in production, outside this fragment.
        if (queue) foreach (string target in new[] { "GridViewColumnHeader", "ListViewItem" })
            element.Element(ui + "ListView.Resources")!.Element(ui + "ResourceDictionary")!.Add(
                new XElement(source.Root!.Element(ui + "Window.Resources")!.Elements(ui + "Style")
                    .Single(style => (string?)style.Attribute("TargetType") == target)));
        var list = Assert.IsAssignableFrom<ListBox>(XamlReader.Parse(element.ToString()));
        var resources = list.Resources;
        ThemeManager.Apply(resources, theme);
        list.Items.Add(new TestRow());
        list.SelectedIndex = 0;
        Layout(list);
        var item = Assert.IsAssignableFrom<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
        Assert.False(Selector.GetIsSelectionActive(item)); // The white inactive-selection state from the report.
        Color expected = ((SolidColorBrush)resources["SelectionBackground"]).Color;
        var border = Visual(item).OfType<Border>().First();
        Assert.Equal(((SolidColorBrush)resources["CardBackground"]).Color, ColorOf(list.Background));
        Assert.Equal(expected, ColorOf(border.Background));
        var bitmap = new RenderTargetBitmap(900, 160, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(list);
        if (Environment.GetEnvironmentVariable("ZENITH_UI_EVIDENCE_DIR") is { Length: > 0 } evidenceDirectory)
        {
            System.IO.Directory.CreateDirectory(evidenceDirectory);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = System.IO.File.Create(System.IO.Path.Combine(evidenceDirectory, $"{theme}-{(queue ? "queue" : "picker")}.png"));
            encoder.Save(output);
        }
        var point = border.TransformToAncestor(list).Transform(new Point(2, 2));
        var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), pixel, 4, 0);
        Assert.Equal(expected, Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]));
        Assert.Contains(Visual(item).OfType<TextBlock>(), text => text.Text == "Synthetic player");
        foreach (var text in Visual(item).OfType<TextBlock>().Where(text => !string.IsNullOrEmpty(text.Text)))
            Assert.True(Contrast(ColorOf(text.Foreground), expected) >= 4.5, $"Unreadable selected text: {text.Text}");
        list.SelectedIndex = -1; Layout(list);
        Assert.Equal(((SolidColorBrush)resources["CardBackground"]).Color, ColorOf(border.Background));
        return Task.CompletedTask;
    });
    private sealed class TestRow
    {
        public string Label => "Synthetic player"; public string Username => "synthetic_player"; public string Identity => "Saved link";
        public string Initial => "S"; public bool HasAvatar => false; public bool IsChecked { get; set; } = true;
        public string ProcessReceipt => "Not started"; public string ResourceEstimate => "Test estimate"; public string State => "Queued";
        public string RequestedDestination => "Place 42"; public string Detail => "Waiting"; public bool CanCancel => true; public bool CanReview => false;
    }
    private static Color ColorOf(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;
    private static void Layout(FrameworkElement element)
    { element.Measure(new Size(900, 160)); element.Arrange(new Rect(0, 0, 900, 160)); element.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
    private static IEnumerable<DependencyObject> Visual(DependencyObject root)
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Visual(child)) yield return nested; } }
    private static double Contrast(Color a, Color b)
    { double L(Color c) { double C(byte v) { double s = v / 255d; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); } return .2126 * C(c.R) + .7152 * C(c.G) + .0722 * C(c.B); } return (Math.Max(L(a), L(b)) + .05) / (Math.Min(L(a), L(b)) + .05); }
}
