using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using MacroSuite.App.Themes;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// Footer notices carry severity: a failure is an error that stays visible until it is dismissed, a later notice replaces
// only the status line, and screen readers are told about both (the error assertively, the status politely).
[Collection(WpfProductionResourceCollection.Name)]
public sealed class NoticeSeverityTests : IDisposable
{
    private const string Secret = "PRIVATE-FIXTURE-TOKEN-https://example.invalid/share?code=SECRET123";
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation", X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-notice-severity-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "data");

    [Fact]
    public Task AnErrorStaysVisibleThroughLaterNoticesUntilItIsDismissed() => OnSta(async () =>
    {
        await using var vm = await ReadyShell();
        string ready = vm.Notice;
        vm.Fail(new IOException(Secret), Zenith.Sessions.Windows.ManagerSubsystem.Library);
        Assert.True(vm.HasErrorNotice, "A failure was not shown as an error.");
        Assert.Contains("could not finish", vm.ErrorNotice);
        Assert.DoesNotContain("SECRET123", vm.ErrorNotice);
        Assert.Equal(ready, vm.Notice);

        vm.Inform("Visible diagnostic events copied.");
        Assert.Equal("Visible diagnostic events copied.", vm.Notice);
        Assert.True(vm.ErrorNotice.Contains("could not finish", StringComparison.Ordinal), "A later notice hid the error before it was dismissed.");

        // A newer failure replaces the error with the plain message its caller chose; the exception text is never shown.
        vm.Fail(new InvalidOperationException(Secret), Zenith.Sessions.Windows.ManagerSubsystem.Diagnostics, "The clipboard is busy or unavailable. Try copying again.");
        Assert.Equal("The clipboard is busy or unavailable. Try copying again.", vm.ErrorNotice);
        Assert.Equal("Visible diagnostic events copied.", vm.Notice);

        vm.DismissError.Execute(null);
        Assert.False(vm.HasErrorNotice, "Dismiss left the error showing.");
        Assert.Equal("", vm.ErrorNotice);
        Assert.Equal("Visible diagnostic events copied.", vm.Notice);
        // Each failure is still recorded by type only.
        Assert.Equal(2, vm.Events.Count(e => e.Contains("Operation failed", StringComparison.Ordinal)));
        Assert.DoesNotContain(vm.Events, e => e.Contains("SECRET123", StringComparison.Ordinal));
    });

    [Fact]
    public Task AnUnexpectedFaultIsAnErrorRepeatedNoticesAreRaisedAgainAndPanicAndLockClearTheError() => OnSta(async () =>
    {
        await using var vm = await ReadyShell();
        vm.ContainUnhandled(typeof(InvalidOperationException));
        Assert.True(vm.ErrorNotice.Contains("unexpected error", StringComparison.Ordinal), "An unexpected fault was not shown as an error.");
        vm.Inform("Settings saved.");
        Assert.Contains("unexpected error", vm.ErrorNotice);

        // The same text again is raised again, so the footer's live regions announce a repeated copy or failure.
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.Inform("Settings saved.");
        vm.Fail(new IOException(Secret), Zenith.Sessions.Windows.ManagerSubsystem.Library); vm.Fail(new IOException(Secret), Zenith.Sessions.Windows.ManagerSubsystem.Library);
        Assert.Equal(1, raised.Count(name => name == nameof(ShellViewModel.Notice)));
        Assert.Equal(2, raised.Count(name => name == nameof(ShellViewModel.ErrorNotice)));

        vm.Panic.Execute(null);
        Assert.False(vm.HasErrorNotice, "Panic left an earlier error showing beside its stop notice.");
        Assert.Contains("Panic", vm.Notice);
        // A failure after Panic is shown as an error and does not replace the Panic notice.
        vm.Fail(new IOException(Secret), Zenith.Sessions.Windows.ManagerSubsystem.Library);
        Assert.True(vm.HasErrorNotice); Assert.Contains("Panic", vm.Notice);
        // So is an unexpected fault caught by the last-chance guard after Panic.
        vm.DismissError.Execute(null);
        vm.ContainUnhandled(typeof(InvalidOperationException));
        Assert.True(vm.ErrorNotice.Contains("unexpected error", StringComparison.Ordinal), "An unexpected fault after Panic showed no error.");
        Assert.Contains("Panic", vm.Notice);

        vm.Lock.Execute(null);
        Assert.False(vm.HasErrorNotice, "Lock left an error showing behind the locked dashboard.");
        Assert.Contains("locked", vm.Notice, StringComparison.OrdinalIgnoreCase);
        // Once unlocked, a later unexpected fault is shown as an error again.
        vm.Unlock.Execute(null); await vm.Unlock.Execution;
        vm.ContainUnhandled(typeof(InvalidOperationException));
        Assert.True(vm.ErrorNotice.Contains("unexpected error", StringComparison.Ordinal), "An unexpected fault after Lock and Unlock showed no error.");
    });

