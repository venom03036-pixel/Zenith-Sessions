using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// Changing page names it in the window title (taskbar and Alt-Tab) and tells screen readers which page opened, because a
// shortcut such as Ctrl+2 changes the page without moving keyboard focus.
public sealed class PageTitleTests : IDisposable
{
    // Every page the Navigate command accepts, with the name the sidebar and buttons show for it.
    private static readonly (string Tab, string Page)[] Pages =
    [
        ("Accounts", "Accounts"), ("Sessions", "Sessions"), ("JoinGame", "Join a game"), ("Windows", "Windows"), ("Automation", "Automation"),
        ("Diagnostics", "Diagnostics"), ("Settings", "Settings"), ("Guide", "Guide"), ("Overview", "Overview"),
    ];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-page-title-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public Task EveryPageNamesItselfInTheWindowTitleBeforeTheManagerName() => OnSta(async () =>
    {
        await using var vm = new ShellViewModel(Path.Combine(_root, "data"), false, Dispatcher.CurrentDispatcher, new SyntheticMonitor());
        await vm.InitializeAsync();
        Assert.Equal("Overview", vm.PageName);
        Assert.Equal("Overview · " + vm.InstanceTitle, vm.WindowTitle);
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(ShellViewModel.PageName) or nameof(ShellViewModel.WindowTitle)) raised.Add(e.PropertyName); };
        foreach (var (tab, page) in Pages)
        {
            raised.Clear();
            vm.Navigate.Execute(tab);
            Assert.Equal(page, vm.PageName);
            // The page leads; the manager name (Personal, Sample preview or Library <folder>) stays so the taskbar still says which manager this is.
            Assert.Equal(page + " · " + vm.InstanceTitle, vm.WindowTitle);
            Assert.Equal([nameof(ShellViewModel.PageName), nameof(ShellViewModel.WindowTitle)], raised);
        }
        // Opening the page that is already open changes nothing, so it is not announced again.
        raised.Clear();
        vm.Navigate.Execute("Overview");
        Assert.Empty(raised);
        // A page opened by another command, not the sidebar, is named the same way.
        vm.ShowWindowsOverview.Execute(null);
        Assert.Equal("Windows · " + vm.InstanceTitle, vm.WindowTitle);
        Assert.Equal([nameof(ShellViewModel.PageName), nameof(ShellViewModel.WindowTitle)], raised);
    });

    [Fact]
    public Task APageChangeIsAnnouncedThroughTheWindowsAutomationPeer() => OnSta(() =>
    {
        var window = new Window();
        try
        {
            Assert.Null(UIElementAutomationPeer.FromElement(window));
            MainWindow.AnnouncePage(window, "Join a game");
            Assert.IsType<WindowAutomationPeer>(UIElementAutomationPeer.FromElement(window));
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

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
            || !Path.GetFileName(full).StartsWith("zenith-page-title-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
