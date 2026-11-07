using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Versioning;
using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class TargetedInputClearHoldsTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TargetedInputTarget Target(int number = 1) => new(Guid.NewGuid(), 400 + number, 1000 + number,
        (nint)(100 + number), new PixelRectangle(-100, 20, 640, 480), 1);
    private static TargetedInputSignal Down(int key) => new(TargetedInputKind.KeyDown, key);
    private static TargetedInputSignal Up(int key) => new(TargetedInputKind.KeyUp, key);

    [Fact]
    public async Task ClearReleasesHoldsAtLastPointerAndRetainsRunningLeaseForMoreActions()
    {
        await using var fixture = new Fixture();
        var target = Target(); var run = fixture.Open(target);
        await run.SubmitAsync([Down(65), new(TargetedInputKind.LeftDown, X: 10, Y: 11), new(TargetedInputKind.Move, X: 80, Y: 90)]).WaitAsync(Deadline);

        var cleared = await run.ClearHoldsAsync().WaitAsync(Deadline);

        Assert.Equal(TargetedInputRunState.Running, cleared.State);
        Assert.Equal(0, cleared.LogicalHolds); Assert.Equal(0, cleared.SubmittedHolds);
        Assert.Null(fixture.Owner.TryOpen(target, TargetedInputPurpose.MacroPlayback).Run);
        var releases = fixture.Transport.Accepted.Where(command => command.Cleanup).ToArray();
        Assert.Equal(2, releases.Length);
        Assert.Equal(TargetedInputKind.LeftUp, releases[0].Signal.Kind);
        Assert.Equal(80, releases[0].Signal.X); Assert.Equal(90, releases[0].Signal.Y);
        Assert.Equal(Up(65), releases[1].Signal);
        Assert.Equal(TargetedInputDispatchStatus.Submitted,
            (await run.SubmitAsync([Down(66), Up(66)]).WaitAsync(Deadline)).Status);
        Assert.Equal(cleared.RunId, run.Snapshot.RunId);
        Assert.Equal(TargetedInputRunState.Stopped, (await run.StopAsync().WaitAsync(Deadline)).State);
        Assert.NotNull(fixture.Owner.TryOpen(target, TargetedInputPurpose.MacroPlayback).Run);
    }

    [Fact]
    public async Task ClearWhilePausedDiscardsRestoreIntentWithoutResuming()
    {
        await using var fixture = new Fixture(); var run = fixture.Open(Target());
        await run.SubmitAsync([Down(65), Down(66)]).WaitAsync(Deadline);
        await run.PauseAsync().WaitAsync(Deadline);
        int before = fixture.Transport.Accepted.Count;

        var cleared = await run.ClearHoldsAsync().WaitAsync(Deadline);

        Assert.Equal(TargetedInputRunState.Paused, cleared.State);
        Assert.Equal(0, cleared.LogicalHolds); Assert.Equal(0, cleared.SubmittedHolds);
        Assert.Equal(before, fixture.Transport.Accepted.Count);
        Assert.Equal(TargetedInputRunState.Running, (await run.ResumeAsync().WaitAsync(Deadline)).State);
        Assert.Equal(before, fixture.Transport.Accepted.Count);
        Assert.Equal(TargetedInputDispatchStatus.Submitted,
            (await run.SubmitAsync([Down(67), Up(67)]).WaitAsync(Deadline)).Status);
    }

    [Fact]
    public async Task ClearDuringPauseJoinsReleaseAndLeavesNoRestorableHolds()
    {
        await using var fixture = new Fixture(); var run = fixture.Open(Target());
        await run.SubmitAsync([Down(65)]).WaitAsync(Deadline);
        var gate = fixture.NewGate(); fixture.Transport.Submission = async (command, admission, ct) =>
        {
            if (command.Cleanup) await gate.HoldAsync();
            return fixture.Transport.Accept(command, admission, ct);
        };
        var pause = run.PauseAsync(); await gate.EnteredAsync();
        var clear = run.ClearHoldsAsync();
        Assert.False(clear.IsCompleted);
        gate.Open();

        Assert.Equal(TargetedInputRunState.Paused, (await pause.WaitAsync(Deadline)).State);
        var result = await clear.WaitAsync(Deadline);
        Assert.Equal(TargetedInputRunState.Paused, result.State); Assert.Equal(0, result.LogicalHolds);
        int accepted = fixture.Transport.Accepted.Count;
        await run.ResumeAsync().WaitAsync(Deadline);
        Assert.Equal(accepted, fixture.Transport.Accepted.Count);
    }

    [Fact]
    public async Task PauseDuringClearWinsAndOrdinaryEmissionStaysClosed()
    {
        await using var fixture = new Fixture(); var run = fixture.Open(Target());
        await run.SubmitAsync([Down(65)]).WaitAsync(Deadline);
        var gate = fixture.BlockCleanup(); var clear = run.ClearHoldsAsync(); await gate.EnteredAsync();
        Assert.Equal(TargetedInputRunState.Clearing, run.Snapshot.State);
        Assert.Equal(TargetedInputDispatchStatus.Inactive, (await run.SubmitAsync([Down(66)])).Status);
        var pause = run.PauseAsync(); gate.Open();

        Assert.Equal(TargetedInputRunState.Paused, (await clear.WaitAsync(Deadline)).State);
        Assert.Equal(TargetedInputRunState.Paused, (await pause.WaitAsync(Deadline)).State);
        int accepted = fixture.Transport.Accepted.Count;
        await run.ResumeAsync().WaitAsync(Deadline);
        Assert.Equal(accepted, fixture.Transport.Accepted.Count);
    }

    [Fact]
    public async Task ClearDuringPartialRestorationReleasesPrefixAndRequiresExplicitResume()
    {
        await using var fixture = new Fixture(); var run = fixture.Open(Target());
        await run.SubmitAsync([Down(65), Down(66)]).WaitAsync(Deadline);
        await run.PauseAsync().WaitAsync(Deadline);
        var gate = fixture.NewGate(); fixture.Transport.Submission = async (command, admission, ct) =>
        {
            if (!command.Cleanup && command.Signal == Down(66)) await gate.HoldAsync();
            return fixture.Transport.Accept(command, admission, ct);
        };
        int before = fixture.Transport.Accepted.Count;
        var resume = run.ResumeAsync(); await gate.EnteredAsync();
        Assert.Equal(2, run.Snapshot.SubmittedHolds); // Includes the second, still possibly submitted press.
        var clear = run.ClearHoldsAsync(); gate.Open();

        Assert.Equal(TargetedInputRunState.Paused, (await resume.WaitAsync(Deadline)).State);
        var result = await clear.WaitAsync(Deadline);
        Assert.Equal(TargetedInputRunState.Paused, result.State);
        Assert.Equal(0, result.LogicalHolds); Assert.Equal(0, result.SubmittedHolds);
        var suffix = fixture.Transport.Accepted.Skip(before).ToArray();
        Assert.Equal(2, suffix.Length); Assert.Equal(Down(65), suffix[0].Signal);
        Assert.True(suffix[1].Cleanup); Assert.Equal(Up(65), suffix[1].Signal);
        int accepted = fixture.Transport.Accepted.Count;
        await run.ResumeAsync().WaitAsync(Deadline);
        Assert.Equal(accepted, fixture.Transport.Accepted.Count);
    }

    [Fact]
    public async Task StopDuringClearSharesOwnerAndDoesNotReturnToRunning()
    {
        await using var fixture = new Fixture(); var target = Target(); var run = fixture.Open(target);
        await run.SubmitAsync([Down(65)]).WaitAsync(Deadline);
        var gate = fixture.BlockCleanup(); var clear = run.ClearHoldsAsync(); await gate.EnteredAsync();
        var stop = run.StopAsync(); Assert.Same(clear, stop); gate.Open();

        Assert.Equal(TargetedInputRunState.Stopped, (await clear.WaitAsync(Deadline)).State);
        Assert.Equal(TargetedInputRunState.Stopped, (await stop.WaitAsync(Deadline)).State);
        Assert.Equal(0, run.Snapshot.SubmittedHolds);
        Assert.NotNull(fixture.Owner.TryOpen(target, TargetedInputPurpose.MacroPlayback).Run);
    }

    [Fact]
    public async Task FaultCleanupCannotBeDowngradedByClear()
    {
        await using var fixture = new Fixture(); var run = fixture.Open(Target());
        var gate = fixture.NewGate(); fixture.Transport.Submission = async (command, admission, ct) =>
        {
            if (!command.Cleanup) return TargetedInputSubmission.Uncertain;
            await gate.HoldAsync(); return fixture.Transport.Accept(command, admission, ct);
        };
        var batch = run.SubmitAsync([Down(65)]); await gate.EnteredAsync();
        var clear = run.ClearHoldsAsync(); gate.Open();

        Assert.Equal(TargetedInputDispatchStatus.Faulted, (await batch.WaitAsync(Deadline)).Status);
        Assert.Equal(TargetedInputRunState.Faulted, (await clear.WaitAsync(Deadline)).State);
        Assert.Null(fixture.Owner.TryOpen(run.Target, TargetedInputPurpose.MacroPlayback).Run);
        Assert.Equal(TargetedInputRunState.Stopped, (await run.RetryCleanupAsync().WaitAsync(Deadline)).State);
    }

    [Fact]
    public async Task LateUncertainSubmissionUpgradesPendingClearToFault()
    {
        await using var fixture = new Fixture(); var run = fixture.Open(Target()); var gate = fixture.NewGate();
        fixture.Transport.Submission = async (command, admission, ct) =>
        {
            if (!command.Cleanup) { await gate.HoldAsync(); return TargetedInputSubmission.Uncertain; }
            return fixture.Transport.Accept(command, admission, ct);
        };
        var batch = run.SubmitAsync([Down(65)]); await gate.EnteredAsync();
        var clear = run.ClearHoldsAsync(); Assert.Equal(TargetedInputRunState.Clearing, run.Snapshot.State); gate.Open();

        Assert.Equal(TargetedInputDispatchStatus.Faulted, (await batch.WaitAsync(Deadline)).Status);
        var result = await clear.WaitAsync(Deadline);
        Assert.Equal(TargetedInputRunState.Faulted, result.State);
        Assert.Equal(0, result.SubmittedHolds); Assert.Equal(0, result.LogicalHolds);
        Assert.Null(fixture.Owner.TryOpen(run.Target, TargetedInputPurpose.MacroPlayback).Run);
        Assert.Single(fixture.Transport.Accepted.Where(command => command.Cleanup));
    }

    [Theory]
    [InlineData(TargetedInputSubmission.Rejected)]
    [InlineData(TargetedInputSubmission.Uncertain)]
    public async Task FailedClearKeepsSubmittedOwnershipAndLeaseUntilExplicitCleanupRetry(TargetedInputSubmission failure)
    {
        await using var fixture = new Fixture(); var target = Target(); var run = fixture.Open(target);
        await run.SubmitAsync([Down(65)]).WaitAsync(Deadline);
        fixture.Transport.Submission = (command, admission, ct) => ValueTask.FromResult(command.Cleanup
            ? failure : fixture.Transport.Accept(command, admission, ct));

        var result = await run.ClearHoldsAsync().WaitAsync(Deadline);

        Assert.Equal(TargetedInputRunState.Faulted, result.State);
        Assert.Equal(0, result.LogicalHolds); Assert.Equal(1, result.SubmittedHolds);
        Assert.Null(fixture.Owner.TryOpen(target, TargetedInputPurpose.MacroPlayback).Run);
        int attempts = fixture.Transport.Attempts.Count;
        Assert.Equal(TargetedInputRunState.Faulted, (await run.ClearHoldsAsync()).State);
        Assert.Equal(attempts, fixture.Transport.Attempts.Count);
        fixture.Transport.Submission = null;
        Assert.Equal(TargetedInputRunState.Stopped, (await run.RetryCleanupAsync().WaitAsync(Deadline)).State);
        Assert.NotNull(fixture.Owner.TryOpen(target, TargetedInputPurpose.MacroPlayback).Run);
    }

    [Fact]
    public async Task ThrowingClearKeepsQuarantineAndAllowsSuccessfulExplicitRetry()
    {
        await using var fixture = new Fixture(); var run = fixture.Open(Target());
        await run.SubmitAsync([Down(65)]).WaitAsync(Deadline);
        fixture.Transport.Submission = (_, _, _) => throw new InvalidOperationException("synthetic transport failure");

        var result = await run.ClearHoldsAsync().WaitAsync(Deadline);

        Assert.Equal(TargetedInputRunState.Faulted, result.State);
        Assert.Equal(1, result.SubmittedHolds); Assert.Equal(0, result.LogicalHolds);
        Assert.DoesNotContain("synthetic", result.Explanation);
        fixture.Transport.Submission = null;
        Assert.Equal(TargetedInputRunState.Stopped, (await run.RetryCleanupAsync().WaitAsync(Deadline)).State);
    }

    [Fact]
    public async Task FailedClearThenFailedDisposeStillRequiresOwnedCleanupBeforeDisposalCanComplete()
    {
        await using var fixture = new Fixture(); var run = fixture.Open(Target());
        await run.SubmitAsync([Down(65)]).WaitAsync(Deadline);
        fixture.Transport.Submission = (_, _, _) => ValueTask.FromResult(TargetedInputSubmission.Rejected);
        Assert.Equal(TargetedInputRunState.Faulted, (await run.ClearHoldsAsync().WaitAsync(Deadline)).State);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Owner.DisposeAsync().AsTask().WaitAsync(Deadline));

        Assert.Equal(TargetedInputRunState.Faulted, run.Snapshot.State); Assert.Equal(1, run.Snapshot.SubmittedHolds);
        fixture.Transport.Submission = null;
        Assert.Equal(TargetedInputRunState.Stopped, (await run.RetryCleanupAsync().WaitAsync(Deadline)).State);
        await fixture.Owner.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.Equal(0, fixture.Transport.ActiveCalls);
    }

    [Fact]
    public async Task ClearAfterAnAlreadyCommittedBlockedDownCleansPrefixAndDoesNotSubmitTail()
    {
        await using var fixture = new Fixture(); var run = fixture.Open(Target()); var gate = fixture.NewGate();
        fixture.Transport.Submission = async (command, admission, ct) =>
        {
            if (!command.Cleanup)
            {
                // Model a commit that passed native admission before Clear but whose acknowledgement is late.
                Assert.True(admission()); await gate.HoldAsync(); fixture.Transport.Accepted.Enqueue(command);
                return TargetedInputSubmission.Queued;
            }
            return fixture.Transport.Accept(command, admission, ct);
        };
        var batch = run.SubmitAsync([Down(65), Down(66)]); await gate.EnteredAsync();
        var clear = run.ClearHoldsAsync(); gate.Open();

        var dispatched = await batch.WaitAsync(Deadline);
        Assert.Equal(TargetedInputDispatchStatus.Inactive, dispatched.Status); Assert.Equal(1, dispatched.SubmittedCount);
        var result = await clear.WaitAsync(Deadline);
        Assert.Equal(TargetedInputRunState.Running, result.State);
        Assert.Equal(0, result.LogicalHolds); Assert.Equal(0, result.SubmittedHolds);
        Assert.Equal(new[] { Down(65), Up(65) }, fixture.Transport.Accepted.Select(command => command.Signal));
    }

    [Fact]
    public async Task CancellationOfBlockedEmissionPreemptsConcurrentClearAndFinalizesStop()
    {
        await using var fixture = new Fixture(); var run = fixture.Open(Target()); var gate = fixture.NewGate();
        using var cancellation = new CancellationTokenSource();
        fixture.Transport.Submission = async (command, admission, ct) =>
        {
            if (!command.Cleanup)
            {
                Assert.True(admission()); await gate.HoldAsync(); fixture.Transport.Accepted.Enqueue(command);
                return TargetedInputSubmission.Queued;
            }
            return fixture.Transport.Accept(command, admission, ct);
        };
        var batch = run.SubmitAsync([Down(65), Down(66)], cancellation.Token); await gate.EnteredAsync();
        var clear = run.ClearHoldsAsync(); cancellation.Cancel(); gate.Open();

        Assert.Equal(TargetedInputDispatchStatus.Cancelled, (await batch.WaitAsync(Deadline)).Status);
        Assert.Equal(TargetedInputRunState.Stopped, (await clear.WaitAsync(Deadline)).State);
        Assert.Equal(new[] { Down(65), Up(65) }, fixture.Transport.Accepted.Select(command => command.Signal));
    }

    [Fact]
    public async Task RepeatedClearRequestsShareOneTransitionAndOneRelease()
    {
        await using var fixture = new Fixture(); var run = fixture.Open(Target());
        await run.SubmitAsync([Down(65)]).WaitAsync(Deadline);
        var gate = fixture.BlockCleanup(); var clear = run.ClearHoldsAsync(); await gate.EnteredAsync();
        long generation = run.Snapshot.Generation;
        for (int index = 0; index < 100; index++) Assert.Same(clear, run.ClearHoldsAsync());
        Assert.Equal(generation, run.Snapshot.Generation); gate.Open();

        Assert.Equal(TargetedInputRunState.Running, (await clear.WaitAsync(Deadline)).State);
        Assert.Single(fixture.Transport.Attempts.Where(command => command.Cleanup));
    }

    [Fact]
    public async Task ClearUsesTheSameGlobalTransportLaneAsAnotherRun()
    {
        await using var fixture = new Fixture(); var first = fixture.Open(Target()); var second = fixture.Open(Target(2));
        await first.SubmitAsync([Down(65)]).WaitAsync(Deadline);
        var gate = fixture.BlockCleanup(); var clear = first.ClearHoldsAsync(); await gate.EnteredAsync();
        var secondBatch = second.SubmitAsync([Down(66), Up(66)]);
        Assert.False(secondBatch.IsCompleted); Assert.Equal(1, fixture.Transport.MaximumConcurrentCalls);
        gate.Open(); await clear.WaitAsync(Deadline);

        Assert.Equal(TargetedInputDispatchStatus.Submitted, (await secondBatch.WaitAsync(Deadline)).Status);
        Assert.Equal(1, fixture.Transport.MaximumConcurrentCalls);
        var accepted = fixture.Transport.Accepted.ToArray();
        Assert.True(accepted[1].Cleanup); Assert.Equal(first.Snapshot.RunId, accepted[1].RunId);
        Assert.Equal(second.Snapshot.RunId, accepted[2].RunId);
    }

    [Fact]
    public async Task DisposeDuringClearJoinsActualCleanupAndStopWins()
    {
        await using var fixture = new Fixture(); var run = fixture.Open(Target());
        await run.SubmitAsync([Down(65)]).WaitAsync(Deadline);
        var gate = fixture.BlockCleanup(); var clear = run.ClearHoldsAsync(); await gate.EnteredAsync();
        var disposal = fixture.Owner.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted); Assert.Equal(1, fixture.Transport.ActiveCalls); gate.Open();

        await disposal.WaitAsync(Deadline);
        Assert.Equal(TargetedInputRunState.Stopped, (await clear.WaitAsync(Deadline)).State);
        Assert.Equal(0, fixture.Transport.ActiveCalls);
        Assert.Null(fixture.Owner.TryOpen(Target(3), TargetedInputPurpose.MacroPlayback).Run);
    }

    [Fact]
    public async Task EmptyClearRetainsLeaseAndStoppedClearCreatesNoTransportWork()
    {
        await using var fixture = new Fixture(); var target = Target(); var run = fixture.Open(target);
        Assert.Equal(TargetedInputRunState.Running, (await run.ClearHoldsAsync().WaitAsync(Deadline)).State);
        Assert.Empty(fixture.Transport.Attempts);
        Assert.Null(fixture.Owner.TryOpen(target, TargetedInputPurpose.MacroPlayback).Run);
        await run.StopAsync().WaitAsync(Deadline); long generation = run.Snapshot.Generation;

        Assert.Equal(TargetedInputRunState.Stopped, (await run.ClearHoldsAsync()).State);
        Assert.Equal(generation, run.Snapshot.Generation); Assert.Empty(fixture.Transport.Attempts);
    }

    [Fact]
    public async Task ExposedHoldBudgetIsTheActualConfiguredOwnerBudgetWithoutChangingTheLease()
    {
        await using var defaults = new Fixture(); Assert.Equal(64, defaults.Owner.MaximumHoldsPerRun);
        await using var fixture = new Fixture(new TargetedInputLimits(MaximumHolds: 1));
        Assert.Equal(1, fixture.Owner.MaximumHoldsPerRun);
        var run = fixture.Open(Target()); var before = run.Snapshot;
        Assert.Equal(1, fixture.Owner.MaximumHoldsPerRun);
        Assert.Equal(before, run.Snapshot); Assert.Empty(fixture.Transport.Attempts);

        var result = await run.SubmitAsync([Down(65), Down(66)]).WaitAsync(Deadline);

        Assert.Equal(TargetedInputDispatchStatus.Faulted, result.Status); Assert.Equal(1, result.SubmittedCount);
        Assert.Equal(new[] { Down(65), Up(65) }, fixture.Transport.Accepted.Select(command => command.Signal));
        Assert.Equal(1, fixture.Owner.MaximumHoldsPerRun);
        Assert.Equal(TargetedInputRunState.Faulted, result.Run.State);
    }

    [Fact]
    public void EveryMovedPublicTypeIsForwardedAndOriginalStateValuesStayStable()
    {
        Type[] moved = [typeof(PixelRectangle), typeof(TargetedInputTarget), typeof(TargetedInputPurpose),
            typeof(TargetedInputKind), typeof(TargetedInputRunState), typeof(TargetedInputValidation),
            typeof(TargetedInputSubmission), typeof(TargetedInputDispatchStatus), typeof(TargetedInputSignal),
            typeof(TargetedInputModifiers), typeof(TargetedInputCommand), typeof(TargetedInputRunSnapshot),
            typeof(TargetedInputDispatchResult), typeof(TargetedInputOpenResult), typeof(TargetedInputLimits),
            typeof(ITargetedInputTransport), typeof(TargetedInputCoordinator), typeof(TargetedInputRun)];
        var originalAssembly = typeof(AccountProfile).Assembly;
        var sharedAssembly = typeof(TargetedInputCoordinator).Assembly;
        Assert.NotSame(originalAssembly, sharedAssembly);
        Assert.Equal("Zenith.Automation.Input", sharedAssembly.GetName().Name);
        Assert.Equal(".NETCoreApp,Version=v8.0", sharedAssembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName);
        foreach (var type in moved)
        {
            Assert.Same(sharedAssembly, type.Assembly);
            Assert.Equal("Zenith.Sessions.Core", type.Namespace);
            Assert.Contains(type, originalAssembly.GetForwardedTypes());
        }
        Assert.Equal(0, (int)TargetedInputRunState.Running); Assert.Equal(1, (int)TargetedInputRunState.Pausing);
        Assert.Equal(2, (int)TargetedInputRunState.Paused); Assert.Equal(3, (int)TargetedInputRunState.Resuming);
        Assert.Equal(4, (int)TargetedInputRunState.Stopping); Assert.Equal(5, (int)TargetedInputRunState.Stopped);
        Assert.Equal(6, (int)TargetedInputRunState.Faulted); Assert.Equal(7, (int)TargetedInputRunState.Clearing);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly List<Gate> _gates = [];
        public Transport Transport { get; } = new();
        public TargetedInputCoordinator Owner { get; }
        public Fixture(TargetedInputLimits? limits = null) => Owner = new(Transport, limits);
        public TargetedInputRun Open(TargetedInputTarget target) => Owner.TryOpen(target, TargetedInputPurpose.MacroPlayback).Run
            ?? throw new InvalidOperationException("The synthetic target could not be opened.");
        public Gate NewGate() { var gate = new Gate(); _gates.Add(gate); return gate; }
        public Gate BlockCleanup()
        {
            var gate = NewGate(); Transport.Submission = async (command, admission, ct) =>
            {
                if (command.Cleanup) await gate.HoldAsync();
                return Transport.Accept(command, admission, ct);
            };
            return gate;
        }
        public async ValueTask DisposeAsync()
        {
            Transport.Submission = null;
            foreach (var gate in _gates) gate.Open();
            await Owner.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    private sealed class Gate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task EnteredAsync() => _entered.Task.WaitAsync(Deadline);
        public async Task HoldAsync() { _entered.TrySetResult(); await _release.Task; }
        public void Open() => _release.TrySetResult();
    }

    private sealed class Transport : ITargetedInputTransport
    {
        public readonly ConcurrentQueue<TargetedInputCommand> Attempts = new();
        public readonly ConcurrentQueue<TargetedInputCommand> Accepted = new();
        public Func<TargetedInputCommand, Func<bool>, CancellationToken, ValueTask<TargetedInputSubmission>>? Submission;
        private int _active, _maximum;
        public int ActiveCalls => Volatile.Read(ref _active);
        public int MaximumConcurrentCalls => Volatile.Read(ref _maximum);
        public ValueTask<TargetedInputValidation> ValidateAsync(TargetedInputTarget target, bool cleanup, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(TargetedInputValidation.Current); }
        public async ValueTask<TargetedInputSubmission> SubmitAsync(TargetedInputCommand command, Func<bool> admissionOpen, CancellationToken ct)
        {
            int active = Interlocked.Increment(ref _active), previous;
            do { previous = Volatile.Read(ref _maximum); if (active <= previous) break; }
            while (Interlocked.CompareExchange(ref _maximum, active, previous) != previous);
            try
            {
                Attempts.Enqueue(command);
                return Submission is { } behavior ? await behavior(command, admissionOpen, ct) : Accept(command, admissionOpen, ct);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public TargetedInputSubmission Accept(TargetedInputCommand command, Func<bool> admissionOpen, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!admissionOpen()) return TargetedInputSubmission.Rejected;
            Accepted.Enqueue(command); return TargetedInputSubmission.Queued;
        }
    }
}
