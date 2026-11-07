using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientPreviewPresentationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-preview-ui-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ShellInitializationAndSampleModeDoNotCaptureWithoutPermission(bool sample) => OnUi(async () =>
    {
        var provider = new PreviewProvider { AutoReady = true }; var monitor = new DesktopMonitor();
        await using var shell = await Ready(monitor, provider, sample);
        Assert.Equal(0, provider.CaptureCalls);
        Assert.Empty(shell.ClientPreviews.Rows);
        ShowPreviews(shell);
        await Refresh(shell);
        Assert.Equal(0, provider.CaptureCalls); // Merely showing the panel never grants capture permission.
        if (sample)
        {
            shell.SelectedSession = shell.Sessions[0]; shell.StartClientPreviews.Execute(null);
            Assert.Equal(0, provider.CaptureCalls); Assert.False(shell.CanStartClientPreviews);
        }
    });

    [Fact]
    public Task ShellPinsCheckedClientsOrExplicitSelectedFallbackAndRejectsMoreThanFour() => OnUi(async () =>
    {
        var provider = new PreviewProvider { AutoReady = true }; var monitor = new DesktopMonitor(5);
        await using var shell = await Ready(monitor, provider);
        ShowPreviews(shell);
        foreach (var row in shell.Sessions) row.IsChecked = true;
        shell.StartClientPreviews.Execute(null);
        Assert.Equal(0, provider.CaptureCalls); Assert.Empty(shell.ClientPreviews.Rows);
        foreach (var row in shell.Sessions) row.IsChecked = false;
        shell.SelectedSession = shell.Sessions[1]; shell.StartClientPreviews.Execute(null);
        var selected = await provider.NextAsync();
        Assert.Equal(shell.Sessions[1].Observation.ProcessId, Assert.Single(selected.Targets).Process.ProcessId);
        shell.SelectedSession = shell.Sessions[2];
        Assert.Equal(selected.Targets[0], Assert.Single(shell.ClientPreviews.Rows).Target); // Selection is pinned.
        shell.StopClientPreviews.Execute(null); await provider.NextReleaseAsync();
        foreach (var row in shell.Sessions.Take(4)) row.IsChecked = true;
        await Refresh(shell); shell.StartClientPreviews.Execute(null);
        var checkedCall = await provider.NextAsync();
        Assert.Equal(4, checkedCall.Targets.Count);
        Assert.Equal(shell.Sessions.Take(4).Select(row => row.Observation.ProcessId), checkedCall.Targets.Select(target => target.Process.ProcessId));
    });

    [Theory]
    [InlineData("hide")]
    [InlineData("tab")]
    [InlineData("lock")]
    [InlineData("panic")]
    public Task ShellContextFencesClearPixelsAndRequireAnExplicitRestart(string context) => OnUi(async () =>
    {
        var provider = new PreviewProvider { AutoReady = true }; var monitor = new DesktopMonitor();
        await using var shell = await Ready(monitor, provider);
        ShowPreviews(shell); shell.SelectedSession = shell.Sessions[0]; shell.StartClientPreviews.Execute(null);
        await provider.NextAsync();
        await Until(() => shell.ClientPreviews.Rows.Count == 1 && shell.ClientPreviews.Rows[0].HasImage);
        switch (context)
        {
            case "hide": shell.SetPreviewWindowVisible(false); break;
            case "tab": shell.Navigate.Execute("Overview"); break;
            case "lock": shell.LockDashboard(); break;
            default: shell.Panic.Execute(null); break;
        }
        Assert.All(shell.ClientPreviews.Rows, row => Assert.Null(row.Image));
        Assert.False(shell.ClientPreviews.IsRunning);
        await provider.NextReleaseAsync(); int calls = provider.CaptureCalls;
        if (shell.IsLocked) shell.Unlock.Execute(null);
        ShowPreviews(shell); await Refresh(shell);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(shell.ClientPreviews.IsRunning); Assert.Equal(calls, provider.CaptureCalls);
        shell.StartClientPreviews.Execute(null);
        Assert.True((await provider.NextAsync()).Generation > 0);
    });

    [Fact]
    public Task ShellStaleResourceEvidenceFencesCaptureInsteadOfRenewingFromPresentationTime() => OnUi(async () =>
    {
        var provider = new PreviewProvider { AutoReady = true }; var monitor = new DesktopMonitor();
        await using var shell = await Ready(monitor, provider);
        ShowPreviews(shell); shell.SelectedSession = shell.Sessions[0]; shell.StartClientPreviews.Execute(null);
        await provider.NextAsync();
        monitor.ResourceTime = DateTimeOffset.UtcNow.AddMinutes(-1); await Refresh(shell);
        Assert.False(shell.CanStartClientPreviews); Assert.False(shell.ClientPreviews.IsRunning);
        Assert.All(shell.ClientPreviews.Rows, row => Assert.Null(row.Image));
        await provider.NextReleaseAsync(); int calls = provider.CaptureCalls;
        monitor.ResourceTime = null; await Refresh(shell);
        Assert.Equal(calls, provider.CaptureCalls); Assert.False(shell.ClientPreviews.IsRunning);
    });

    [Fact]
    public Task ShellRecordsOnlyTheCaptureFailureTypeAndKeepsPreviewsAvailable() => OnUi(async () =>
    {
        var provider = new PreviewProvider(); var monitor = new DesktopMonitor();
        await using var shell = await Ready(monitor, provider);
        ShowPreviews(shell); shell.SelectedSession = shell.Sessions[0]; shell.StartClientPreviews.Execute(null);
        var call = await provider.NextAsync(); Task active = shell.ClientPreviews.ActiveCapture;
        call.Reply.SetException(new TimeoutException("untrusted-provider-marker")); await active.WaitAsync(TimeSpan.FromSeconds(5));
        shell.RefreshDiagnostics.Execute(null);
        var failure = Assert.Single(shell.DiagnosticRows, row => row.Event.Kind == Zenith.Sessions.Windows.ManagerEventKind.OperationFailed
            && row.Event.ErrorType == nameof(TimeoutException));
        Assert.DoesNotContain("untrusted-provider-marker", failure.Event.Summary);
        Assert.True(shell.ClientPreviews.IsAvailable); // A provider failure is not a presenter fault.
    });

    [Theory]
    [InlineData("start")]
    [InlineData("window")]
    [InlineData("pid")]
    public Task CompleteChangedProcessInventoryFencesPinnedPreviewAndRejectsLatePixels(string changed) => OnUi(async () =>
    {
        var provider = new PreviewProvider { IgnoreCancellation = true }; var monitor = new DesktopMonitor(1);
        await using var shell = await Ready(monitor, provider);
        ShowPreviews(shell); shell.SelectedSession = shell.Sessions[0]; shell.StartClientPreviews.Execute(null);
        var call = await provider.NextAsync();
        try
        {
            var original = monitor.Processes[0];
            monitor.Processes = [changed switch
            {
                "start" => original with { ProcessStartUtcTicks = original.ProcessStartUtcTicks + 1 },
                "window" => original with { Window = original.Window + 1 },
                _ => original with { ProcessId = original.ProcessId + 100, Window = original.Window + 100 }
            }];
            await Refresh(shell);
            Assert.True(call.Token.IsCancellationRequested);
            Assert.False(shell.ClientPreviews.IsRunning);
        }
        finally { call.Reply.TrySetResult([ReadyFrame(call, DateTimeOffset.UtcNow)]); }
        await provider.NextReleaseAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.All(shell.ClientPreviews.Rows, row => Assert.Null(row.Image));
    });

    [Fact]
    public Task PresenterPublishesDetachedFrozenPixelsAndExpiresThemDuringAStuckCapture() => OnUi(async () =>
    {
        var clock = new Clock(); var provider = new PreviewProvider();
        await using var presenter = new ClientPreviewsViewModel(provider, Dispatcher.CurrentDispatcher, () => true, clock: clock);
        presenter.Start([Target()], clock.GetUtcNow().AddMinutes(1));
        var call = await provider.NextAsync(); Task active = presenter.ActiveCapture;
        byte[] pixels = new byte[230400]; new byte[] { 10, 20, 30, 255 }.CopyTo(pixels, 0);
        call.Reply.SetResult([ReadyFrame(call, clock.GetUtcNow(), pixels)]); await active;
        await DriveUntil(clock, () => presenter.Rows[0].HasImage);
        var image = presenter.Rows[0].Image!;
        Assert.True(image.IsFrozen); Assert.Equal(PixelFormats.Bgra32, image.Format);
        Assert.Equal(320, image.PixelWidth); Assert.Equal(180, image.PixelHeight);
        pixels[0] = 99; byte[] copied = new byte[230400]; image.CopyPixels(copied, 1280, 0);
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, copied[..4]);
        Assert.Contains("unverified", presenter.Rows[0].Captured, StringComparison.OrdinalIgnoreCase);
        clock.Advance(TimeSpan.FromSeconds(4));
        await DriveUntil(clock, () => !presenter.Rows[0].HasImage);
        Assert.Null(presenter.Rows[0].Image);
    });

    [Fact]
    public Task PreviewConversionDoesNotCopyDetachedPixelsBeforeWpf() => OnUi(() =>
    {
        // The coordinator publishes a fresh exact-length array that nothing modifies; WPF copies it natively.
        byte[] pixels = new byte[ClientPreviewLimits.MaximumFrameBytes];
        for (int index = 0; index < pixels.Length; index++) pixels[index] = (byte)(index * 7);
        var frame = new ClientPreviewFrame(Target(), 1, 320, 180, 1280, pixels, DateTimeOffset.UnixEpoch, 1920, 1080, 1);
        ClientPreviewsViewModel.CreatePreviewImage(frame); // Warm up WPF type initialization and WIC factories.
        long before = GC.GetAllocatedBytesForCurrentThread();
        var image = ClientPreviewsViewModel.CreatePreviewImage(frame);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < ClientPreviewLimits.MaximumFrameBytes,
            $"Converting one detached 320x180 frame allocated {allocated} managed bytes; WPF already copies the pixels, so a frame-sized copy is not needed.");
        Assert.True(image.IsFrozen); Assert.Equal(PixelFormats.Bgra32, image.Format);
        Assert.Equal(320, image.PixelWidth); Assert.Equal(180, image.PixelHeight);
        byte[] copied = new byte[pixels.Length]; image.CopyPixels(copied, 1280, 0);
        Assert.Equal(pixels, copied);
        // A slice of a larger buffer is not the published shape; it still converts its own bytes only.
        byte[] padded = new byte[pixels.Length + 8]; pixels.CopyTo(padded, 4);
        var sliced = ClientPreviewsViewModel.CreatePreviewImage(frame with { Pixels = new ReadOnlyMemory<byte>(padded, 4, pixels.Length) });
        Array.Clear(copied); sliced.CopyPixels(copied, 1280, 0);
        Assert.Equal(pixels, copied);
        return Task.CompletedTask;
    });

    [Fact]
    public Task FenceWhileOldSourceIsReleasingCancelsThePendingReplacementRequest() => OnUi(async () =>
    {
        var clock = new Clock(); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new PreviewProvider { IgnoreCancellation = true, ReleaseGate = gate.Task };
        var presenter = new ClientPreviewsViewModel(provider, Dispatcher.CurrentDispatcher, () => true, clock: clock);
        CaptureCall? first = null;
        try
        {
            presenter.Start([Target()], clock.GetUtcNow().AddMinutes(1)); first = await provider.NextAsync();
            provider.IgnoreCancellation = false; // Only the old owner ignores cancellation; cleanup cannot strand later fixture calls.
            presenter.Start([Target(2)], clock.GetUtcNow().AddMinutes(1));
            presenter.Fence("Explicit stop while an older capture is releasing.");
            Assert.False(presenter.IsRunning); Assert.True(presenter.IsReleasing);
            first.Reply.SetResult([ReadyFrame(first, clock.GetUtcNow())]); await provider.NextReleaseAsync();
            clock.Advance(TimeSpan.FromSeconds(1)); gate.SetResult(); await provider.ReleaseCompleted.Task;
            await DriveUntil(clock, () => !presenter.IsReleasing);
            Assert.Equal(1, provider.CaptureCalls); // The replacement may never enter CaptureAsync after Fence.
            Assert.False(presenter.IsRunning); Assert.All(presenter.Rows, row => Assert.Null(row.Image));
        }
        finally { first?.Reply.TrySetResult([]); provider.UnblockAll(); gate.TrySetResult(); await presenter.DisposeAsync(); }
    });

    [Fact]
    public Task PresenterEligibilityUsesTheOriginalResourceLeaseAndNeverRestartsOnRenewal() => OnUi(async () =>
    {
        var clock = new Clock(); var provider = new PreviewProvider();
        await using var presenter = new ClientPreviewsViewModel(provider, Dispatcher.CurrentDispatcher, () => true, clock: clock);
        DateTimeOffset resourceObserved = clock.GetUtcNow();
        presenter.Start([Target()], resourceObserved.AddSeconds(3));
        var call = await provider.NextAsync(); Task active = presenter.ActiveCapture;
        call.Reply.SetResult([ReadyFrame(call, clock.GetUtcNow())]); await active;
        await DriveUntil(clock, () => presenter.Rows[0].HasImage);
        clock.Advance(TimeSpan.FromSeconds(2)); presenter.RenewResourceLease(resourceObserved.AddSeconds(3));
        clock.Advance(TimeSpan.FromSeconds(1));
        await DriveUntil(clock, () => !presenter.IsRunning && !presenter.IsReleasing);
        Assert.Null(presenter.Rows[0].Image); int calls = provider.CaptureCalls;
        presenter.RenewResourceLease(clock.GetUtcNow().AddSeconds(3));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(presenter.IsRunning); Assert.Equal(calls, provider.CaptureCalls);
    });

    private async Task<ShellViewModel> Ready(DesktopMonitor monitor, PreviewProvider provider, bool sample = false)
    {
        var shell = new ShellViewModel(_root, sample, Dispatcher.CurrentDispatcher, monitor,
            new NoPublicDirectory(), new NoServerDirectory(), previewProvider: provider);
        await shell.InitializeAsync(); await Refresh(shell);
        Assert.True(shell.IsReady, shell.Notice);
        return shell;
    }
    private static void ShowPreviews(ShellViewModel shell)
    { shell.Navigate.Execute("Sessions"); shell.ShowClientPreviewMode.Execute(null); shell.SetPreviewWindowVisible(true); }
    private static async Task Refresh(ShellViewModel shell)
    { shell.Refresh.Execute(null); await shell.Refresh.Execution; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Synthetic preview publication did not reach its expected state.");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(5);
        }
    }
    private static async Task DriveUntil(Clock clock, Func<bool> condition)
    {
        var elapsed=Stopwatch.StartNew();int advances=0;
        while(!condition() && advances<12 && elapsed.Elapsed<TimeSpan.FromSeconds(5))
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (condition()) break;
            // The final stopped state can be painted after the condition check and leave
            // the presenter idle. Never wait for a new timer that no longer needs to exist.
            if(clock.TryAdvancePresentation()) advances++;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(1);
        }
        Assert.True(condition(), "The synthetic presenter did not publish the expected state.");
    }
    private static ClientPreviewTarget Target(int pid = 1) => new(new(null, pid, new DateTime(2026, 1, 1).Ticks), (nint)(pid + 100));
    private static ClientPreviewResult ReadyFrame(CaptureCall call, DateTimeOffset at, byte[]? pixels = null)
    {
        var frame = new ClientPreviewFrame(call.Targets[0], call.Generation, 320, 180, 1280,
            pixels ?? new byte[230400], at, 1920, 1080, call.Sequence);
        return new(frame.Target, ClientPreviewStatus.Ready, frame, "untrusted-provider-marker");
    }
    private sealed class DesktopMonitor(int count = 2) : IDesktopMonitor
    {
        public SessionObservation[] Processes = Enumerable.Range(1, count).Select(pid => new SessionObservation(pid,
            new DateTime(2026, 1, 1).Ticks, "Synthetic", pid + 100, "Synthetic", false, true, 1, 1, 0, DateTimeOffset.UtcNow)).ToArray();
        public DateTimeOffset? ResourceTime;
        private long _revision;
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new DesktopSnapshot(new(ResourceTime ?? now, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB,
                64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 10), Processes,
                ProcessObservedUtc: now, ProcessRevision: Interlocked.Increment(ref _revision), ProcessInventoryComplete: true));
        }
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => throw new InvalidOperationException("No window action expected.");
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => throw new InvalidOperationException("No window action expected.");
    }
    private sealed class NoPublicDirectory : IPublicRobloxDirectory
    {
        public Task<PublicDirectoryResult<PublicRobloxUser>> LookupUserAsync(long id, CancellationToken ct) => throw new InvalidOperationException("No network expected.");
        public Task<PublicDirectoryResult<PublicRobloxAvatar>> GetAvatarAsync(long id, CancellationToken ct) => throw new InvalidOperationException("No network expected.");
        public Task<PublicDirectoryResult<EncodedPublicAvatar>> FetchAvatarAsync(PublicRobloxAvatar avatar, CancellationToken ct) => throw new InvalidOperationException("No network expected.");
        public void ClearMemoryCache() { }
    }
    private sealed class NoServerDirectory : IRobloxPublicServerDirectory
    { public Task<RobloxPublicServerResult> BrowseAsync(RobloxPublicServerQuery query, CancellationToken ct) => throw new InvalidOperationException("No network expected."); }

    private sealed record CaptureCall(IReadOnlyList<ClientPreviewTarget> Targets, long Generation, long Sequence, CancellationToken Token)
    { public TaskCompletionSource<IReadOnlyList<ClientPreviewResult>> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    private sealed class PreviewProvider : IClientPreviewProvider
    {
        private readonly Channel<CaptureCall> _calls = Channel.CreateBounded<CaptureCall>(16);
        private readonly Channel<bool> _releases = Channel.CreateBounded<bool>(16);
        private readonly List<CaptureCall> _pending = [];
        private int _captures;
        public bool AutoReady, IgnoreCancellation;
        public Task? ReleaseGate;
        public TaskCompletionSource ReleaseCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CaptureCalls => Volatile.Read(ref _captures);
        public async Task<IReadOnlyList<ClientPreviewResult>> CaptureAsync(IReadOnlyList<ClientPreviewTarget> targets, long generation, CancellationToken ct)
        {
            bool ignoreCancellation = IgnoreCancellation;
            var call = new CaptureCall(targets.ToArray(), generation, Interlocked.Increment(ref _captures), ct);
            lock (_pending) _pending.Add(call);
            _calls.Writer.TryWrite(call);
            if (AutoReady)
                return call.Targets.Select(target =>
                {
                    var result = ReadyFrame(call, DateTimeOffset.UtcNow);
                    return result with { Target = target, Frame = result.Frame! with { Target = target } };
                }).ToArray();
            return ignoreCancellation ? await call.Reply.Task : await call.Reply.Task.WaitAsync(ct);
        }
        public Task<CaptureCall> NextAsync() => _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public Task<bool> NextReleaseAsync() => _releases.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public async Task ReleaseAsync(CancellationToken ct)
        { _releases.Writer.TryWrite(true); if (ReleaseGate is { } gate) await gate; ReleaseCompleted.TrySetResult(); }
        public void UnblockAll() { lock (_pending) foreach (var call in _pending) call.Reply.TrySetResult([]); }
        public ValueTask DisposeAsync() { UnblockAll(); return ValueTask.CompletedTask; }
    }
    private sealed class Clock : TimeProvider
    {
        private readonly object _gate = new(); private readonly List<Timer> _timers = [];
        private readonly Channel<Timer> _presentation = Channel.CreateBounded<Timer>(32);
        private DateTimeOffset _utc = new(2026, 11, 5, 18, 0, 0, TimeSpan.Zero); private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utc; }
        public override long GetTimestamp() { lock (_gate) return _ticks; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            lock (_gate) { _timers.Add(timer); timer.Change(dueTime, period); }
            return timer;
        }
        public bool TryAdvancePresentation()
        {
            while (_presentation.Reader.TryRead(out var timer))
            {
                TimeSpan advance;
                lock (_gate) { if (timer.Due is not { } due) continue; advance = TimeSpan.FromTicks(Math.Max(0, due - _ticks)); }
                Advance(advance); return true;
            }
            return false;
        }
        public void Advance(TimeSpan duration)
        {
            List<Timer> ready;
            lock (_gate)
            {
                _utc += duration; _ticks += duration.Ticks;
                ready = _timers.Where(timer => timer.Due is { } due && due <= _ticks).ToList();
                foreach (var timer in ready) timer.Due = null;
            }
            foreach (var timer in ready) timer.Callback(timer.State);
        }
        private sealed class Timer(Clock owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback; public object? State { get; } = state; public long? Due;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._ticks + dueTime.Ticks;
                    if (dueTime == TimeSpan.FromMilliseconds(250)) owner._presentation.Writer.TryWrite(this);
                    return true;
                }
            }
            public void Dispose() { lock (owner._gate) { Due = null; owner._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private static Task OnUi(Func<Task> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; Task operation = Task.CompletedTask;
            dispatcher.BeginInvoke(() => operation = Run());
            async Task Run()
            {
                try { await work(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
            Dispatcher.Run(); operation.GetAwaiter().GetResult();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-preview-ui-tests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
