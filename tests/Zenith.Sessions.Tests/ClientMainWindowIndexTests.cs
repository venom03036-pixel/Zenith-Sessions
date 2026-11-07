using System.Diagnostics;
using System.IO;
using System.Windows.Interop;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class ClientMainWindowIndexTests
{
    /// <summary>A synthetic desktop: entries are top-level windows in Z order. No real window is touched.</summary>
    private sealed class FakeWindows(params (nint Window, int ProcessId, bool Owned, bool Visible)[] zOrder) : ITopLevelWindows
    {
        public int Enumerations, Visited;
        public Action? AfterVisit;
        public readonly List<nint> AttributeReads = [];
        public void Enumerate(ITopLevelWindowVisitor visitor)
        {
            Enumerations++;
            foreach (var entry in zOrder)
            {
                Visited++;
                if (!visitor.Visit(entry.Window, entry.ProcessId)) return;
                AfterVisit?.Invoke();
            }
        }
        public bool IsUnownedAndVisible(nint window)
        {
            AttributeReads.Add(window);
            foreach (var entry in zOrder) if (entry.Window == window) return !entry.Owned && entry.Visible;
            return false;
        }
    }

    [Fact]
    public void MainWindowIndexEnumeratesOnceAndMatchesMainWindowFinderRules()
    {
        int[] clients = Enumerable.Range(1001, 16).ToArray();
        var desktop = new List<(nint, int, bool, bool)>();
        foreach (int client in clients)
        {
            nint baseHandle = client * 16;
            desktop.Add((0x100000 + client, 5000 + client, false, true)); // another process's main window
            desktop.Add((baseHandle + 1, client, true, true));         // owned dialog or splash: never the main window
            desktop.Add((baseHandle + 2, client, false, false));       // hidden unowned window: never the main window
            if (client == 1016) continue;                              // this client has no qualifying window
            desktop.Add((baseHandle + 3, client, false, true));        // first unowned visible window: the main window
            desktop.Add((baseHandle + 4, client, false, true));        // a later one must not replace it
        }
        var windows = new FakeWindows(desktop.ToArray());
        var index = new ClientMainWindowIndex(windows);

        index.Build(clients, default);

        Assert.Equal(1, windows.Enumerations);
        foreach (int client in clients.Where(client => client != 1016)) Assert.Equal((nint)(client * 16 + 3), index.MainWindow(client));
        Assert.Equal((nint)0, index.MainWindow(1016));
        Assert.Equal((nint)0, index.MainWindow(5000 + 1001));
        // Window attributes are read only for client-owned windows, never for every window on the desktop.
        Assert.NotEmpty(windows.AttributeReads);
        Assert.All(windows.AttributeReads, window => Assert.Contains((int)(window / 16), clients));
        Assert.DoesNotContain((nint)(1001 * 16 + 4), windows.AttributeReads);

        // No clients: no window pass at all, and the previous sample's windows are not reused.
        index.Build([], default);
        Assert.Equal(1, windows.Enumerations);
        Assert.Equal((nint)0, index.MainWindow(1001));
    }

    [Fact]
    public void MainWindowIndexStopsOnceEveryClientHasItsWindowAndHonoursCancellation()
    {
        var desktop = new List<(nint, int, bool, bool)> { (10, 7, false, true), (11, 8, false, true) };
        for (int other = 0; other < 100; other++) desktop.Add((100 + other, 9000 + other, false, true));
        var windows = new FakeWindows(desktop.ToArray());
        var index = new ClientMainWindowIndex(windows);

        index.Build([7, 8], default);

        Assert.Equal(2, windows.Visited);
        Assert.Equal((nint)10, index.MainWindow(7)); Assert.Equal((nint)11, index.MainWindow(8));

        // Cancellation stops the pass at the next window and leaves no partial result behind.
        using var stop = new CancellationTokenSource();
        var canceling = new FakeWindows(desktop.ToArray()) { AfterVisit = stop.Cancel };
        var canceled = new ClientMainWindowIndex(canceling);
        Assert.Throws<OperationCanceledException>(() => canceled.Build([7, 8, 4242], stop.Token));
        Assert.Equal(2, canceling.Visited);
        Assert.Equal((nint)0, canceled.MainWindow(7));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    public async Task SampleProcessesUsesOneWindowEnumerationPerTick(int selfCopies)
    {
        // Stand-in clients: copies of this test process under the first client name, plus one child process of
        // ours under the second. Each is read through the native one-handle reader. Their windows come from the
        // synthetic desktop. No Roblox client is involved.
        using var child = StartIdleChild();
        try
        {
            int self = Environment.ProcessId;
            nint selfWindow = 0x7A10, childWindow = 0x7A20;
            var windows = new FakeWindows((0x7A00, child.Id, true, true), (selfWindow, self, false, true),
                (0x7A11, self, false, true), (childWindow, child.Id, false, true));
            var native = new NativeClientProcessSource("RobloxPlayerBeta", "RobloxPlayer", "Roblox");
            var source = new FakeClientProcessSource { Reader = native.Read };
            for (int copy = 0; copy < selfCopies; copy++) source.Clients.Add(new(self, 0, 1, 1, 1));
            source.Clients.Add(new(child.Id, 1, 1, 1, 1));
            using var monitor = new WindowsDesktopMonitor(TimeProvider.System, null, source, windows);

            var first = monitor.SampleProcesses(default);

            Assert.Equal(1, windows.Enumerations);
            Assert.Equal(selfCopies + 1, first.Sessions.Count);
            Assert.All(first.Sessions, row => Assert.Null(row.Problem));
            Assert.All(first.Sessions, row => Assert.Equal(row.ProcessId == self ? selfWindow : childWindow, row.Window));

            monitor.SampleProcesses(default);
            Assert.Equal(2, windows.Enumerations);
        }
        finally { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public void SampleProcessesWithoutClientsScansNoWindows()
    {
        var windows = new FakeWindows((0x7A10, Environment.ProcessId, false, true));
        using var monitor = new WindowsDesktopMonitor(TimeProvider.System, null, new FakeClientProcessSource(), windows);
        Assert.Empty(monitor.SampleProcesses(default).Sessions);
        Assert.Equal(0, windows.Enumerations);
    }

    [Fact]
    public async Task NativeIndexChoosesTheSameMainWindowAsProcessMainWindowHandle()
    {
        var created = new TaskCompletionSource<(nint Main, nint Owned, nint Hidden, Dispatcher Dispatcher)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                // Test-owned 1x1 tool windows far off screen. WS_EX_NOACTIVATE: they never take focus or show on the taskbar.
                const int Popup = unchecked((int)0x80000000), Visible = 0x10000000, ToolNoActivate = 0x80 | 0x08000000;
                using var main = new HwndSource(new HwndSourceParameters("zenith-main-window-fixture", 1, 1)
                { WindowStyle = Popup | Visible, ExtendedWindowStyle = ToolNoActivate, PositionX = -32000, PositionY = -32000 });
                using var owned = new HwndSource(new HwndSourceParameters("zenith-owned-window-fixture", 1, 1)
                { WindowStyle = Popup | Visible, ExtendedWindowStyle = ToolNoActivate, PositionX = -32000, PositionY = -32000, ParentWindow = main.Handle });
                using var hidden = new HwndSource(new HwndSourceParameters("zenith-hidden-window-fixture", 1, 1)
                { WindowStyle = Popup, ExtendedWindowStyle = ToolNoActivate, PositionX = -32000, PositionY = -32000 });
                created.TrySetResult((main.Handle, owned.Handle, hidden.Handle, Dispatcher.CurrentDispatcher));
                Dispatcher.Run();
                exited.TrySetResult();
            }
            catch (Exception error) { created.TrySetException(error); exited.TrySetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        var fixture = await created.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var windows = NativeTopLevelWindows.Instance;
            Assert.True(windows.IsUnownedAndVisible(fixture.Main));
            Assert.False(windows.IsUnownedAndVisible(fixture.Owned));
            Assert.False(windows.IsUnownedAndVisible(fixture.Hidden));

            int self = Environment.ProcessId;
            var index = new ClientMainWindowIndex(windows);
            nint chosen = 0, expected = -1;
            // Other test classes may open or close windows in this process between the two reads; a repeat settles it.
            for (int attempt = 0; attempt < 10 && chosen != expected; attempt++)
            {
                index.Build([self], default);
                chosen = index.MainWindow(self);
                using var process = Process.GetProcessById(self);
                expected = process.MainWindowHandle;
            }
            Assert.NotEqual((nint)0, chosen);
            Assert.Equal(expected, chosen);
        }
        finally
        {
            fixture.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static Process StartIdleChild()
    {
        // cmd.exe waits on its redirected input until the test kills it; it opens no window.
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /q /k")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        return Process.Start(start) ?? throw new InvalidOperationException("The idle child process did not start.");
    }
}
