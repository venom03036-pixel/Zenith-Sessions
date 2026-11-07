using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MacroSuite.App.Themes;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

/// <summary>Actual dialog content rendered offscreen; no native window is shown or controlled.</summary>
[Collection(WpfProductionResourceCollection.Name)]
public sealed class GroupManagerThemeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-group-theme-ui-" + Guid.NewGuid().ToString("N"));
    public static IEnumerable<object[]> PalettesAndScales()
    {
        foreach (string theme in new[] { "graphite-mint", "midnight", "amber-night", "ocean-blue", "violet-dusk", "custom-light" })
            foreach (double scale in new[] { 1d, 1.25d, 1.5d, 2d }) yield return [theme, scale];
    }
    [Theory]
    [MemberData(nameof(PalettesAndScales))]
    public Task RealDialogTabsAndSelectedRowsRenderReadableThemeColorsWithoutOsWhiteChrome(string theme, double scale) => OnSta(async () =>
    {
        await using var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor());
        await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice);
        var catalog = await shell.ReadGroupCatalogAsync(new(), default);
        await shell.CreateGroupAsync("Readable group", "#112233", catalog.CatalogRevision, default);
        catalog = await shell.ReadGroupCatalogAsync(new(), default);
        await shell.CreateGroupAsync("Second group", null, catalog.CatalogRevision, default);
        var dialog = new AccountGroupManagerDialog(shell); var model = Assert.IsType<AccountGroupManagerViewModel>(dialog.DataContext);
        Border? host = null;
        try
        {
            var resources = Palette(theme); dialog.Resources.MergedDictionaries.Add(resources);
            model.RefreshCatalog.Execute(null); await model.RefreshCatalog.Execution.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, model.CatalogGroups.Count);
            // Hidden Window skips normal child rendering. Exercise its exact production
            // content in an unshown layout host with the same model and resource scope.
            var content = Assert.IsAssignableFrom<FrameworkElement>(dialog.Content); dialog.Content = null;
            host = new Border { Child = content, DataContext = model, Resources = dialog.Resources };
            host.SetResourceReference(Border.BackgroundProperty, "CardBackground"); Layout(host);
            var tabs = Assert.Single(Visual(host).OfType<TabControl>()); var headers = tabs.Items.Cast<TabItem>().ToArray();
            Assert.Equal(new[] { "Groups and colors", "Rename or remove" }, headers.Select(item => item.Header));
            var surface = Assert.IsType<Border>(tabs.Template.FindName("GroupContentSurface", tabs));
            Assert.Equal(ColorOf(resources["CardBackground"]), ColorOf(surface.Background));
            var list = Assert.Single(Visual(host).OfType<ListBox>()); Assert.True(list.IsEnabled); list.SelectedIndex = 1; Layout(host);
            var selected = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(1));
            var ordinary = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
            CheckRow(selected, selected: true, resources); CheckRow(ordinary, selected: false, resources);
            Assert.Equal("Second group", AutomationProperties.GetName(selected)); Assert.DoesNotContain(model.SelectedGroup!.Id.ToString(), AutomationProperties.GetName(selected));
            var selectedRow = Assert.IsType<Border>(selected.Template.FindName("GroupRowChrome", selected));
            var selectedName = Visual(selected).OfType<TextBlock>().Single(text => text.Text == "Second group");
            var image = new RenderTargetBitmap((int)Math.Ceiling(620 * scale), (int)Math.Ceiling(600 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            image.Render(host);
            Assert.Equal(ColorOf(resources["CardBackground"]), Sample(image, host, surface, new Point(8, 4), scale));
            Assert.Equal(ColorOf(resources["SelectionBackground"]), Sample(image, host, selectedRow, new Point(4, 4), scale));
            AssertGlyphs(image, host, selectedName, ColorOf(selectedRow.Background), scale);
            foreach (var header in headers) CheckHeader(header, resources, host, image, scale);
            var close = Logical(content).OfType<Button>().Single(button => Equals(button.Content, "Close")); var closeBounds = Bounds(host, close);
            Reachable(host, close); tabs.SelectedIndex = 1; Layout(host); image.Clear(); image.Render(host);
            foreach (var header in headers) CheckHeader(header, resources, host, image, scale);
            Assert.Equal(ColorOf(resources["CardBackground"]), Sample(image, host, surface, new Point(8, 4), scale));
            Reachable(host, close); Assert.Equal(closeBounds.Bottom, Bounds(host, close).Bottom);
        }
        finally
        {
            if (host is not null) host.Child = null;
            await model.DisposeAsync(); dialog.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    });
    private static void CheckRow(ListBoxItem row, bool selected, ResourceDictionary resources)
    {
        Color background = ColorOf(resources[selected ? "SelectionBackground" : "SurfaceBackground"]);
        Color foreground = ColorOf(resources[selected ? "SelectionForeground" : "TextPrimary"]);
        var chrome = Assert.IsType<Border>(row.Template.FindName("GroupRowChrome", row));
        Assert.Equal(background, ColorOf(chrome.Background)); Assert.Equal(foreground, ColorOf(row.Foreground)); Assert.True(row.ActualHeight >= 36);
        if (selected) Assert.Equal(ColorOf(resources["SelectionBorder"]), ColorOf(chrome.BorderBrush));
        foreach (var text in Visual(row).OfType<TextBlock>())
        { Assert.Equal(foreground, ColorOf(text.Foreground)); Assert.True(Contrast(ColorOf(text.Foreground), background) >= 4.5); }
    }
    private static void CheckHeader(TabItem item, ResourceDictionary resources, FrameworkElement host, RenderTargetBitmap image, double scale)
    {
        var chrome = Assert.IsType<Border>(item.Template.FindName("GroupTabHeaderChrome", item));
        var text = Assert.IsType<TextBlock>(item.Template.FindName("GroupTabHeaderText", item));
        Assert.Equal(item.Header, text.Text); Assert.True(item.ActualHeight >= 36); Assert.True(text.ActualWidth > 0 && text.ActualHeight > 0);
        Color background = ColorOf(resources[item.IsSelected ? "SelectionBackground" : "CardBackground"]);
        Color foreground = ColorOf(resources[item.IsSelected ? "SelectionForeground" : "TextPrimary"]);
        Assert.Equal(background, ColorOf(chrome.Background)); Assert.Equal(foreground, ColorOf(text.Foreground)); Assert.True(Contrast(foreground, background) >= 4.5);
        Assert.Equal(background, Sample(image, host, chrome, new Point(8, 4), scale)); AssertGlyphs(image, host, text, background, scale);
    }
    private static ResourceDictionary Palette(string theme)
    {
        var resources = Assert.IsType<ResourceDictionary>(Application.LoadComponent(new Uri("/Zenith.Sessions.App;component/Themes/SessionsControls.xaml", UriKind.Relative)));
        ThemeManager.Apply(resources, theme == "custom-light" ? "graphite-mint" : theme);
        if (theme == "custom-light")
        {
            foreach (var pair in new Dictionary<string, string>
            {
                ["CardBackground"] = "#FAFAFA", ["SurfaceBackground"] = "#FFFFFF", ["TextPrimary"] = "#151515", ["TextSecondary"] = "#454545",
                ["ControlBorder"] = "#666666", ["SelectionBackground"] = "#17324D", ["SelectionForeground"] = "#FFFFFF", ["SelectionBorder"] = "#005A9E", ["HoverBackground"] = "#E5EEF7"
            }) resources[pair.Key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
        }
        return resources;
    }
    private static Color Sample(BitmapSource image, FrameworkElement host, FrameworkElement element, Point point, double scale)
    {
        Point pixel = element.TransformToAncestor(host).Transform(point); byte[] value = new byte[4];
        image.CopyPixels(new Int32Rect((int)Math.Floor(pixel.X * scale), (int)Math.Floor(pixel.Y * scale), 1, 1), value, 4, 0);
        return Color.FromArgb(value[3], value[2], value[1], value[0]);
    }
    private static void AssertGlyphs(BitmapSource image, FrameworkElement host, FrameworkElement text, Color background, double scale)
    {
        Rect bounds = Bounds(host, text); int left = Math.Max(0, (int)Math.Floor(bounds.Left * scale)), top = Math.Max(0, (int)Math.Floor(bounds.Top * scale));
        int width = Math.Min(image.PixelWidth - left, Math.Max(1, (int)Math.Ceiling(bounds.Width * scale))), height = Math.Min(image.PixelHeight - top, Math.Max(1, (int)Math.Ceiling(bounds.Height * scale)));
        byte[] pixels = new byte[width * height * 4]; image.CopyPixels(new Int32Rect(left, top, width, height), pixels, width * 4, 0);
        int ink = 0; for (int index = 0; index < pixels.Length; index += 4)
            if (Math.Abs(pixels[index] - background.B) + Math.Abs(pixels[index + 1] - background.G) + Math.Abs(pixels[index + 2] - background.R) > 30) ink++;
        Assert.True(ink > 10, "The actual header/name must render readable glyphs, not only expose its text property.");
    }
    private static double Contrast(Color left, Color right)
    {
        static double Channel(byte value) { double scaled = value / 255d; return scaled <= .04045 ? scaled / 12.92 : Math.Pow((scaled + .055) / 1.055, 2.4); }
        static double Luminance(Color color) => .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        double a = Luminance(left), b = Luminance(right); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    private static Color ColorOf(object brush) => Assert.IsType<SolidColorBrush>(brush).Color;
    private static Rect Bounds(FrameworkElement host, FrameworkElement child) => child.TransformToAncestor(host).TransformBounds(new Rect(child.RenderSize));
    private static void Reachable(FrameworkElement host, FrameworkElement child)
    { Rect bounds = Bounds(host, child); Assert.True(bounds.Width > 0 && bounds.Height > 0); Assert.InRange(bounds.Left, 0, host.ActualWidth); Assert.InRange(bounds.Right, 1, host.ActualWidth + .5); Assert.InRange(bounds.Bottom, 1, host.ActualHeight + .5); }
    private static void Layout(FrameworkElement host)
    { host.Measure(new Size(620, 600)); host.Arrange(new Rect(0, 0, 620, 600)); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind); host.UpdateLayout(); }
    private static IEnumerable<DependencyObject> Visual(DependencyObject root)
    {
        yield return root; for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var child in Visual(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        yield return root; foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var nested in Logical(child)) yield return nested;
    }
    private sealed class Monitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 10), [])); }
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => throw new InvalidOperationException("No native action expected.");
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => throw new InvalidOperationException("No native action expected.");
    }
    private static Task OnSta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); done.TrySetResult(); } catch (Exception error) { done.TrySetException(error); } finally { dispatcher.InvokeShutdown(); }
            }); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-group-theme-ui-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected theme fixture path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
