using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MacroSuite.App.Themes;
using Xunit;
using Zenith.Sessions.App;

namespace Zenith.Sessions.Tests;

/// <summary>
/// Hover and press on the Sessions-only buttons (SessionsControls.xaml) under Windows High Contrast, where every text and border
/// colour is the window text colour and the only hover and press fills are the window colour with a bar along the leading edge
/// (SessionsThemes.Apply). No pointer is moved: IsMouseOver is set through its WPF property key and IsPressed through the setter
/// ButtonBase leaves to derived types, so the triggers run as they would under the pointer.
/// </summary>
[Collection(WpfProductionResourceCollection.Name)]
public sealed class HighContrastHoverTests
{
    // Under High Contrast the hover fill is the window colour, and the selection text colour is the Highlight text colour: in
    // High Contrast Black both are black, in White both are white, so a selected segment that took the hover fill lost its label.
    // In the ordinary themes the selected segment must keep its selection fill too, not swap it for the hover colour.
    [Theory]
    [InlineData("high contrast black")]
    [InlineData("high contrast white")]
    [InlineData("graphite-mint")]
    [InlineData("light")]
    public Task TheSelectedSegmentKeepsItsFillAndReadableLabelWhileThePointerIsOverIt(string palette) => OnSta(() =>
    {
        var (root, resources) = Host(palette);
        var segment = new Button { Content = "Previews", Tag = true, Style = (Style)resources["SegmentButton"] };
        root.Child = segment; Layout(root);
        Hover(segment, true); Layout(root);
        var image = RenderedInk.Render(root); var fill = (Border)segment.Template.FindName("Border", segment);
        Color selection = ((SolidColorBrush)resources["SelectionBackground"]).Color;
        Assert.True(selection == RenderedInk.At(image, root, fill, new Point(fill.ActualWidth / 2, 2.5)),
            $"{palette}: the selected segment under the pointer is filled {AppearancePalette.Hex(RenderedInk.At(image, root, fill, new Point(fill.ActualWidth / 2, 2.5)))}, not its selection colour {AppearancePalette.Hex(selection)}.");
        var label = Descendants(segment).OfType<TextBlock>().Single(text => text.Text == "Previews");
        RenderedInk.AssertReadable(image, root, label, selection, 4.5, $"{palette}: the selected segment's label under the pointer");
    });

    // A segment that is not selected still shows the pointer: the window-text bar under High Contrast, the hover colour elsewhere.
    [Theory]
    [InlineData("high contrast black")]
    [InlineData("graphite-mint")]
    public Task AnUnselectedSegmentStillShowsHover(string palette) => OnSta(() =>
    {
        var (root, resources) = Host(palette);
        var segment = new Button { Content = "History", Tag = false, Style = (Style)resources["SegmentButton"] };
        root.Child = segment; Layout(root);
        var fill = (Border)segment.Template.FindName("Border", segment); var edge = new Point(2, fill.ActualHeight / 2);
        Color rest = RenderedInk.At(RenderedInk.Render(root), root, fill, edge);
        Hover(segment, true); Layout(root);
        Color hovered = RenderedInk.At(RenderedInk.Render(root), root, fill, edge);
        Assert.True(hovered != rest, $"{palette}: an unselected segment under the pointer looks the same as at rest ({AppearancePalette.Hex(rest)}).");
        string expected = palette.StartsWith("high contrast ", StringComparison.Ordinal) ? "TextPrimary" : "HoverBackground";
        Assert.Equal(((SolidColorBrush)resources[expected]).Color, hovered);
    });

