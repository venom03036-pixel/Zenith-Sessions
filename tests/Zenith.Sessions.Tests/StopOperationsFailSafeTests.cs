using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// Panic and Lock are absolute (Rule 11). A stop step that throws, for example a cancellation callback or a property-changed
// handler, must never skip the launch-queue stop, the pause, the lock or the notice after it, and it is reported by type only.
public sealed class StopOperationsFailSafeTests : IDisposable
{
    private const string Secret = "https://www.roblox.com/share?code=SECRET123&type=Server";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-stop-fail-safe-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("panic")]
    [InlineData("lock")]
    [InlineData("idle")]
    public Task AStopStepThatThrowsNeverSkipsTheQueueStopThePauseTheLockOrTheNotice(string stop) => OnDispatcherAsync(async () =>
    {
        var clock = new ManualClock();
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), idleClock: clock);
        try
        {
            await vm.InitializeAsync();
            if (stop == "idle") { vm.IdleLockMinutes = 5; vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution; }
            long generation = QueueStopGeneration(vm);
            SetField(vm, "_queuePaused", false);
            // The very first stop step raises this; a handler that throws there used to skip every step after it.
            void Throwing(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            { if (e.PropertyName == nameof(ShellViewModel.QueuePauseLabel)) throw new InvalidOperationException(Secret); }
            vm.PropertyChanged += Throwing;
            Exception? escaped;
            try
            {
                escaped = Record.Exception(() =>
                {
                    if (stop == "panic") vm.Panic.Execute(null);
                    else if (stop == "lock") vm.Lock.Execute(null);
                    else { clock.Advance(TimeSpan.FromMinutes(5)); vm.CheckIdleLock(); }
                });
            }
            finally { vm.PropertyChanged -= Throwing; }

            Assert.True(QueueStopGeneration(vm) > generation, "A throwing stop step skipped the launch-queue stop.");
            Assert.True((bool)GetField(vm, "_queuePaused")!, "A throwing stop step left the queue unpaused.");
            Assert.Contains("Queue paused", vm.QueueStatus, StringComparison.Ordinal);
            if (stop == "panic") Assert.Contains("Panic", vm.Notice, StringComparison.Ordinal);
            else
            {
                Assert.True(vm.IsLocked, "A throwing stop step left the manager unlocked.");
                Assert.Contains("locked", vm.Notice, StringComparison.OrdinalIgnoreCase);
            }
            Assert.Null(escaped);
            // The failed step is shown and kept by type only (Rule 10).
            Assert.True(vm.HasErrorNotice, "A failed stop step was not reported.");
            Assert.DoesNotContain("SECRET123", vm.ErrorNotice); Assert.DoesNotContain("SECRET123", vm.Notice);
            Assert.Contains(vm.Events, e => e.Contains("(InvalidOperationException)", StringComparison.Ordinal));
            Assert.All(vm.Events, e => Assert.DoesNotContain("SECRET123", e));
        }
        finally { await vm.DisposeAsync(); }
        string log = File.ReadAllText(Path.Combine(_root, "diagnostics", "manager.jsonl"));
        Assert.Contains("InvalidOperationException", log); Assert.DoesNotContain("SECRET123", log);
    });

    [Fact]
    public Task PanicStillStopsTheQueueWhenASignInCancellationCallbackThrowsAndEveryPanicListenerHearsIt() => OnDispatcherAsync(async () =>
    {
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor());
        using var signIn = new CancellationTokenSource();
        try
        {
            await vm.InitializeAsync();
            long generation = QueueStopGeneration(vm);
            // A sign-in is under way and one of its registered callbacks throws, so Cancel() throws an AggregateException.
            signIn.Token.Register(() => throw new InvalidOperationException(Secret));
            SetField(vm, "_authorizationStop", signIn);
            bool laterListenerRan = false;
            vm.PanicRequested += () => throw new InvalidOperationException(Secret);
            vm.PanicRequested += () => laterListenerRan = true;

            var escaped = Record.Exception(() => vm.Panic.Execute(null));

            Assert.True(QueueStopGeneration(vm) > generation, "A throwing sign-in callback skipped the launch-queue stop.");
            Assert.Contains("Queue paused", vm.QueueStatus, StringComparison.Ordinal);
            Assert.Contains("Panic", vm.Notice, StringComparison.Ordinal);
            Assert.True(laterListenerRan, "A throwing Panic listener kept a later listener from hearing Panic.");
            Assert.Null(escaped);
            Assert.True(vm.HasErrorNotice, "A failed stop step was not reported.");
            Assert.Contains(vm.Events, e => e.Contains("(AggregateException)", StringComparison.Ordinal));
            Assert.Contains(vm.Events, e => e.Contains("(InvalidOperationException)", StringComparison.Ordinal));
            Assert.All(vm.Events, e => Assert.DoesNotContain("SECRET123", e));
            Assert.DoesNotContain("SECRET123", vm.ErrorNotice);
        }
        finally { SetField(vm, "_authorizationStop", null); await vm.DisposeAsync(); }
    });

    [Fact]
    public Task APanicWithoutAFailedStepShowsNoError() => OnDispatcherAsync(async () =>
    {
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor());
        await vm.InitializeAsync();
        vm.Panic.Execute(null);
        Assert.False(vm.HasErrorNotice, "A clean Panic reported a failed step.");
        Assert.DoesNotContain(vm.Events, e => e.Contains("step failed", StringComparison.OrdinalIgnoreCase));
        vm.Lock.Execute(null);
        Assert.False(vm.HasErrorNotice, "A clean Lock reported a failed step.");
        Assert.True(vm.IsLocked);
    });

    private static long QueueStopGeneration(ShellViewModel vm)
    {
        object queue = GetField(vm, "_launchQueue")!;
        return (long)queue.GetType().GetField("_generation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(queue)!;
    }

    [Theory]
    [InlineData("lifetime")]
    [InlineData("trim")]
    public Task AThrowingShutdownStopStillCancelsEveryOtherOwnerAndDisposesTheirResources(string failingStep) => OnDispatcherAsync(async () =>
    {
        var monitor = new SyntheticMonitor();
        var trim = new ThrowingTrim();
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, monitor, clientTrim: trim);
        using var query = new CancellationTokenSource();
        using var authorization = new CancellationTokenSource();
        await vm.InitializeAsync();
        SetField(vm, "_queryCancellation", query);
        SetField(vm, "_authorizationStop", authorization);
        var lifetime = (CancellationTokenSource)GetField(vm, "_lifetime")!;
        using var callback = failingStep == "lifetime"
            ? lifetime.Token.Register(() => throw new InvalidOperationException(Secret)) : default;
        trim.ThrowOnStop = failingStep == "trim";
        var error = await Record.ExceptionAsync(() => vm.DisposeAsync().AsTask());
        Assert.True(authorization.IsCancellationRequested, "A throwing stop skipped authorization cancellation.");
        Assert.True(query.IsCancellationRequested, "A throwing stop skipped the active query cancellation.");
        Assert.True(trim.Disposed, "A throwing trim stop skipped its own resource disposal.");
        Assert.True(monitor.Disposed, "A throwing stop skipped later owners.");
        Assert.Null(error);
        string log = File.ReadAllText(Path.Combine(_root, "diagnostics", "manager.jsonl"));
        Assert.Contains(failingStep == "lifetime" ? "AggregateException" : "InvalidOperationException", log);
        Assert.DoesNotContain("SECRET123", log);
    });

    private sealed class ThrowingTrim : IClientWorkingSetTrimControl
    {
        public bool ThrowOnStop, Disposed;
        public Task<ClientWorkingSetTrimBatchResult> TrimAsync(IReadOnlyList<SessionObservation> sessions, CancellationToken ct)
            => Task.FromResult(new ClientWorkingSetTrimBatchResult([], false));
        public void CancelOperations() { if (ThrowOnStop) throw new InvalidOperationException(Secret); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private static object? GetField(ShellViewModel vm, string name)
        => typeof(ShellViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm);
    private static void SetField(ShellViewModel vm, string name, object? value)
        => typeof(ShellViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = 1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private sealed class SyntheticMonitor : IDesktopMonitor, IDisposable
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 20), [])); }
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
    }

    private static Task OnDispatcherAsync(Func<Task> work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(() => _ = RunAsync());
            async Task RunAsync()
            {
                try { await work(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
            try { Dispatcher.Run(); }
            catch (Exception ex) { completion.TrySetException(new Xunit.Sdk.XunitException($"A dispatcher fault escaped and ended the UI thread ({ex.GetType().Name}).")); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-stop-fail-safe-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