    [Fact]
    public Task AFaultWhileLockedIsWaitingAsAnErrorWhenTheDashboardIsUnlocked() => OnSta(async () =>
    {
        await using var vm = await ReadyShell();
        // An ordinary failure while locked (the idle lock, for example) is still the error when the user unlocks.
        vm.Lock.Execute(null);
        vm.Fail(new IOException(Secret), Zenith.Sessions.Windows.ManagerSubsystem.Library);
        vm.Unlock.Execute(null); await vm.Unlock.Execution;
        Assert.True(vm.ErrorNotice.Contains("could not finish", StringComparison.Ordinal), "A failure while locked was gone after Unlock.");
        vm.DismissError.Execute(null);

        // An unexpected fault caught by the last-chance guard while locked is treated the same way, so the user is told to restart.
        vm.Lock.Execute(null);
        vm.ContainUnhandled(typeof(InvalidOperationException));
        Assert.Contains(vm.Events, e => e.Contains("Unexpected error caught (InvalidOperationException)", StringComparison.Ordinal));
        vm.Unlock.Execute(null); await vm.Unlock.Execution;
        Assert.True(vm.ErrorNotice.Contains("unexpected error", StringComparison.Ordinal), "An unexpected fault while locked showed no error after Unlock.");
        Assert.Contains("restart the app", vm.ErrorNotice);
        Assert.Contains("unlocked", vm.Notice);
        Assert.DoesNotContain(vm.Events, e => e.Contains("SECRET123", StringComparison.Ordinal));
    });