    // Chips (favorites, recently played) and result cards changed only their border on hover, to TextMuted. Under High Contrast
    // TextMuted and the resting BorderBrushLight are both the window text colour, so the pointer showed nothing. Now they take the
    // theme's hover fill as the other buttons do: under High Contrast that is the window colour with the window-text bar.
    [Theory]
    [InlineData("high contrast black", "ChipButton")]
    [InlineData("high contrast white", "ChipButton")]
    [InlineData("high contrast black", "GameCard")]
    [InlineData("high contrast white", "GameCard")]
    public Task ChipsAndCardsShowTheWindowTextBarUnderHighContrastWhileThePointerIsOverThem(string palette, string style) => OnSta(() =>
    {
        var (root, resources) = Host(palette);
        var button = Styled(resources, style); root.Child = button; Layout(root);
        var shell = (Border)button.Template.FindName("Shell", button);
        // Inside the 1 px border and the 3 px bar, below a card's art (the bar must show beside the card's text, not only behind its art).
        var edge = new Point(2, shell.ActualHeight - 16);
        Color rest = RenderedInk.At(RenderedInk.Render(root), root, shell, edge);
        Color window = ((SolidColorBrush)resources["AppBackground"]).Color, text = ((SolidColorBrush)resources["TextPrimary"]).Color;
        Assert.Equal(window, rest);
        Hover(button, true); Layout(root);
        Color hovered = RenderedInk.At(RenderedInk.Render(root), root, shell, edge);
        Assert.True(hovered != rest, $"{palette}: a {style} under the pointer looks the same as at rest ({AppearancePalette.Hex(rest)}).");
        Assert.Equal(text, hovered);
        // The bar is the only change: the label still reads on the window colour beside it.
        var label = Descendants(button).OfType<TextBlock>().Single();
        RenderedInk.AssertReadable(RenderedInk.Render(root), root, label, window, 4.5, $"{palette}: the {style} label under the pointer");
    });

    // In the ordinary themes the same fill is the theme's hover colour, as on the default (secondary) button and the list rows.
    [Theory]
    [InlineData("graphite-mint", "ChipButton")]
    [InlineData("light", "ChipButton")]
    [InlineData("graphite-mint", "GameCard")]
    [InlineData("light", "GameCard")]
    public Task ChipsAndCardsTakeTheThemeHoverFillWhileThePointerIsOverThem(string palette, string style) => OnSta(() =>
    {
        var (root, resources) = Host(palette);
        var button = Styled(resources, style); root.Child = button; Layout(root);
        var shell = (Border)button.Template.FindName("Shell", button); var inside = new Point(4, shell.ActualHeight - 16);
        Color rest = RenderedInk.At(RenderedInk.Render(root), root, shell, inside);
        Assert.Equal(((SolidColorBrush)resources[style == "GameCard" ? "CardBackground" : "SurfaceBackground"]).Color, rest);
        Hover(button, true); Layout(root);
        Assert.Equal(((SolidColorBrush)resources["HoverBackground"]).Color, RenderedInk.At(RenderedInk.Render(root), root, shell, inside));
    });

    // The bar marks the state without touching what the button shows. The narrowest controls that take the press fill are the
    // icon buttons: 32 px with a centred 16 px glyph (Lock, More options) and 28 px with a 13 px glyph (Refresh overview, Close
    // details); the secondary button keeps 12 px of padding before its label. Pressed is told from hovered by the wider bar.
    [Theory]
    [InlineData("high contrast black", "IconButton")]
    [InlineData("high contrast white", "IconButton")]
    [InlineData("high contrast black", "IconButton 28")]
    [InlineData("high contrast white", "IconButton 28")]
    [InlineData("high contrast black", "SecondaryButton")]
    [InlineData("high contrast white", "SecondaryButton")]
    public Task APressedIconOrSecondaryButtonShowsAWiderBarThatStopsShortOfItsGlyphOrLabel(string palette, string style) => OnSta(() =>
    {
        var (root, resources) = Host(palette);
        var button = Pressable(resources, style); root.Child = button; Layout(root);
        var fill = (Border)button.Template.FindName("Border", button);
        FrameworkElement content = style.StartsWith("IconButton", StringComparison.Ordinal)
            ? Descendants(button).OfType<System.Windows.Shapes.Path>().Single() : Descendants(button).OfType<TextBlock>().Single();
        string shown = content is TextBlock ? "label" : "glyph";
        double contentLeft = content.TransformToAncestor(fill).Transform(new Point(0, 0)).X;
        Color window = ((SolidColorBrush)resources["AppBackground"]).Color, text = ((SolidColorBrush)resources["TextPrimary"]).Color;
        Hover(button, true); Layout(root);
        int hovered = LeadingRun(RenderedInk.Render(root), root, fill, text);
        // A pressed button is under the pointer too, so both triggers are on and the press fill wins.
        button.Press(true); Layout(root);
        var image = RenderedInk.Render(root); int pressed = LeadingRun(image, root, fill, text);
        Assert.True(hovered >= 3, $"{palette}: the hovered {style} shows a {hovered} px mark at its leading edge, narrower than the 3 px bar.");
        Assert.True(pressed >= hovered + 3, $"{palette}: the pressed {style} shows a {pressed} px mark after {hovered} px on hover, so pressed cannot be told from hovered.");
        Assert.True(pressed <= contentLeft, $"{palette}: the {style}'s {pressed} px pressed bar reaches its {shown}, which starts {contentLeft} px in.");
        // Beside the bar the glyph or label still reads on the window colour.
        RenderedInk.AssertReadable(image, root, content, window, 4.5, $"{palette}: the pressed {style}'s {shown}");
    });

