using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FluentAssertions;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Themes;

/// <summary>
/// The shared ComboBox template (Themes/ModernControls.xaml, also compiled into Zenith Sessions)
/// creates item containers only for the rows its drop-down shows (QUALITY_STATUS F-4). With a
/// non-virtualizing items host, a UI Automation walk of the window or opening the drop-down created
/// one ComboBoxItem per item: 10,000 for each jump-destination list of a large macro.
/// The tests never open the Popup, because an open Popup is a real window on the desktop; they lay
/// its content out the way the open Popup does (as wide as the ComboBox, height unconstrained).
/// </summary>
public sealed class ComboBoxVirtualizationTests
{
    private const int ManyItems = 10_000;
    /// <summary>The drop-down shows about 7 rows; virtualization adds a page of cache on each side.</summary>
    private const int MaxRealized = 60;

    [Fact]
    public Task ManyItems_AutomationWalkAndOpenDropDown_CreateOnlyTheRowsThatAreShown()
        => WpfTestApplication.RunAsync(async () =>
        {
            var combo = Many(ManyItems);
            using var host = new Host(combo); await Idle();
            Realized(combo).Should().Be(0);

            var walk = ThreadCpuSample.Take();
            int peers = WalkAutomationTree(UIElementAutomationPeer.CreatePeerForElement(combo));
            var walkUse = ThreadCpuSample.Take() - walk;
            Realized(combo).Should().BeLessThanOrEqualTo(MaxRealized, "a UI Automation client walking the window must not create a row per item");
            peers.Should().BeLessThan(MaxRealized + 10);
            combo.SelectedIndex = 5_000; await Idle();
            var comboPeer = UIElementAutomationPeer.CreatePeerForElement(combo);
            var selection = (System.Windows.Automation.Provider.ISelectionProvider)comboPeer.GetPattern(PatternInterface.Selection);
            selection.GetSelection().Should().ContainSingle("UI Automation still reports the selected item while it has no row");
            Realized(combo).Should().BeLessThanOrEqualTo(MaxRealized);
            combo.SelectedIndex = -1; await Idle();

            var open = ThreadCpuSample.Take();
            var dropDown = LayOutDropDown(combo);
            var openUse = ThreadCpuSample.Take() - open;
            Realized(combo).Should().BeInRange(1, MaxRealized, "the open drop-down creates only the rows it shows and their cache");
            var scroll = Visuals<ScrollViewer>(dropDown).Single();
            scroll.CanContentScroll.Should().BeTrue();
            scroll.ExtentHeight.Should().Be(ManyItems, "the drop-down still scrolls by item, as it did before");
            scroll.ComputedVerticalScrollBarVisibility.Should().Be(Visibility.Visible);
            dropDown.DesiredSize.Height.Should().BeApproximately(264, .01, "the drop-down is still capped at 260 plus its 4 top margin");
            dropDown.DesiredSize.Width.Should().BeApproximately(combo.ActualWidth, .01);
            Console.WriteLine($"ComboBox {ManyItems:N0} items: automation walk {peers} peers, {walkUse}; drop-down layout {Realized(combo)} rows, {openUse}.");
        });

    [Fact]
    public Task OpenDropDown_WheelAndScrollBar_ReachRowsThatWereNotCreatedYet()
        => WpfTestApplication.RunAsync(async () =>
        {
            var combo = Many(ManyItems);
            using var host = new Host(combo); await Idle();
            var dropDown = LayOutDropDown(combo);
            var scroll = Visuals<ScrollViewer>(dropDown).Single();

            scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -Mouse.MouseWheelDeltaForOneLine) { RoutedEvent = UIElement.MouseWheelEvent });
            dropDown.UpdateLayout();
            scroll.VerticalOffset.Should().Be(Math.Max(1, SystemParameters.WheelScrollLines), "one wheel notch scrolls as many rows as before");

            var bar = Visuals<ScrollBar>(scroll).Single(b => b.Orientation == Orientation.Vertical);
            double before = scroll.VerticalOffset;
            ScrollBar.PageDownCommand.Execute(null, bar); dropDown.UpdateLayout();
            scroll.VerticalOffset.Should().Be(before + scroll.ViewportHeight);

            DragThumb(bar, scroll, ManyItems / 2); dropDown.UpdateLayout();
            scroll.VerticalOffset.Should().BeInRange(ManyItems * .4, ManyItems * .6, "one pixel of thumb travel is about 50 rows");
            ShouldShowRow(combo, scroll, (int)scroll.VerticalOffset);