    [Fact]
    public void TheWindowAnnouncesEachUpdateOfBothFooterNotices()
    {
        var notices = ProductionFooterNotices();
        foreach (string path in new[] { nameof(ShellViewModel.ErrorNotice), nameof(ShellViewModel.Notice) })
        {
            var text = notices.Descendants(Ui + "TextBlock").SingleOrDefault(t => ((string?)t.Attribute("Text"))?.StartsWith("{Binding " + path + ",", StringComparison.Ordinal) == true);
            Assert.True(text is not null, $"The footer has no text bound to {path}.");
            Assert.Equal("AnnounceNotice", (string?)text!.Attribute("Binding.TargetUpdated"));
        }
        Assert.NotNull(typeof(MainWindow).GetMethod("AnnounceNotice", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(object), typeof(DataTransferEventArgs)]));
    }

    [Fact]
    public Task TheFooterShowsTheErrorInDangerColourWithDismissAndBothNoticesAreLiveRegions() => OnSta(async () =>
    {
        await using var vm = await ReadyShell();
        var window = ComposeFooter(vm, out var panel);
        try
        {
            var error = BoundText(panel, nameof(ShellViewModel.ErrorNotice));
            var status = BoundText(panel, nameof(ShellViewModel.Notice));
            Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(error));
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
            Assert.True(BindingOperations.GetBinding(error, TextBlock.TextProperty)!.NotifyOnTargetUpdated);
            Assert.True(BindingOperations.GetBinding(status, TextBlock.TextProperty)!.NotifyOnTargetUpdated);
            var dismiss = Visual(panel).OfType<Button>().Single(b => BindingOperations.GetBinding(b, ButtonBase.CommandProperty)?.Path?.Path == nameof(ShellViewModel.DismissError));
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(dismiss)));

            Layout(panel);
            Assert.False(Shown(panel, error)); Assert.False(Shown(panel, dismiss));
            Assert.Equal(vm.Notice, status.Text); Assert.True(Shown(panel, status));

            vm.Fail(new IOException(Secret), Zenith.Sessions.Windows.ManagerSubsystem.Library); Layout(panel);
            Assert.True(Shown(panel, error), "The footer does not show the error.");
            Assert.True(Shown(panel, dismiss));
            Assert.Equal(vm.ErrorNotice, error.Text);
            Assert.Equal("Error: " + vm.ErrorNotice, AutomationProperties.GetName(error));
            Assert.Equal(((SolidColorBrush)window.Resources["AccentDanger"]).Color, ((SolidColorBrush)error.Foreground).Color);

            vm.Inform("Settings saved."); Layout(panel);
            Assert.True(Shown(panel, error), "A later notice hid the error in the footer.");
            Assert.Equal("Settings saved.", status.Text);
            int updates = 0;
            status.AddHandler(Binding.TargetUpdatedEvent, new EventHandler<DataTransferEventArgs>((_, e) => { if (e.Property == TextBlock.TextProperty) updates++; }));
            vm.Inform("Settings saved.");
            Assert.Equal(1, updates);

            dismiss.Command.Execute(dismiss.CommandParameter); Layout(panel);
            Assert.False(Shown(panel, error)); Assert.False(Shown(panel, dismiss));
            Assert.Equal("Settings saved.", status.Text);

            // The window raises LiveRegionChanged through the element's automation peer; empty text is not announced.
            MainWindow.AnnounceLiveRegion(error);
            Assert.Null(UIElementAutomationPeer.FromElement(error));
            MainWindow.AnnounceLiveRegion(status);
            Assert.NotNull(UIElementAutomationPeer.FromElement(status));
        }
        finally { window.Content = null; window.Close(); }
    });

    private async Task<ShellViewModel> ReadyShell()
    {
        var vm = new ShellViewModel(Data, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor());
        await vm.InitializeAsync();
        return vm;
    }

    private static XElement ProductionFooterNotices()
    {
        using var stream = typeof(NoticeSeverityTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        var notices = XDocument.Load(stream).Descendants().SingleOrDefault(e => (string?)e.Attribute(X + "Name") == "FooterNotices");
        Assert.True(notices is not null, "The footer has no separate error and status notices.");
        return new XElement(notices!);
    }

    private static Window ComposeFooter(ShellViewModel vm, out FrameworkElement panel)
    {
        _ = Application.LoadComponent(new Uri("/Zenith.Sessions.App;component/Themes/SessionsControls.xaml", UriKind.Relative));
        var notices = ProductionFooterNotices();
        // The window's own announcement handler is code-behind, which loose XAML cannot reach.
        foreach (var handler in notices.DescendantsAndSelf().Attributes("Binding.TargetUpdated").ToArray()) handler.Remove();
        var resources = new XElement(Ui + "ResourceDictionary",
            new XElement(Ui + "ResourceDictionary.MergedDictionaries", new XElement(Ui + "ResourceDictionary", new XAttribute("Source", "/Zenith.Sessions.App;component/Themes/SessionsControls.xaml"))),
            new XElement(Ui + "BooleanToVisibilityConverter", new XAttribute(X + "Key", "VisibleIf")));
        var root = new XElement(Ui + "Window", new XAttribute(XNamespace.Xmlns + "x", X.NamespaceName), new XElement(Ui + "Window.Resources", resources), notices);
        var window = (Window)XamlReader.Parse(root.ToString());
        ThemeManager.Apply(window.Resources, "graphite-mint");
        window.DataContext = vm;
        panel = (FrameworkElement)window.Content;
        return window;
    }

    private static TextBlock BoundText(DependencyObject root, string path) =>
        Visual(root).OfType<TextBlock>().Single(text => BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == path);

    private static bool Shown(FrameworkElement root, FrameworkElement element)
    {
        for (DependencyObject? current = element; current is not null && current != root; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
        return element.ActualWidth > 0 && element.ActualHeight > 0;
    }

    private static IEnumerable<DependencyObject> Visual(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Visual(child)) yield return nested; }
    }

    private static void Layout(FrameworkElement view)
    {
        view.Measure(new Size(900, 40)); view.Arrange(new Rect(0, 0, 900, 40));
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); view.UpdateLayout();
    }

    private sealed class SyntheticMonitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 20), [])); }
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
    }

    private static Task OnSta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () => { try { await action(); done.TrySetResult(); } catch (Exception error) { done.TrySetException(error); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-notice-severity-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