    // How many pixel columns from the leading edge of border, along its middle row, are drawn in colour before another colour appears.
    private static int LeadingRun(BitmapSource image, FrameworkElement root, FrameworkElement border, Color colour)
    {
        int run = 0; double middle = border.ActualHeight / 2;
        while (run < border.ActualWidth && RenderedInk.At(image, root, border, new Point(run + .5, middle)) == colour) run++;
        return run;
    }

    // A button whose pressed state can be set without a pointer: ButtonBase leaves its setter to derived types.
    private sealed class PressableButton : Button { public void Press(bool pressed) => IsPressed = pressed; }

    // The icon buttons as the title bar and Overview show them (a Glyph path inside), or a secondary button with a label.
    private static PressableButton Pressable(ResourceDictionary resources, string style)
    {
        var button = new PressableButton { Style = (Style)resources[style.Split(' ')[0]] };
        switch (style)
        {
            case "IconButton":
                button.Content = new System.Windows.Shapes.Path { Style = (Style)resources["Glyph"], Data = Geometry.Parse("M3,7 L13,7 L13,14 L3,14 Z M5.2,7 L5.2,5 A2.8,2.8 0 0 1 10.8,5 L10.8,7") };
                break;
            case "IconButton 28":
                button.Width = button.Height = button.MinHeight = 28;
                button.Content = new System.Windows.Shapes.Path { Style = (Style)resources["Glyph"], Width = 13, Height = 13, Data = Geometry.Parse("M13.5,8 A5.5,5.5 0 1 1 11.9,4.1 M13.5,2.5 L13.5,5.7 L10.3,5.7") };
                break;
            default: button.Content = "Add account"; break;
        }
        return button;
    }

    // A chip as the favorites show it, or a result card with its 120 px art area above the text, as Join a game shows it.
    private static Button Styled(ResourceDictionary resources, string style)
    {
        var button = new Button { Style = (Style)resources[style] };
        if (style == "ChipButton") { button.Content = new TextBlock { Text = "Natural Disaster Survival", VerticalAlignment = VerticalAlignment.Center }; return button; }
        var art = new Border { Height = 120, CornerRadius = new CornerRadius(8, 8, 0, 0) }; art.SetResourceReference(Border.BackgroundProperty, "SurfaceBackground");
        var panel = new StackPanel { Width = 220 }; panel.Children.Add(art);
        panel.Children.Add(new TextBlock { Text = "Natural Disaster Survival", FontWeight = FontWeights.SemiBold, Margin = new Thickness(14, 10, 14, 12) });
        button.Content = panel; return button;
    }

    // The shipped Sessions styles with the palette applied as the app applies it, on an opaque page background.
    private static (Border Root, ResourceDictionary Resources) Host(string palette)
    {
        var resources = (ResourceDictionary)Application.LoadComponent(new Uri("/Zenith.Sessions.App;component/Themes/SessionsControls.xaml", UriKind.Relative));
        SessionsThemes.Apply(resources, SessionsThemesTests.Palette(palette));
        var root = new Border { Resources = resources, Padding = new Thickness(8) };
        root.SetResourceReference(Border.BackgroundProperty, "AppBackground");
        return (root, resources);
    }

    private static void Layout(FrameworkElement root)
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); root.Arrange(new Rect(root.DesiredSize)); root.UpdateLayout();
    }

    // IsMouseOver is read-only and owned by WPF's input system; its property key sets it without a pointer, and the triggers follow.
    // (The IsMouseOver getter reads a cached flag that only the input system writes; the triggers read the property itself.)
    private static void Hover(UIElement element, bool over)
    {
        var key = typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as DependencyPropertyKey;
        Assert.True(key is not null, "WPF no longer exposes UIElement.IsMouseOverPropertyKey; this test needs another way to hover.");
        element.SetValue(key!, over);
        Assert.Equal(over, (bool)element.GetValue(UIElement.IsMouseOverProperty));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }

    private static Task OnSta(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(() =>
            {
                try { action(); done.TrySetResult(); } catch (Exception error) { done.TrySetException(error); } finally { dispatcher.InvokeShutdown(); }
            }); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
