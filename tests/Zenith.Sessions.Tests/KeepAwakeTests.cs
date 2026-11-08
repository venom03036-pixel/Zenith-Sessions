using System.Collections.Concurrent;
using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// Keep clients awake (R-instances-17): a built-in plan that, every N minutes, brings each client forward, sends one F13 press
// through the one targeted input owner and restores the previous foreground window. Controlled transport and foreground
// switch only: no HWND lookup, Windows messages, physical input or account data.
public sealed class KeepAwakeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private const int F13 = 0x7C;

    [Fact]
    public void TheGateRefusesKeepClientsAwakeUntilBackgroundInputIsSupportedExactlyLikeLaunchAndRun()
    {
        // The shipped capability list still says background input is unverified, so both gated features are refused.
        var production = new CapabilityGate();
        foreach (var feature in new[] { GatedFeature.KeepClientsAwake, GatedFeature.LaunchAndRun })
        {
            var decision = production.Check(feature);
            Assert.False(decision.Permitted, $"{feature} must be refused while background input is unverified.");
            Assert.Contains("input isolation trial", decision.Explanation, StringComparison.Ordinal);
            Assert.Contains(CapabilityGate.BackgroundInputCapability, decision.Explanation, StringComparison.Ordinal);
            Assert.Contains(CapabilityGate.FeatureName(feature), decision.Explanation, StringComparison.Ordinal);
        }
        Assert.Contains(ProviderCapabilities.Current, c => c.Name == CapabilityGate.BackgroundInputCapability && c.State != CapabilityState.Supported);

        // Once the trial marks that capability Supported, the same gate permits them; any other state still refuses.
        var verified = new CapabilityGate([new ProviderCapability(CapabilityGate.BackgroundInputCapability, CapabilityState.Supported, "Live trial passed.")]);
        Assert.True(verified.Check(GatedFeature.KeepClientsAwake).Permitted);
        Assert.True(verified.Check(GatedFeature.LaunchAndRun).Permitted);
        var unavailable = new CapabilityGate([new ProviderCapability(CapabilityGate.BackgroundInputCapability, CapabilityState.Unavailable, "")]);
        Assert.False(unavailable.Check(GatedFeature.KeepClientsAwake).Permitted);
        var missing = new CapabilityGate([]);
        Assert.False(missing.Check(GatedFeature.KeepClientsAwake).Permitted);
        Assert.Throws<ArgumentOutOfRangeException>(() => verified.Check((GatedFeature)99));
    }

    [Fact]
    public void ThePolicyIsOffByDefaultBoundedAndDefersWhileTheUserIsActive()
    {
        Assert.Equal(15, KeepAwakePolicy.DefaultMinutes);
        Assert.True(KeepAwakePolicy.IsValidMinutes(0));
        Assert.False(KeepAwakePolicy.IsValidMinutes(4)); Assert.True(KeepAwakePolicy.IsValidMinutes(5));
        Assert.True(KeepAwakePolicy.IsValidMinutes(19)); Assert.False(KeepAwakePolicy.IsValidMinutes(20)); Assert.False(KeepAwakePolicy.IsValidMinutes(-5));
        var standard = KeepAwakePolicy.ChoicesFor(0);
        Assert.Equal([0, 5, 10, 15], standard.Select(c => c.Minutes));
        Assert.Equal("Off", standard[0].Label); Assert.Equal("Every 15 minutes", standard[3].Label);
        Assert.Same(standard, KeepAwakePolicy.ChoicesFor(10));
        Assert.Equal([0, 5, 10, 12, 15], KeepAwakePolicy.ChoicesFor(12).Select(c => c.Minutes));
        Assert.Equal(standard, KeepAwakePolicy.ChoicesFor(25)); // An invalid saved value adds no choice.

        Assert.Equal(KeepAwakeStep.Off, KeepAwakePolicy.Decide(0, permitted: true, userIdleFor: TimeSpan.FromHours(1), out _));
        Assert.Equal(KeepAwakeStep.Refused, KeepAwakePolicy.Decide(15, permitted: false, userIdleFor: TimeSpan.FromHours(1), out _));
        // The user touched the keyboard or mouse a moment ago: never take the foreground from them; look again shortly.
        Assert.Equal(KeepAwakeStep.Defer, KeepAwakePolicy.Decide(15, permitted: true, userIdleFor: TimeSpan.FromSeconds(3), out var wait));
        Assert.Equal(KeepAwakePolicy.Deferral, wait);
        Assert.Equal(KeepAwakeStep.Press, KeepAwakePolicy.Decide(15, permitted: true, userIdleFor: KeepAwakePolicy.UserIdleThreshold, out wait));
        Assert.Equal(TimeSpan.FromMinutes(15), wait);
        // Idle time Windows could not report does not stop the press: a missed press is a disconnected client.
        Assert.Equal(KeepAwakeStep.Press, KeepAwakePolicy.Decide(5, permitted: true, userIdleFor: null, out wait));
        Assert.Equal(TimeSpan.FromMinutes(5), wait);
        Assert.Throws<ArgumentOutOfRangeException>(() => KeepAwakePolicy.Decide(25, permitted: true, userIdleFor: null, out _));
    }

    [Fact]
    public async Task ATickBringsEachClientForwardSendsOneF13PressThroughTheInputOwnerAndRestoresTheForeground()
    {
        await using var fixture = new Fixture(previousForeground: 0x5000);
        var first = fixture.Client(600); var second = fixture.Client(601);
        var result = await fixture.Runner.RunAsync([first, second], default).WaitAsync(Deadline);

        Assert.Equal(2, result.Pressed);
        Assert.All(result.Clients, row => Assert.Equal(KeepAwakeOutcome.Pressed, row.Outcome));
        Assert.Equal([first.ProcessKey, second.ProcessKey], result.Clients.Select(row => row.Client));
        // Exactly one down and one up of F13 per client, in that order, each addressed to that client's own registered target.
        var commands = fixture.Transport.Accepted.ToArray();
        Assert.Equal(4, commands.Length);
        Assert.Equal([TargetedInputKind.KeyDown, TargetedInputKind.KeyUp, TargetedInputKind.KeyDown, TargetedInputKind.KeyUp], commands.Select(c => c.Signal.Kind));
        Assert.All(commands, c => Assert.Equal(F13, c.Signal.Key));
        Assert.Equal([fixture.Window(600), fixture.Window(600), fixture.Window(601), fixture.Window(601)], commands.Select(c => c.Target.Window));
        Assert.Equal(2, commands.Select(c => c.RunId).Distinct().Count());
        // Each client was in front before its press, and the window that was in front before the tick is back in front.
        Assert.Equal([fixture.Window(600), fixture.Window(601), (nint)0x5000], fixture.Foreground.Requests);
        Assert.True(result.ForegroundRestored);
        Assert.Equal((nint)0x5000, fixture.Foreground.Current);
        // Every lease was released: the same windows can be opened again, and nothing is left running in the owner.
        Assert.NotNull(fixture.Owner.TryOpen(fixture.Target(600), TargetedInputPurpose.KeepAwake).Run);
        Assert.Contains("2 clients", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClientWindowsWillNotBringForwardGetsNoPressAndTheOthersStillDo()
    {
        await using var fixture = new Fixture(previousForeground: 0x5000);
        var first = fixture.Client(700); var second = fixture.Client(701); var third = fixture.Client(702);
        fixture.Foreground.Refuse(fixture.Window(701));
        var result = await fixture.Runner.RunAsync([first, second, third], default).WaitAsync(Deadline);

        Assert.Equal([KeepAwakeOutcome.Pressed, KeepAwakeOutcome.NotInFront, KeepAwakeOutcome.Pressed], result.Clients.Select(row => row.Outcome));
        // A press is never sent to a client that is not in front: a focus change must not deliver F13 to another program.
        Assert.DoesNotContain(fixture.Transport.Attempts, c => c.Target.Window == fixture.Window(701));
        Assert.Equal(4, fixture.Transport.Accepted.Count);
        Assert.True(result.ForegroundRestored);
        Assert.Contains("2 of 3 clients", result.Summary, StringComparison.Ordinal);
        Assert.Contains("1 could not be brought forward", result.Summary, StringComparison.Ordinal);
        // The refused client's lease is released as well.
        Assert.NotNull(fixture.Owner.TryOpen(fixture.Target(701), TargetedInputPurpose.KeepAwake).Run);
    }

    [Fact]
    public async Task AClientTheOwnerCannotLeaseOrTheTransportRejectsIsReportedWithoutAFallback()
    {
        await using var fixture = new Fixture(previousForeground: 0x5000);
        var owned = fixture.Client(800); var rejected = fixture.Client(801); var unregistered = fixture.Client(802);
        // A macro (or anything else) already owns the first window's lease; the plan never steals it.
        var macro = fixture.Owner.TryOpen(fixture.Target(800), TargetedInputPurpose.MacroPlayback).Run;
        Assert.NotNull(macro);
        fixture.Transport.Reject(fixture.Window(801));
        fixture.Executor.Unregister(802);
        var result = await fixture.Runner.RunAsync([owned, rejected, unregistered], default).WaitAsync(Deadline);

        Assert.Equal([KeepAwakeOutcome.LeaseRefused, KeepAwakeOutcome.InputRejected, KeepAwakeOutcome.NotRegistered], result.Clients.Select(row => row.Outcome));
        Assert.Equal(0, result.Pressed);
        Assert.DoesNotContain(fixture.Transport.Attempts, c => c.Target.Window == fixture.Window(800));
        // Nothing was sent any other way: the rejected client got exactly the one refused attempt and no retry.
        Assert.Single(fixture.Transport.Attempts.Where(c => c.Target.Window == fixture.Window(801)));
        Assert.Empty(fixture.Transport.Accepted);
        Assert.Equal(TargetedInputRunState.Running, macro!.Snapshot.State);
        Assert.True(result.ForegroundRestored);
        // The rejected client's faulted lease was stopped and released.
        Assert.NotNull(fixture.Owner.TryOpen(fixture.Target(801), TargetedInputPurpose.KeepAwake).Run);
        await macro.StopAsync().WaitAsync(Deadline);
    }

    [Fact]
    public async Task ACancelledTickStopsBeforeTheNextClientAndStillRestoresTheForeground()
    {
        await using var fixture = new Fixture(previousForeground: 0x5000);
        var first = fixture.Client(900); var second = fixture.Client(901); var third = fixture.Client(902);
        using var stop = new CancellationTokenSource();
        // Panic arrives while the second client's press is being sent.
        fixture.Transport.BeforeSubmit = command => { if (command.Target.Window == fixture.Window(901)) stop.Cancel(); };
        var result = await fixture.Runner.RunAsync([first, second, third], stop.Token).WaitAsync(Deadline);

        Assert.Equal(KeepAwakeOutcome.Pressed, result.Clients[0].Outcome);
        Assert.Equal(KeepAwakeOutcome.Cancelled, result.Clients[1].Outcome);
        Assert.Equal(KeepAwakeOutcome.Cancelled, result.Clients[2].Outcome);
        Assert.DoesNotContain(fixture.Transport.Attempts, c => c.Target.Window == fixture.Window(902));
        Assert.True(result.ForegroundRestored);
        Assert.Equal((nint)0x5000, fixture.Foreground.Current);
        // Every lease, including the cancelled one, is released.
        Assert.NotNull(fixture.Owner.TryOpen(fixture.Target(901), TargetedInputPurpose.KeepAwake).Run);
    }

    [Fact]
    public async Task MoreClientsThanTheBoundAreLeftForTheNextTickAndAnEmptyListSendsNothing()
    {
        await using var fixture = new Fixture(previousForeground: 0);
        var clients = Enumerable.Range(1000, KeepAwakeRunner.MaximumClients + 3).Select(fixture.Client).ToArray();
        var result = await fixture.Runner.RunAsync(clients, default).WaitAsync(Deadline);
        Assert.Equal(KeepAwakeRunner.MaximumClients, result.Pressed);
        Assert.Equal(KeepAwakeRunner.MaximumClients, result.Clients.Count);
        Assert.Equal(3, result.LeftForNextTick);
        // Nothing was in front before the tick, so there is nothing to restore and the last client stays in front.
        Assert.False(result.ForegroundRestored);
        Assert.Contains("3 more", result.Summary, StringComparison.Ordinal);

        // The deferred tail must actually get a turn on the next tick.
        var next = await fixture.Runner.RunAsync(clients, default).WaitAsync(Deadline);
        Assert.Equal(clients[KeepAwakeRunner.MaximumClients].ProcessKey, next.Clients[0].Client);
        Assert.Equal(clients.Length, result.Clients.Concat(next.Clients).Select(r => r.Client).Distinct().Count());

        var empty = await fixture.Runner.RunAsync([], default).WaitAsync(Deadline);
        Assert.Empty(empty.Clients); Assert.Equal(0, empty.Pressed);
    }

    [Fact]
    public async Task RegistrationFailureKeepsOnlyTheErrorTypeAndDoesNotSendInput()
    {
        await using var fixture = new Fixture(previousForeground: 0);
        fixture.Executor.RegistrationError = new InvalidOperationException("secret registration payload");
        var result = await fixture.Runner.RunAsync([fixture.Client(1000)], default);
        Assert.Equal(KeepAwakeOutcome.NotRegistered, result.Clients[0].Outcome);
        Assert.Contains("InvalidOperationException", result.Clients[0].Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", result.Clients[0].Explanation, StringComparison.Ordinal);
        Assert.Empty(fixture.Transport.Attempts);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public ControlledTransport Transport { get; } = new();
        public ControlledForeground Foreground { get; }
        public TargetedInputCoordinator Owner { get; }
        public ControlledExecutor Executor { get; }
        public KeepAwakeRunner Runner { get; }
        public Fixture(nint previousForeground)
        {
            Owner = new(Transport, new(MaximumRuns: 64));
            Executor = new(Owner);
            Foreground = new(previousForeground);
            Runner = new(Executor, Foreground);
        }
        public nint Window(int pid) => (nint)(9000 + pid);
        public SessionObservation Client(int pid) => new(pid, 10_000 + pid, "Synthetic", Window(pid), "Synthetic client", false, true, 1, 1, null, DateTimeOffset.UtcNow);
        public TargetedInputTarget Target(int pid) => Executor.Register(Client(pid))!;
        public ValueTask DisposeAsync() => Owner.DisposeAsync();
    }

    /// <summary>Registers a client window as a target of the controlled transport, the way a verified transport would.</summary>
    private sealed class ControlledExecutor(TargetedInputCoordinator coordinator) : IKeepAwakeInputExecutor
    {
        private readonly ConcurrentDictionary<int, Guid> _registrations = new();
        private readonly ConcurrentDictionary<int, bool> _unregistered = new();
        public TargetedInputCoordinator Coordinator => coordinator;
        public Exception? RegistrationError { get; set; }
        public void Unregister(int pid) => _unregistered[pid] = true;
        public TargetedInputTarget? Register(SessionObservation client)
        {
            if (RegistrationError is { } error) throw error;
            if (_unregistered.ContainsKey(client.ProcessId)) return null;
            var registration = _registrations.GetOrAdd(client.ProcessId, _ => Guid.NewGuid());
            return new(registration, client.ProcessId, client.ProcessStartUtcTicks, client.Window, new PixelRectangle(0, 0, 800, 600), 1);
        }
    }

    private sealed class ControlledForeground(nint current) : IForegroundWindowSwitch
    {
        private readonly HashSet<nint> _refused = [];
        public List<nint> Requests { get; } = [];
        public nint Current { get; private set; } = current;
        public void Refuse(nint window) => _refused.Add(window);
        public bool BringForward(nint window)
        {
            Requests.Add(window);
            if (_refused.Contains(window)) return false;
            Current = window; return true;
        }
    }

    private sealed class ControlledTransport : ITargetedInputTransport
    {
        private readonly HashSet<nint> _rejected = [];
        public ConcurrentQueue<TargetedInputCommand> Attempts { get; } = new();
        public ConcurrentQueue<TargetedInputCommand> Accepted { get; } = new();
        public Action<TargetedInputCommand>? BeforeSubmit { get; set; }
        public void Reject(nint window) => _rejected.Add(window);
        public ValueTask<TargetedInputValidation> ValidateAsync(TargetedInputTarget target, bool cleanup, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(TargetedInputValidation.Current); }
        public ValueTask<TargetedInputSubmission> SubmitAsync(TargetedInputCommand command, Func<bool> admissionOpen, CancellationToken ct)
        {
            BeforeSubmit?.Invoke(command);
            ct.ThrowIfCancellationRequested();
            Attempts.Enqueue(command);
            if (_rejected.Contains(command.Target.Window) && !command.Cleanup) return ValueTask.FromResult(TargetedInputSubmission.Rejected);
            if (!admissionOpen()) return ValueTask.FromResult(TargetedInputSubmission.Rejected);
            Accepted.Enqueue(command);
            return ValueTask.FromResult(TargetedInputSubmission.Queued);
        }
    }
}