            ScrollBar.ScrollToBottomCommand.Execute(null, bar); dropDown.UpdateLayout();
            ShouldShowRow(combo, scroll, ManyItems - 1);
            ScrollBar.ScrollToTopCommand.Execute(null, bar); dropDown.UpdateLayout();
            ShouldShowRow(combo, scroll, 0);
        });

    [Fact]
    public Task ClosedComboBox_KeysAndTypeToSelect_ReachItemsThatHaveNoRow()
        => WpfTestApplication.RunAsync(async () =>
        {
            var combo = Many(ManyItems);
            using var host = new Host(combo); await Idle();
            var steps = new List<string>();
            foreach (Key key in new[] { Key.Down, Key.Down, Key.End, Key.Up, Key.Home, Key.PageDown, Key.PageUp })
            {
                combo.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, host.Source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
                await Idle(); steps.Add($"{key}:{combo.SelectedIndex}");
            }
            foreach (string letter in new[] { "Q", "Q" })
            {
                combo.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, combo, letter))
                { RoutedEvent = TextCompositionManager.TextInputEvent });
                await Idle(); steps.Add($"{letter}:{combo.SelectedIndex}");
            }
            // Recorded with the earlier non-virtualizing host: PageUp/PageDown do nothing while closed,
            // and typing a letter again moves to the next item starting with it.
            steps.Should().Equal("Down:0", "Down:1", "End:9999", "Up:9998", "Home:0", "Next:0", "PageUp:0", "Q:16", "Q:42");
            Realized(combo).Should().Be(0);
        });

    [Fact]
    public Task ShortList_DropDownKeepsItsSize_AndShowsNoScrollBar()
        => WpfTestApplication.RunAsync(async () =>
        {
            var combo = new ComboBox { ItemsSource = new[] { "Alpha", "Beta", "Gamma" }, SelectedIndex = 0 };
            using var host = new Host(combo); await Idle();
            var dropDown = LayOutDropDown(combo);
            var scroll = Visuals<ScrollViewer>(dropDown).Single();
            scroll.ComputedVerticalScrollBarVisibility.Should().Be(Visibility.Collapsed);
            scroll.ViewportHeight.Should().Be(scroll.ExtentHeight);
            var rows = Enumerable.Range(0, 3).Select(i => (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(i)).ToList();
            rows.Should().NotContainNulls();
            // Margin 4 above the drop-down, border 1 and padding 4 on each side, then the rows.
            rows[0].TranslatePoint(new Point(), dropDown).Should().Be(new Point(5, 5));
            dropDown.DesiredSize.Height.Should().BeApproximately(4 + 2 + 8 + rows.Sum(r => r.ActualHeight), .5);
            dropDown.DesiredSize.Width.Should().BeApproximately(combo.ActualWidth, .01);
            rows.Should().OnlyContain(r => Math.Abs(r.ActualWidth - (combo.ActualWidth - 10)) < .5, "each row spans the drop-down inside its border and padding");
        });

    private static ComboBox Many(int count) => new()
    {
        ItemsSource = Enumerable.Range(0, count).Select(i => $"{(char)('A' + i % 26)} item {i:00000}").ToList()
    };

    private static int Realized(ComboBox combo)
        => Enumerable.Range(0, combo.Items.Count).Count(i => combo.ItemContainerGenerator.ContainerFromIndex(i) is not null);

    private static void ShouldShowRow(ComboBox combo, ScrollViewer scroll, int index)
    {
        combo.ItemContainerGenerator.ContainerFromIndex(index).Should().NotBeNull($"row {index} is in the viewport");
        Realized(combo).Should().BeLessThanOrEqualTo(MaxRealized, "scrolling creates rows for the new viewport, not for every item");
        scroll.VerticalOffset.Should().BeInRange(index - scroll.ViewportHeight, index + 1, "a thumb drag may stop part-way into a row");
    }

    private static FrameworkElement LayOutDropDown(ComboBox combo)
    {
        var popup = (Popup)combo.Template.FindName("Popup", combo);
        var content = (FrameworkElement)popup.Child;
        content.Measure(new Size(combo.ActualWidth, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();
        return content;
    }

    private static void DragThumb(ScrollBar bar, ScrollViewer scroll, double offset)
    {
        var track = Visuals<Track>(bar).Single();
        var thumb = track.Thumb!;
        double travel = Math.Max(1, track.ActualHeight - thumb.ActualHeight);
        double delta = (offset - bar.Value) / Math.Max(1, bar.Maximum - bar.Minimum) * travel;
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(0, delta) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(0, delta, false) { RoutedEvent = Thumb.DragCompletedEvent });
    }

    private static int WalkAutomationTree(AutomationPeer peer)
    {
        int count = 1;
        foreach (var child in peer.GetChildren() ?? []) count += WalkAutomationTree(child);
        return count;
    }

    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

    /// <summary>A hidden native window with the shared styles; nothing is shown on the desktop.</summary>
    private sealed class Host : IDisposable
    {
        private readonly HwndSource _source;
        private readonly StackPanel _root = new() { Width = 320 };
        public PresentationSource Source => _source;
        public Host(ComboBox combo)
        {
            _root.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("pack://application:,,,/MacroSuite.App;component/Themes/ModernControls.xaml", UriKind.Absolute) });
            _root.Children.Add(combo);
            _source = new HwndSource(new HwndSourceParameters("Zenith private ComboBox layout")
            { Width = 400, Height = 300, WindowStyle = unchecked((int)0x80000000), PositionX = -30000, PositionY = -30000 });
            _source.RootVisual = _root;
            _root.Measure(new Size(400, 300)); _root.Arrange(new Rect(0, 0, 400, 300)); _root.UpdateLayout();
        }
        public void Dispose() { _source.RootVisual = null; _source.Dispose(); }
    }
}
