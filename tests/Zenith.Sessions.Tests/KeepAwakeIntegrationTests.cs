using System.Collections.Concurrent;
using System.IO;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// Keep clients awake through the shell: the saved choice arms one timer, a tick presses F13 in each client of the latest fresh scan
// through the one input owner, the project's capability gate refuses it until the live trial passes, and Panic, Lock and exit stop it.
public sealed class KeepAwakeIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-keep-awake-shell-" + Guid.NewGuid().ToString("N"));
    private static readonly CapabilityGate Verified = new([new ProviderCapability(CapabilityGate.BackgroundInputCapability, CapabilityState.Supported, "Fixture: trial passed.")]);

    [Fact]
    public Task AnEarlierInitializationCannotStartKeepAwakeAfterPanic() => OnDispatcherAsync(async () =>
    {
        using (var store = new Zenith.Sessions.Windows.SettingsStore(_root))
            await store.SaveAsync(new Zenith.Sessions.Windows.SessionsSettings(KeepAwakeMinutes: 5), default);
        var input = new Executor(); var foreground = new Foreground(0x5000);
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(600),
            keepAwakeInput: input, foregroundSwitch: foreground, userIdle: new Idle(TimeSpan.FromHours(1)), capabilityGate: Verified);
        Task initialization;
        using (var held = new HeldPreferencesWrite(vm, initialLoad: true))
        {
            initialization = vm.InitializeAsync();
            Assert.False(initialization.IsCompleted);
            vm.Panic.Execute(null);
        }
        await initialization;
        Assert.True(vm.IsReady, vm.Notice);
        vm.Refresh.Execute(null); await vm.Refresh.Execution;
        Assert.Single(vm.Sessions); // Ordinary monitoring and startup continue after Panic.
        await vm.RunKeepAwakeTickAsync();
        Assert.Empty(input.Transport.Accepted);
        Assert.Empty(foreground.Requests);
        Assert.Null(vm.KeepAwakeDue);
        Assert.True(vm.CanResumeKeepAwake);
    });

    [Fact]
    public Task AnEarlierPendingSettingsSaveCannotStartKeepAwakeAfterPanic() => OnDispatcherAsync(async () =>
    {
        var input = new Executor(); var foreground = new Foreground(0x5000);
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(600),
            keepAwakeInput: input, foregroundSwitch: foreground, userIdle: new Idle(TimeSpan.FromHours(1)), capabilityGate: Verified);
        await vm.InitializeAsync();
        vm.Refresh.Execute(null); await vm.Refresh.Execution;
        using (var held = new HeldPreferencesWrite(vm))
        {
            vm.KeepAwakeMinutes = 5;
            vm.SaveSettings.Execute(null);
            Assert.False(vm.SaveSettings.Execution.IsCompleted);
            vm.Panic.Execute(null);
        }
        await vm.SaveSettings.Execution;
        await vm.RunKeepAwakeTickAsync();
        Assert.Empty(input.Transport.Accepted);
        Assert.Empty(foreground.Requests);
        Assert.Null(vm.KeepAwakeDue);
        Assert.True(vm.CanResumeKeepAwake);
    });

    [Fact]
    public Task ASavedGapArmsOneTimerAndATickPressesEachClientThroughTheInputOwnerThenReArmsIt() => OnDispatcherAsync(async () =>
    {
        var monitor = new Monitor(600, 601); var input = new Executor(); var foreground = new Foreground(0x5000); var idle = new Idle(TimeSpan.FromHours(1));
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, monitor, keepAwakeInput: input, foregroundSwitch: foreground, userIdle: idle, capabilityGate: Verified);
        try
        {
            await vm.InitializeAsync();
            Assert.Equal(0, vm.KeepAwakeMinutes); Assert.Null(vm.KeepAwakeDue);
            Assert.Equal(ShellViewModel.KeepAwakeOffStatus, vm.KeepAwakeStatus);
            Assert.Equal([0, 5, 10, 15], vm.KeepAwakeChoices.Select(c => c.Minutes));

            vm.KeepAwakeMinutes = 10; vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
            Assert.Equal("Settings saved", vm.SaveStatus);
            Assert.Equal(TimeSpan.FromMinutes(10), vm.KeepAwakeDue);
            Assert.Contains("First press in 10 minutes", vm.KeepAwakeStatus, StringComparison.Ordinal);
            // A theme autosave does not re-arm the timer or change the status.
            vm.ApplyTheme.Execute("midnight"); await vm.ApplyTheme.Execution;
            Assert.Equal(TimeSpan.FromMinutes(10), vm.KeepAwakeDue);

            // The latest fresh scan lists two clients with windows; the tick presses F13 in each and puts the earlier window back.
            vm.Refresh.Execute(null); await vm.Refresh.Execution;
            await vm.RunKeepAwakeTickAsync();
            var commands = input.Transport.Accepted.ToArray();
            Assert.Equal(4, commands.Length);
            Assert.All(commands, c => Assert.Equal(KeepAwakeRunner.F13VirtualKey, c.Signal.Key));
            Assert.Equal([(nint)9600, (nint)9600, (nint)9601, (nint)9601], commands.Select(c => c.Target.Window));
            Assert.Equal([(nint)9600, (nint)9601, (nint)0x5000], foreground.Requests);
            Assert.Contains("Sent one F13 press to 2 clients. Next press in 10 minutes.", vm.KeepAwakeStatus, StringComparison.Ordinal);
            Assert.Contains(vm.Events, e => e.EndsWith("Keep clients awake: sent F13 to 2 of 2 clients.", StringComparison.Ordinal));
            Assert.Equal(TimeSpan.FromMinutes(10), vm.KeepAwakeDue);
            Assert.False(vm.HasErrorNotice);

            // The user touched the keyboard a moment ago: the press waits and looks again shortly, taking no window from them.
            idle.Value = TimeSpan.FromSeconds(2);
            await vm.RunKeepAwakeTickAsync();
            Assert.Equal(4, input.Transport.Accepted.Count);
            Assert.Contains("Waiting until you have left the keyboard and mouse alone", vm.KeepAwakeStatus, StringComparison.Ordinal);
            Assert.Equal(KeepAwakePolicy.Deferral, vm.KeepAwakeDue);

            // Idle time Windows could not report does not stop the press.
            idle.Value = null;
            await vm.RunKeepAwakeTickAsync();
            Assert.Equal(8, input.Transport.Accepted.Count);

            // Saving Off stops the timer and says so.
            vm.KeepAwakeMinutes = 0; vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
            Assert.Null(vm.KeepAwakeDue); Assert.Equal(ShellViewModel.KeepAwakeOffStatus, vm.KeepAwakeStatus);
        }
        finally { await vm.DisposeAsync(); }
    });

    [Fact]
    public Task TheShippedGateRefusesEveryTickUntilTheTrialPassesAndABuildWithoutATransportSaysSo() => OnDispatcherAsync(async () =>
    {
        // The shipped capability list: background input is Unverified, so the saved choice runs nothing and says why.
        var input = new Executor();
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(600), keepAwakeInput: input, foregroundSwitch: new Foreground(0), userIdle: new Idle(TimeSpan.FromHours(1)));
        try
        {
            await vm.InitializeAsync();
            vm.KeepAwakeMinutes = 15; vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
            Assert.Equal("Settings saved", vm.SaveStatus);
            Assert.Null(vm.KeepAwakeDue);
            Assert.Contains("input isolation trial", vm.KeepAwakeStatus, StringComparison.Ordinal);
            Assert.Contains("Nothing was sent to any client", vm.KeepAwakeStatus, StringComparison.Ordinal);
            vm.Refresh.Execute(null); await vm.Refresh.Execution;
            await vm.RunKeepAwakeTickAsync();
            Assert.Empty(input.Transport.Attempts); Assert.Null(vm.KeepAwakeDue);
            Assert.False(vm.CanResumeKeepAwake);
            vm.Panic.Execute(null);
            Assert.Contains("input isolation trial", vm.KeepAwakeStatus, StringComparison.Ordinal);
            Assert.False(vm.CanResumeKeepAwake);
            vm.Lock.Execute(null); vm.Unlock.Execute(null);
            Assert.Contains("input isolation trial", vm.KeepAwakeStatus, StringComparison.Ordinal);
            Assert.False(vm.CanResumeKeepAwake);
        }
        finally { await vm.DisposeAsync(); }

        // The production composition has no verified transport yet: even a passing gate arms nothing, and the status says that instead.
        var bare = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(600), capabilityGate: Verified);
        try
        {
            await bare.InitializeAsync();
            Assert.Null(bare.KeepAwakeDue);
            Assert.Contains("no verified input transport", bare.KeepAwakeStatus, StringComparison.Ordinal);
            await bare.RunKeepAwakeTickAsync();
            Assert.Null(bare.KeepAwakeDue);
        }
        finally { await bare.DisposeAsync(); }

        // Sample mode never sends anything either.
        var sample = new ShellViewModel(_root, true, Dispatcher.CurrentDispatcher, new Monitor(600), keepAwakeInput: input, foregroundSwitch: new Foreground(0), userIdle: new Idle(TimeSpan.FromHours(1)), capabilityGate: Verified);
        try
        {
            await sample.InitializeAsync();
            Assert.Null(sample.KeepAwakeDue);
            Assert.Contains("Sample mode", sample.KeepAwakeStatus, StringComparison.Ordinal);
            await sample.RunKeepAwakeTickAsync();
            Assert.Empty(input.Transport.Attempts);
        }
        finally { await sample.DisposeAsync(); }
    });

    [Fact]
    public Task PanicAndLockStopTheFeatureUntilResumeOrSavingItOnAgainAndACancelledTickReleasesItsLease() => OnDispatcherAsync(async () =>
    {
        var monitor = new Monitor(700, 701); var input = new Executor(); var foreground = new Foreground(0x5000);
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, monitor, keepAwakeInput: input, foregroundSwitch: foreground, userIdle: new Idle(TimeSpan.FromHours(1)), capabilityGate: Verified);
        try
        {
            await vm.InitializeAsync();
            vm.KeepAwakeMinutes = 5; vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
            Assert.Equal(TimeSpan.FromMinutes(5), vm.KeepAwakeDue);
            vm.Refresh.Execute(null); await vm.Refresh.Execution;

            // Panic lands while the first client's press is being sent: the tick stops there, the lease is released and the
            // earlier window is put back. The feature stays stopped until Resume.
            input.Transport.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var tick = vm.RunKeepAwakeTickAsync();
            await input.Transport.Held.Task.WaitAsync(TimeSpan.FromSeconds(30));
            vm.Panic.Execute(null);
            input.Transport.Hold.SetResult();
            await tick.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Contains("Panic", vm.Notice, StringComparison.Ordinal);
            // The cancelled down was reported uncertain, so the owner released it: the only accepted command is that cleanup up.
            Assert.DoesNotContain(input.Transport.Accepted, c => !c.Cleanup);
            Assert.Equal([TargetedInputKind.KeyUp], input.Transport.Accepted.Select(c => c.Signal.Kind));
            Assert.DoesNotContain(input.Transport.Attempts, c => c.Target.Window == (nint)9701);
            Assert.Equal((nint)0x5000, foreground.Current);
            Assert.Null(vm.KeepAwakeDue);
            Assert.Contains("Stopped by Panic or Lock", vm.KeepAwakeStatus, StringComparison.Ordinal);
            Assert.True(vm.CanResumeKeepAwake);
            Assert.NotNull(input.Coordinator.TryOpen(input.Register(monitor.Client(700))!, TargetedInputPurpose.KeepAwake).Run);
            input.Transport.Hold = null;
            // A tick that fires anyway does nothing while stopped.
            int attempts = input.Transport.Attempts.Count;
            await vm.RunKeepAwakeTickAsync();
            Assert.Equal(attempts, input.Transport.Attempts.Count);

            vm.ResumeKeepAwake.Execute(null);
            Assert.False(vm.CanResumeKeepAwake);
            Assert.Equal(TimeSpan.FromMinutes(5), vm.KeepAwakeDue);
            Assert.Contains("First press in 5 minutes", vm.KeepAwakeStatus, StringComparison.Ordinal);

            // Lock stops it too; Unlock alone does not restart it, saving the choice on after off does.
            vm.Lock.Execute(null);
            Assert.Null(vm.KeepAwakeDue); Assert.False(vm.CanResumeKeepAwake);
            vm.Unlock.Execute(null);
            Assert.Null(vm.KeepAwakeDue); Assert.True(vm.CanResumeKeepAwake);
            vm.KeepAwakeMinutes = 0; vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
            Assert.False(vm.CanResumeKeepAwake);
            vm.KeepAwakeMinutes = 10; vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
            Assert.Equal(TimeSpan.FromMinutes(10), vm.KeepAwakeDue); Assert.False(vm.CanResumeKeepAwake);

            // Exit cancels a tick in flight and joins it.
            input.Transport.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously); input.Transport.Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
            tick = vm.RunKeepAwakeTickAsync();
            await input.Transport.Held.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var exit = vm.DisposeAsync().AsTask();
            input.Transport.Hold.SetResult();
            await exit.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(tick.IsCompletedSuccessfully);
            Assert.DoesNotContain(input.Transport.Accepted, c => !c.Cleanup);
            Assert.Null(vm.KeepAwakeDue);
        }
        finally { await vm.DisposeAsync(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LateCancelledTickCannotOverwriteAResumedOrChangedSchedule(bool saveOffAndOn) => OnDispatcherAsync(async () =>
    {
        var input = new Executor();
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(700), keepAwakeInput: input,
            foregroundSwitch: new Foreground(0x5000), userIdle: new Idle(TimeSpan.FromHours(1)), capabilityGate: Verified);
        try
        {
            await vm.InitializeAsync();
            vm.KeepAwakeMinutes = 5; vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
            vm.Refresh.Execute(null); await vm.Refresh.Execution;
            input.Transport.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var tick = vm.RunKeepAwakeTickAsync();
            await input.Transport.Held.Task.WaitAsync(TimeSpan.FromSeconds(30));
            vm.Panic.Execute(null);
            if (saveOffAndOn)
            {
                vm.KeepAwakeMinutes = 0; vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
                vm.KeepAwakeMinutes = 5; vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
            }
            else vm.ResumeKeepAwake.Execute(null);
            string resumedStatus = vm.KeepAwakeStatus;
            Assert.Contains("First press", resumedStatus, StringComparison.Ordinal);
            input.Transport.Hold.SetResult();
            await tick.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(resumedStatus, vm.KeepAwakeStatus);
            Assert.Equal(TimeSpan.FromMinutes(5), vm.KeepAwakeDue);
            Assert.False(vm.CanResumeKeepAwake);
        }
        finally { input.Transport.Hold?.TrySetResult(); await vm.DisposeAsync(); }
    });

    [Fact]
    public Task AnEarlyTickDoesNotConsumeTheScheduleBeforeInitializationFinishes() => OnDispatcherAsync(async () =>
    {
        var store = new Zenith.Sessions.Windows.SettingsStore(_root);
        await store.SaveAsync(new Zenith.Sessions.Windows.SessionsSettings(KeepAwakeMinutes: 5), default);
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(600), keepAwakeInput: new Executor(),
            foregroundSwitch: new Foreground(0), userIdle: new Idle(TimeSpan.FromHours(1)), capabilityGate: Verified);
        bool earlyTickRan = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.KeepAwakeStatus) && !vm.IsReady && !earlyTickRan)
            {
                earlyTickRan = true;
                Assert.True(vm.RunKeepAwakeTickAsync().IsCompletedSuccessfully);
            }
        };
        try
        {
            await vm.InitializeAsync();
            Assert.True(earlyTickRan);
            Assert.Equal(TimeSpan.FromMinutes(5), vm.KeepAwakeDue);
            Assert.Contains("First press", vm.KeepAwakeStatus, StringComparison.Ordinal);
        }
        finally { await vm.DisposeAsync(); }
    });

    /// <summary>A fresh, complete scan of the given clients on every sample, each with a window.</summary>
    private sealed class Monitor(params int[] pids) : IDesktopMonitor
    {
        private long _revision;
        public SessionObservation Client(int pid) => new(pid, DateTimeOffset.UtcNow.AddMinutes(-3).UtcTicks, "Synthetic", (nint)(9000 + pid), "Synthetic client", false, true, 1, 1, null, DateTimeOffset.UtcNow);
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            var clients = pids.Select(Client).Select(c => c with { ObservedUtc = now }).ToArray();
            return Task.FromResult(new DesktopSnapshot(new(now, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 20),
                clients, ProcessObservedUtc: now, ProcessRevision: ++_revision, ProcessInventoryComplete: true));
        }
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class Executor : IKeepAwakeInputExecutor
    {
        private readonly ConcurrentDictionary<int, Guid> _registrations = new();
        public Transport Transport { get; } = new();
        public TargetedInputCoordinator Coordinator { get; }
        public Executor() => Coordinator = new(Transport);
        public TargetedInputTarget? Register(SessionObservation client)
            => new(_registrations.GetOrAdd(client.ProcessId, _ => Guid.NewGuid()), client.ProcessId, client.ProcessStartUtcTicks, client.Window, new PixelRectangle(0, 0, 800, 600), 1);
    }

    private sealed class Transport : ITargetedInputTransport
    {
        public ConcurrentQueue<TargetedInputCommand> Attempts { get; } = new();
        public ConcurrentQueue<TargetedInputCommand> Accepted { get; } = new();
        /// <summary>When set, the first ordinary submission waits here (after signalling <see cref="Held"/>) before it checks cancellation.</summary>
        public TaskCompletionSource? Hold;
        public TaskCompletionSource Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<TargetedInputValidation> ValidateAsync(TargetedInputTarget target, bool cleanup, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(TargetedInputValidation.Current); }
        public async ValueTask<TargetedInputSubmission> SubmitAsync(TargetedInputCommand command, Func<bool> admissionOpen, CancellationToken ct)
        {
            Attempts.Enqueue(command);
            if (!command.Cleanup && Hold is { } hold) { Held.TrySetResult(); await hold.Task.ConfigureAwait(false); }
            ct.ThrowIfCancellationRequested();
            if (!admissionOpen()) return TargetedInputSubmission.Rejected;
            Accepted.Enqueue(command);
            return TargetedInputSubmission.Queued;
        }
    }

    private sealed class Foreground(nint current) : IForegroundWindowSwitch
    {
        public List<nint> Requests { get; } = [];
        public nint Current { get; private set; } = current;
        public bool BringForward(nint window) { Requests.Add(window); Current = window; return true; }
    }

    private sealed class Idle(TimeSpan? value) : IUserInputIdleSource
    {
        public TimeSpan? Value = value;
        public TimeSpan? IdleFor => Value;
    }

    private static Task OnDispatcherAsync(Func<Task> work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            Task operation = Task.CompletedTask;
            dispatcher.BeginInvoke(() => { operation = RunAsync(); });
            async Task RunAsync()
            {
                try { await work(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
            Dispatcher.Run(); operation.GetAwaiter().GetResult();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-keep-awake-shell-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
