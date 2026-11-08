using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class ClientResponsivenessTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly long Start = At.AddMinutes(-5).UtcTicks;

    private static SessionObservation Client(bool? hung, int processId = 42, long? start = null, string? problem = null)
        => new(processId, start ?? Start, "Synthetic", 0x1234, "Roblox", false, true, 1, 1, null, At, problem, WindowHung: hung);

    private static bool[] Run(ClientResponsivenessReducer reducer, params bool?[] samples)
        => samples.Select(sample => reducer.Apply([Client(sample)])[0].NotResponding).ToArray();

    [Fact]
    public void ConsecutiveHungSamplesAreRequiredBeforeAClientIsShownAsNotResponding()
    {
        Assert.Equal(new[] { false, false, true, true }, Run(new(), true, true, true, true));
        // One responding sample between hung samples restarts the count.
        Assert.Equal(new[] { false, false, false, false, false, true }, Run(new(), true, true, false, true, true, true));
    }

    [Fact]
    public void RecoveryNeedsConsecutiveRespondingSamplesAndUncheckedWindowsAreNeverClaimed()
    {
        Assert.Equal(new[] { false, false, true, true, true, true, false }, Run(new(), true, true, true, false, true, false, false));
        // A window that could not be checked (hidden, closed or unavailable) is not called frozen and restarts the count.
        Assert.Equal(new[] { false, false, true, false, false, false, true }, Run(new(), true, true, true, null, true, true, true));
        var reducer = new ClientResponsivenessReducer();
        for (int sample = 0; sample < 4; sample++)
            Assert.False(reducer.Apply([Client(true, problem: "Synthetic unavailable observation.")])[0].NotResponding);
    }

    [Fact]
    public void StateBelongsToOneProcessInstanceAndIsDroppedWhenItLeavesTheInventory()
    {
        var reducer = new ClientResponsivenessReducer();
        reducer.Apply([Client(true)]); reducer.Apply([Client(true)]);
        Assert.Empty(reducer.Apply([]));
        Assert.False(reducer.Apply([Client(true)])[0].NotResponding);
        reducer.Apply([Client(true)]);
        // A reused process id with a different start time is a different client.
        Assert.False(reducer.Apply([Client(true, start: Start + 1)])[0].NotResponding);
        var rows = reducer.Apply([Client(true, start: Start + 1), Client(false, processId: 43)]);
        Assert.False(rows[0].NotResponding); Assert.False(rows[1].NotResponding);
        rows = reducer.Apply([Client(true, start: Start + 1), Client(false, processId: 43)]);
        Assert.True(rows[0].NotResponding); Assert.False(rows[1].NotResponding);
        Assert.Equal(43, rows[1].ProcessId); Assert.Equal(Start + 1, rows[0].ProcessStartUtcTicks);
        reducer.Clear();
        Assert.False(reducer.Apply([Client(true, start: Start + 1)])[0].NotResponding);
    }

    [Fact]
    public void ObservedStateNamesANotRespondingClientButNeverHidesAnUnavailableObservation()
    {
        var frozen = Client(true) with { NotResponding = true };
        Assert.Equal("Not responding · account unverified", frozen.Status);
        Assert.Equal("Not responding · account unverified", (frozen with { Minimized = true }).Status);
        Assert.Equal("Observation unavailable", (frozen with { Problem = "Synthetic unavailable observation." }).Status);
        Assert.Equal("Open · account unverified", (frozen with { NotResponding = false }).Status);
        Assert.Equal("Open · account unverified", Client(true).Status); // One raw hung sample is not a state.
    }

    [Fact]
    public void SessionsObservedStateColumnUpdatesWhenAClientStopsAndResumesResponding()
    {
        var row = new SessionRow(Client(false));
        var raised = new List<string?>();
        row.PropertyChanged += (_, change) => raised.Add(change.PropertyName);
        row.Update(Client(true) with { NotResponding = true });
        Assert.Equal("Not responding · account unverified", row.Status);
        Assert.Contains(nameof(SessionRow.Status), raised);
        raised.Clear();
        row.Update(Client(false));
        Assert.Equal("Open · account unverified", row.Status);
        Assert.Contains(nameof(SessionRow.Status), raised);
    }

    [Fact]
    public async Task WindowsReportsATestOwnedWindowAsHungOnlyWhileItsThreadStopsProcessingMessages()
    {
        var created = new TaskCompletionSource<(nint Window, Dispatcher Dispatcher)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int release = 0;
        var thread = new Thread(() =>
        {
            try
            {
                // Hidden popup owned by this test; no Roblox client or visible desktop change is needed.
                using var source = new HwndSource(new HwndSourceParameters("zenith-hang-fixture", 64, 64) { WindowStyle = unchecked((int)0x80000000) });
                created.TrySetResult((source.Handle, Dispatcher.CurrentDispatcher));
                Dispatcher.Run();
                exited.TrySetResult();
            }
            catch (Exception error) { created.TrySetException(error); exited.TrySetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        var (window, dispatcher) = await created.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(NativeTopLevelWindows.Instance.IsHung(0));
            Assert.False(NativeTopLevelWindows.Instance.IsHung(window));
            var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = dispatcher.BeginInvoke(() =>
            {
                blocked.TrySetResult();
                // Thread.Sleep does not pump messages, so the window stops responding like a frozen client.
                var limit = Stopwatch.StartNew();
                while (Volatile.Read(ref release) == 0 && limit.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(50);
            });
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(NativeTopLevelWindows.Instance.IsHung(window)); // Windows waits about five seconds first.
            Assert.True(await Eventually(() => NativeTopLevelWindows.Instance.IsHung(window), TimeSpan.FromSeconds(15)),
                "The blocked test window was never reported as not responding.");
            Volatile.Write(ref release, 1);
            Assert.True(await Eventually(() => !NativeTopLevelWindows.Instance.IsHung(window), TimeSpan.FromSeconds(10)),
                "The test window stayed not responding after its thread resumed processing messages.");
        }
        finally
        {
            Volatile.Write(ref release, 1);
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task<bool> Eventually(Func<bool> condition, TimeSpan limit)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < limit) { if (condition()) return true; await Task.Delay(100); }
        return condition();
    }
}
