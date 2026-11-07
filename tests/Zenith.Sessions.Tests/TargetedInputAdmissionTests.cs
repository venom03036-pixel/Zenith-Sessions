using System.Collections.Concurrent;
using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class TargetedInputAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static TargetedInputTarget NewTarget() => new(Guid.NewGuid(), 500, 10_000, 101, new(-200, 40, 640, 480), 1);
    private static TargetedInputSignal Down(int key) => new(TargetedInputKind.KeyDown, key);
    private static TargetedInputSignal Up(int key) => new(TargetedInputKind.KeyUp, key);

    [Fact]
    public async Task ClosedAdmissionDoesNotReserveTargetAndTheLegacyDiagnosticOverloadStillWorks()
    {
        var transport = new ProbeTransport();
        await using var owner = new TargetedInputCoordinator(transport);
        var target = NewTarget();
        var admission = new Admission();
        admission.Stop();

        Assert.Null(owner.TryOpen(target, TargetedInputPurpose.MacroPlayback, admission).Run);
        Assert.Empty(transport.Validations);
        Assert.Empty(transport.Attempts);

        var diagnostic = Assert.IsType<TargetedInputRun>(owner.TryOpen(target, TargetedInputPurpose.OwnedWindowDiagnostic).Run);
        var result = await diagnostic.SubmitAsync([Down(65), Up(65)]).WaitAsync(Deadline);
        Assert.Equal(TargetedInputDispatchStatus.Submitted, result.Status);
        Assert.Equal(new[] { Down(65), Up(65) }, transport.Accepted.Select(command => command.Signal));
    }

    [Fact]
    public async Task RevocationBeforeBatchAdmissionDoesNotReachTheTransport()
    {
        var transport = new ProbeTransport();
        var admission = new Admission();
        await using var owner = new TargetedInputCoordinator(transport);
        var run = Open(owner, admission);
        admission.Stop();

        var result = await run.SubmitAsync([Down(65)]).WaitAsync(Deadline);

        Assert.Equal(TargetedInputDispatchStatus.Inactive, result.Status);
        Assert.Equal(0, result.SubmittedCount);
        Assert.Empty(transport.Validations);
        Assert.Empty(transport.Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalOrdinaryAdmissionRetainsOnlyTheAcceptedPrefixAndAllowsExactCleanup(bool cancelCaller)
    {
        var transport = new ProbeTransport();
        var admission = new Admission();
        var gate = new BoundaryGate();
        using var caller = new CancellationTokenSource();
        await using var owner = new TargetedInputCoordinator(transport);
        var run = Open(owner, admission);
        transport.BeforeCommit = command => command.Signal == Down(66) ? gate.HoldAsync() : Task.CompletedTask;
        var batch = run.SubmitAsync([Down(65), Down(66), Up(66), Up(65)], caller.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            Assert.Equal(Down(65), Assert.Single(transport.Accepted).Signal);
            admission.Stop();
            if (cancelCaller) caller.Cancel();
        }
        finally { gate.Release.TrySetResult(); }

        var result = await batch.WaitAsync(Deadline);
        Assert.Equal(cancelCaller ? TargetedInputDispatchStatus.Cancelled : TargetedInputDispatchStatus.Inactive, result.Status);
        Assert.Equal(1, result.SubmittedCount);
        Assert.Equal(0, result.UncertainCount);
        Assert.Equal(cancelCaller ? 0 : 1, result.Run.SubmittedHolds);
        Assert.Equal(TargetedInputRunState.Stopped, (await run.StopAsync().WaitAsync(Deadline)).State);
        Assert.Equal(new[] { Down(65), Up(65) }, transport.Accepted.Select(command => command.Signal));
        var release = Assert.Single(transport.Accepted.Where(command => command.Cleanup));
        Assert.Equal(run.Target, release.Target);
        Assert.Equal(result.Run.RunId, release.RunId);
        var cleanupToken = Assert.Single(transport.CleanupTokens);
        Assert.NotEqual(caller.Token, cleanupToken);
        Assert.False(cleanupToken.IsCancellationRequested);
    }

    [Fact]
    public async Task StopJoinsAnAlreadyCommittedDownBeforeReleasingItsHold()
    {
        var transport = new ProbeTransport();
        var admission = new Admission();
        var gate = new BoundaryGate();
        using var caller = new CancellationTokenSource();
        await using var owner = new TargetedInputCoordinator(transport);
        var run = Open(owner, admission);
        transport.AfterCommit = command => command.Signal == Down(65) ? gate.HoldAsync() : Task.CompletedTask;
        var batch = run.SubmitAsync([Down(65), Up(65)], caller.Token);
        Task<TargetedInputRunSnapshot>? stop = null;
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            admission.Stop();
            caller.Cancel();
            stop = run.StopAsync();
            Assert.False(stop.IsCompleted);
            Assert.False(batch.IsCompleted);
            Assert.Equal(Down(65), Assert.Single(transport.Accepted).Signal);
        }
        finally { gate.Release.TrySetResult(); }

        var result = await batch.WaitAsync(Deadline);
        Assert.Equal(TargetedInputDispatchStatus.Cancelled, result.Status);
        Assert.Equal(1, result.SubmittedCount);
        Assert.Equal(0, result.UncertainCount);
        Assert.Equal(TargetedInputRunState.Stopped, (await stop!.WaitAsync(Deadline)).State);
        Assert.Equal(Up(65), Assert.Single(transport.Accepted.Where(command => command.Cleanup)).Signal);
        Assert.False(Assert.Single(transport.CleanupTokens).IsCancellationRequested);
    }

    [Fact]
    public async Task LosingCleanupOwnershipAtFinalCommitRetainsTheHoldAndRejectsAnotherOwner()
    {
        var transport = new ProbeTransport();
        var admission = new Admission();
        var gate = new BoundaryGate();
        await using var owner = new TargetedInputCoordinator(transport);
        var run = Open(owner, admission);
        await run.SubmitAsync([Down(65)]).WaitAsync(Deadline);
        admission.Stop();
        transport.BeforeCommit = command => command.Cleanup ? gate.HoldAsync() : Task.CompletedTask;
        var stopping = run.StopAsync();
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            admission.SetCleanupOwnership(false);
            gate.Release.TrySetResult();
            var stopped = await stopping.WaitAsync(Deadline);

            Assert.Equal(TargetedInputRunState.Faulted, stopped.State);
            Assert.Equal(1, stopped.SubmittedHolds);
            Assert.DoesNotContain(transport.Accepted, command => command.Cleanup);
            Assert.Null(owner.TryOpen(run.Target, TargetedInputPurpose.MacroPlayback, new Admission()).Run);
        }
        finally
        {
            gate.Release.TrySetResult();
            await stopping.WaitAsync(Deadline);
            admission.SetCleanupOwnership(true);
            await run.RetryCleanupAsync().WaitAsync(Deadline);
        }
        var released = Assert.Single(transport.Accepted.Where(command => command.Cleanup));
        Assert.Equal(Up(65), released.Signal);
        Assert.Equal(run.Target, released.Target);
    }

    [Fact]
    public async Task LosingCleanupOwnershipDuringValidationCannotRetireSubmittedHolds()
    {
        var transport = new ProbeTransport();
        var admission = new Admission();
        var gate = new BoundaryGate();
        await using var owner = new TargetedInputCoordinator(transport);
        var run = Open(owner, admission);
        await run.SubmitAsync([Down(65)]).WaitAsync(Deadline);
        admission.Stop();
        transport.Validate = async cleanup =>
        {
            if (!cleanup) return TargetedInputValidation.Current;
            await gate.HoldAsync();
            return TargetedInputValidation.Gone;
        };
        var stopping = run.StopAsync();
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            admission.SetCleanupOwnership(false);
            gate.Release.TrySetResult();
            var stopped = await stopping.WaitAsync(Deadline);

            Assert.Equal(TargetedInputRunState.Faulted, stopped.State);
            Assert.Equal(1, stopped.SubmittedHolds);
            Assert.DoesNotContain(transport.Attempts, command => command.Cleanup);
        }
        finally
        {
            gate.Release.TrySetResult();
            await stopping.WaitAsync(Deadline);
            admission.SetCleanupOwnership(true);
            transport.Validate = null;
            await run.RetryCleanupAsync().WaitAsync(Deadline);
        }
        Assert.Equal(Up(65), Assert.Single(transport.Accepted.Where(command => command.Cleanup)).Signal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationAroundFinalRestoreCommitReleasesOnlyTheActuallyRestoredPrefix(bool alreadyCommitted)
    {
        var transport = new ProbeTransport();
        var admission = new Admission();
        var gate = new BoundaryGate();
        await using var owner = new TargetedInputCoordinator(transport);
        var run = Open(owner, admission);
        await run.SubmitAsync([Down(65), Down(66)]).WaitAsync(Deadline);
        Assert.Equal(TargetedInputRunState.Paused, (await run.PauseAsync().WaitAsync(Deadline)).State);
        int acceptedBeforeResume = transport.Accepted.Count;
        Task HoldSecondRestore(TargetedInputCommand command)
            => command.Signal == Down(66) ? gate.HoldAsync() : Task.CompletedTask;
        if (alreadyCommitted) transport.AfterCommit = HoldSecondRestore;
        else transport.BeforeCommit = HoldSecondRestore;
        var resume = run.ResumeAsync();
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            admission.Stop();
            Assert.False(resume.IsCompleted);
        }
        finally { gate.Release.TrySetResult(); }

        var stopped = await resume.WaitAsync(Deadline);
        Assert.Equal(TargetedInputRunState.Stopped, stopped.State);
        Assert.Equal(0, stopped.LogicalHolds);
        Assert.Equal(0, stopped.SubmittedHolds);
        var accepted = transport.Accepted.Skip(acceptedBeforeResume).ToArray();
        var expected = alreadyCommitted ? new[] { Down(65), Down(66), Up(66), Up(65) } : new[] { Down(65), Up(65) };
        Assert.Equal(expected, accepted.Select(command => command.Signal));
        Assert.All(accepted.Where(command => command.Signal.Kind == TargetedInputKind.KeyUp), command => Assert.True(command.Cleanup));
    }

    [Fact]
    public async Task RevokedPausedOwnerCannotBeginAnotherRestoration()
    {
        var transport = new ProbeTransport();
        var admission = new Admission();
        await using var owner = new TargetedInputCoordinator(transport);
        var run = Open(owner, admission);
        await run.SubmitAsync([Down(65)]).WaitAsync(Deadline);
        await run.PauseAsync().WaitAsync(Deadline);
        int validationsBeforeResume = transport.Validations.Count;
        int attemptsBeforeResume = transport.Attempts.Count;
        admission.Stop();

        var resumed = await run.ResumeAsync().WaitAsync(Deadline);

        Assert.Equal(TargetedInputRunState.Paused, resumed.State);
        Assert.Equal(validationsBeforeResume, transport.Validations.Count);
        Assert.Equal(attemptsBeforeResume, transport.Attempts.Count);
    }

    private static TargetedInputRun Open(TargetedInputCoordinator owner, Admission admission)
        => Assert.IsType<TargetedInputRun>(owner.TryOpen(NewTarget(), TargetedInputPurpose.MacroPlayback, admission).Run);

    private sealed class Admission : ITargetedInputAdmission
    {
        private int _open = 1;
        private int _ownsCleanup = 1;
        public bool CanSubmit => Volatile.Read(ref _open) != 0 && OwnsCleanup;
        public bool OwnsCleanup => Volatile.Read(ref _ownsCleanup) != 0;
        public void Stop() => Volatile.Write(ref _open, 0);
        public void SetCleanupOwnership(bool value) => Volatile.Write(ref _ownsCleanup, value ? 1 : 0);
    }

    private sealed class BoundaryGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task HoldAsync()
        {
            Entered.TrySetResult();
            return Release.Task;
        }
    }

    private sealed class ProbeTransport : ITargetedInputTransport
    {
        public ConcurrentQueue<bool> Validations { get; } = new();
        public ConcurrentQueue<TargetedInputCommand> Attempts { get; } = new();
        public ConcurrentQueue<TargetedInputCommand> Accepted { get; } = new();
        public ConcurrentQueue<CancellationToken> CleanupTokens { get; } = new();
        public Func<bool, Task<TargetedInputValidation>>? Validate { get; set; }
        public Func<TargetedInputCommand, Task>? BeforeCommit { get; set; }
        public Func<TargetedInputCommand, Task>? AfterCommit { get; set; }

        public async ValueTask<TargetedInputValidation> ValidateAsync(TargetedInputTarget target, bool cleanup, CancellationToken ct)
        {
            Validations.Enqueue(cleanup);
            return Validate is { } validate ? await validate(cleanup) : TargetedInputValidation.Current;
        }

        public async ValueTask<TargetedInputSubmission> SubmitAsync(TargetedInputCommand command, Func<bool> admissionOpen, CancellationToken ct)
        {
            Attempts.Enqueue(command);
            if (BeforeCommit is { } before) await before(command);
            if (!admissionOpen()) return TargetedInputSubmission.Rejected;
            Accepted.Enqueue(command);
            if (command.Cleanup) CleanupTokens.Enqueue(ct);
            if (AfterCommit is { } after) await after(command);
            return TargetedInputSubmission.Queued;
        }
    }
}
