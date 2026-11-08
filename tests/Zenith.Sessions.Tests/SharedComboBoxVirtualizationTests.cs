using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MacroSuite.App.Themes;
using Xunit;

namespace Zenith.Sessions.Tests;

/// <summary>
/// Zenith Sessions compiles the shared ComboBox template from MacroSuite.App (QUALITY_STATUS F-4):
/// its drop-down creates rows only for what it shows, so a long choice list or a UI Automation
/// client walking the window does not create one ComboBoxItem per item. The Popup is never opened
/// (it would be a real desktop window); its content is laid out as the open Popup lays it out.
/// </summary>
[Collection(WpfProductionResourceCollection.Name)]
public sealed class SharedComboBoxVirtualizationTests
{
    [Fact]
    public Task SharedHostStyle_LongList_CreatesOnlyTheRowsItsDropDownShows() => OnSta(() =>
    {
        var resources = Resources();
        var combo = new ComboBox
        {
            Resources = resources, Style = (Style)resources[typeof(ComboBox)],
            ItemsSource = Enumerable.Range(0, 5_000).Select(i => $"Choice {i}").ToList(), SelectedIndex = 0
        };
        combo.ApplyTemplate(); combo.Measure(new Size(320, 40)); combo.Arrange(new Rect(0, 0, 320, 40)); combo.UpdateLayout();
        Assert.Equal(0, Realized(combo));
        Walk(UIElementAutomationPeer.CreatePeerForElement(combo));
        Assert.InRange(Realized(combo), 0, 60);

        var popup = Assert.IsType<Popup>(combo.Template.FindName("Popup", combo));
        var dropDown = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
        dropDown.Measure(new Size(320, double.PositiveInfinity)); dropDown.Arrange(new Rect(dropDown.DesiredSize)); dropDown.UpdateLayout();
        Assert.InRange(Realized(combo), 1, 60);
        var scroll = Assert.Single(Visuals<ScrollViewer>(dropDown));
        Assert.True(scroll.CanContentScroll); Assert.Equal(5_000, scroll.ExtentHeight);
        Assert.Equal(264, dropDown.DesiredSize.Height, 2);
        Assert.False(popup.IsOpen);
        return Task.CompletedTask;
    });

    [Fact]
    public Task EditableCompanionStyle_KeepsItsOwnVirtualizingDropDown() => OnSta(() =>
    {
        var resources = Resources();
        var combo = new ComboBox
        {
            Resources = resources, Style = (Style)resources["SessionsEditableComboBox"], IsEditable = true,
            ItemsSource = Enumerable.Range(0, 5_000).Select(i => $"Group {i}").ToList(), MaxDropDownHeight = 220
        };
        combo.ApplyTemplate(); combo.Measure(new Size(360, 40)); combo.Arrange(new Rect(0, 0, 360, 40)); combo.UpdateLayout();
        var popup = Assert.IsType<Popup>(combo.Template.FindName("PART_Popup", combo));
        var dropDown = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
        dropDown.Measure(new Size(360, double.PositiveInfinity)); dropDown.Arrange(new Rect(dropDown.DesiredSize)); dropDown.UpdateLayout();
        Assert.InRange(Realized(combo), 1, 60);
        Assert.Equal(ScrollUnit.Item, VirtualizingPanel.GetScrollUnit(combo));
        Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(combo));
        return Task.CompletedTask;
    });

    private static int Realized(ComboBox combo)
        => Enumerable.Range(0, combo.Items.Count).Count(i => combo.ItemContainerGenerator.ContainerFromIndex(i) is not null);
    private static void Walk(AutomationPeer peer) { foreach (var child in peer.GetChildren() ?? []) Walk(child); }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static ResourceDictionary Resources()
    {
        var resources = Assert.IsType<ResourceDictionary>(Application.LoadComponent(
            new Uri("/Zenith.Sessions.App;component/Themes/SessionsControls.xaml", UriKind.Relative)));
        ThemeManager.Apply(resources, "graphite-mint"); return resources;
    }
    private static Task OnSta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
