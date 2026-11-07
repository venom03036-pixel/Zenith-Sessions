using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using Xunit;
using Zenith.MacroHost.Execution;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class TargetedMacroExecutionTests
{
    private static readonly TimeSpan HostDeadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(HostInputOperationKind.Playback)]
    [InlineData(HostInputOperationKind.Recording)]
    [InlineData(HostInputOperationKind.AutoClicker)]
    [InlineData(HostInputOperationKind.Picker)]
    [InlineData(HostInputOperationKind.CaptureTest)]
    [InlineData(HostInputOperationKind.Companion)]
    public async Task CompanionOperationRefusesAnOccupiedPersonalHostBeforeReading(HostInputOperationKind kind)
    {
        await using var f = new Fixture();
        var source = new HostDocument(Model(Key()));
        await using var preparer = new SavedMacroPlanPreparer(source);
        var host = new HostInputOperationArbiter();
        var operation = new CompanionMacroOperations(host, preparer, f.Runner);
        Assert.True(host.TryAcquire(kind, out var active, out _));
        try
        {
            var result = await operation.RunAsync(source.Reference, [f.A], host.StopGeneration);
            Assert.Equal(CompanionMacroOperationStatus.Busy, result.Status);
            Assert.Null(result.OperationId);
            Assert.Equal(0, source.Reads);
            Assert.Empty(f.Transport.Commands);
            Assert.True(active!.CanContinue);
        }
        finally { await active!.CompleteAsync(true); }
    }

    [Fact]
    public async Task CompanionRejectsStaleGenerationAndDuplicateWindowBeforeReading()
    {
        await using var f = new Fixture();
        var source = new HostDocument(Model(Key()));
        await using var preparer = new SavedMacroPlanPreparer(source);
        var host = new HostInputOperationArbiter();
        var operation = new CompanionMacroOperations(host, preparer, f.Runner);
        long generation = host.StopGeneration;
        host.SignalStop();
        Assert.Equal(CompanionMacroOperationStatus.Stopped, (await operation.RunAsync(source.Reference, [f.A], generation)).Status);
        Assert.Equal(CompanionMacroOperationStatus.Rejected, (await operation.RunAsync(source.Reference,
            [f.A, f.B with { Window = f.A.Window }], host.StopGeneration)).Status);
        Assert.Equal(0, source.Reads);
        Assert.False(host.Snapshot.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostOrRunnerStopJoinsAnIgnoringSavedReadBeforeAllowingAReplacementActivity(bool runnerPanic)
    {
        await using var f = new Fixture();
        var entered = Gate(); var unblock = Gate();
        var source = new HostDocument(Model(Key())) { BeforeRead = async () => { entered.TrySetResult(); await unblock.Task; } };
        await using var preparer = new SavedMacroPlanPreparer(source);
        var host = new HostInputOperationArbiter();
        var operation = new CompanionMacroOperations(host, preparer, f.Runner);
        var run = operation.RunAsync(source.Reference, [f.A], host.StopGeneration);
        try
        {
            await entered.Task.WaitAsync(HostDeadline);
            Assert.Equal(HostInputOperationKind.Companion, host.Snapshot.Kind);
            Assert.Equal(TargetedMacroStartStatus.Rejected, f.Runner.TryStart(Plan(Key()), f.B).Status);
            if (runnerPanic) f.Runner.SignalPanic(); else host.SignalStop();
            await Until(() => source.ReadToken.IsCancellationRequested);
            Assert.False(run.IsCompleted);
            Assert.False(host.TryAcquire(HostInputOperationKind.Recording, out _, out _));
            Assert.Empty(f.Transport.Commands);
        }
        finally { unblock.TrySetResult(); }
        var result = await run.WaitAsync(HostDeadline);
        Assert.Equal(CompanionMacroOperationStatus.Stopped, result.Status);
        Assert.Equal(SavedMacroPreparationStatus.Cancelled, result.Preparation);
        Assert.Empty(result.Recipients);
        Assert.Empty(f.Transport.Commands);
        Assert.True(host.TryAcquire(HostInputOperationKind.Recording, out var recording, out _));
        await recording!.CompleteAsync(true);
    }

    [Fact]
    public async Task RunnerPanicDuringOneRecipientsPreflightFencesEveryLaterRecipient()
    {
        Action? panic = null;
        var checkedRecipients = new List<int>();
        await using var f = new Fixture(eligible: (target, _) =>
        {
            checkedRecipients.Add(target.ProcessId);
            if (target.ProcessId == 2) panic!();
            return true;
        });
        panic = f.Runner.SignalPanic;
        var third = Target(3);
        f.Transport.Register(third);
        var source = new HostDocument(Model(Key()));
        await using var preparer = new SavedMacroPlanPreparer(source);
        var host = new HostInputOperationArbiter();
        var operation = new CompanionMacroOperations(host, preparer, f.Runner);
        var result = await operation.RunAsync(source.Reference, [f.A, f.B, third], host.StopGeneration).WaitAsync(HostDeadline);
        Assert.Equal(CompanionMacroOperationStatus.Stopped, result.Status);
        Assert.Equal(new[] { 1, 2 }, checkedRecipients);
        Assert.All(result.Recipients.Skip(1), row => Assert.Equal(TargetedMacroStartStatus.Rejected, row.StartStatus));
        Assert.All(f.Transport.Commands, command => Assert.Equal(f.A, command.Target));
        Assert.False(host.Snapshot.IsBusy);
        Assert.Equal(TargetedMacroResultStatus.Completed, (await Start(f, Plan(Key()), third).Completion.WaitAsync(HostDeadline)).Status);
    }

    [Fact]
    public async Task RunnerShutdownJoinsAStillOwnedPreparation()
    {
        await using var f = new Fixture();
        var entered = Gate(); var unblock = Gate();
        var source = new HostDocument(Model(Key())) { BeforeRead = async () => { entered.TrySetResult(); await unblock.Task; } };
        await using var preparer = new SavedMacroPlanPreparer(source);
        var host = new HostInputOperationArbiter();
        var operation = new CompanionMacroOperations(host, preparer, f.Runner);
        var run = operation.RunAsync(source.Reference, [f.A], host.StopGeneration);
        Task? shutdown = null;
        try
        {
            await entered.Task.WaitAsync(HostDeadline);
            shutdown = f.Runner.DisposeAsync().AsTask();
            await Until(() => source.ReadToken.IsCancellationRequested);
            Assert.False(shutdown.IsCompleted);
            Assert.True(host.Snapshot.IsBusy);
        }
        finally { unblock.TrySetResult(); }
        await shutdown!.WaitAsync(HostDeadline);
        Assert.Equal(CompanionMacroOperationStatus.Stopped, (await run.WaitAsync(HostDeadline)).Status);
        Assert.False(host.Snapshot.IsBusy);
        Assert.Empty(f.Transport.Commands);
    }

    [Fact]
    public async Task CompanionOneSavedReadStartsIndependentInterpretersForBothCapturedRecipients()
    {
        await using var f = new Fixture();
        var source = new HostDocument(Model(Key(), Key(66)));
        await using var preparer = new SavedMacroPlanPreparer(source);
        var host = new HostInputOperationArbiter();
        var operation = new CompanionMacroOperations(host, preparer, f.Runner);
        TargetedInputTarget[] targets = [f.A, f.B];
        source.BeforeRead = () => { targets[1] = Target(99); return Task.CompletedTask; };
        var result = await operation.RunAsync(source.Reference, targets, host.StopGeneration).WaitAsync(HostDeadline);
        Assert.Equal(CompanionMacroOperationStatus.Finished, result.Status);
        Assert.Equal(1, source.Reads);
        Assert.Equal(new[] { f.A, f.B }, result.Recipients.Select(recipient => recipient.Target));
        var runs = result.Recipients.Select(recipient => Assert.IsType<TargetedMacroResult>(recipient.Run)).ToArray();
        Assert.NotEqual(runs[0].Snapshot.RunId, runs[1].Snapshot.RunId);
        Assert.All(runs, completed =>
        {
            Assert.Equal(TargetedMacroResultStatus.Completed, completed.Status);
            var commands = f.Transport.Commands.Where(command => command.RunId == completed.Snapshot.RunId).ToArray();
            Assert.Equal(new[] { 65, 65, 66, 66 }, commands.Select(command => command.Signal.Key));
            Assert.All(commands, command => Assert.Equal(completed.Snapshot.Target, command.Target));
        });
        Assert.False(host.Snapshot.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostStopClosesFinalCommitAndWaitsForAnOwnedRelease(bool callerStop)
    {
        await using var f = new Fixture();
        var source = new HostDocument(Model(Key()));
        await using var preparer = new SavedMacroPlanPreparer(source);
        var host = new HostInputOperationArbiter();
        var operation = new CompanionMacroOperations(host, preparer, f.Runner);
        using var cancel = new CancellationTokenSource();
        var ordinary = Gate(); var admitOrdinary = Gate(); var cleanup = Gate(); var admitCleanup = Gate();
        f.Transport.Before = async command =>
        {
            if (command.Signal.Kind != TargetedInputKind.KeyUp) return;
            if (command.Cleanup) { cleanup.TrySetResult(); await admitCleanup.Task; }
            else { ordinary.TrySetResult(); await admitOrdinary.Task; }
        };
        var run = operation.RunAsync(source.Reference, [f.A], host.StopGeneration, cancel.Token);
        try
        {
            await ordinary.Task.WaitAsync(HostDeadline);
            Assert.Equal(TargetedInputKind.KeyDown, Assert.Single(f.Transport.Commands).Signal.Kind);
            if (callerStop) cancel.Cancel(); else host.SignalStop();
            Assert.False(run.IsCompleted);
            admitOrdinary.TrySetResult();
            await cleanup.Task.WaitAsync(HostDeadline);
            Assert.False(run.IsCompleted);
            Assert.False(host.TryAcquire(HostInputOperationKind.AutoClicker, out _, out _));
        }
        finally { admitOrdinary.TrySetResult(); admitCleanup.TrySetResult(); }
        var result = await run.WaitAsync(HostDeadline);
        Assert.Equal(CompanionMacroOperationStatus.Stopped, result.Status);
        Assert.Equal(TargetedMacroResultStatus.Stopped, Assert.Single(result.Recipients).Run!.Status);
        Assert.Collection(f.Transport.Commands,
            pressed => Assert.False(pressed.Cleanup),
            released => { Assert.True(released.Cleanup); Assert.Equal(TargetedInputKind.KeyUp, released.Signal.Kind); });
        Assert.False(host.Snapshot.IsBusy);
    }

    [Fact]
    public async Task CompanionFaultRetainsHostUntilAnExplicitSameOwnerReleaseRetry()
    {
        await using var f = new Fixture();
        var source = new HostDocument(Model(Key(65, "Down")));
        await using var preparer = new SavedMacroPlanPreparer(source);
        var host = new HostInputOperationArbiter();
        var operation = new CompanionMacroOperations(host, preparer, f.Runner);
        f.Transport.RejectCleanup = true;
        try
        {
            var result = await operation.RunAsync(source.Reference, [f.A], host.StopGeneration).WaitAsync(HostDeadline);
            Assert.Equal(CompanionMacroOperationStatus.CleanupFailed, result.Status);
            Assert.Equal(TargetedMacroResultStatus.CleanupFailed, Assert.Single(result.Recipients).Run!.Status);
            Assert.True(host.Snapshot.IsQuarantined);
            var operationId = Assert.IsType<Guid>(result.OperationId);
            Assert.Equal(operationId, host.Snapshot.OperationId);
            Assert.Single(f.Transport.Commands);
            Assert.False(await operation.RecoverAsync(Guid.NewGuid()));
            await Assert.ThrowsAsync<HostInputOperationCleanupException>(() => operation.RecoverAsync(operationId));
            Assert.True(host.Snapshot.IsQuarantined);
            f.Transport.RejectCleanup = false;
            Assert.True(await operation.RecoverAsync(operationId).WaitAsync(HostDeadline));
            Assert.False(host.Snapshot.IsBusy);
            Assert.True(f.Transport.Commands.Last().Cleanup);
        }
        finally
        {
            f.Transport.RejectCleanup = false;
            if (host.Snapshot is { IsQuarantined: true, OperationId: { } id })
                await operation.RecoverAsync(id).WaitAsync(HostDeadline);
        }
    }

    [Fact]
    public async Task CompanionFaultWhoseHoldsWereReleasedDoesNotQuarantineTheHost()
    {
        await using var f = new Fixture();
        var source = new HostDocument(Model(Key(65, "Down"), new DelayStep(TimeSpan.FromMilliseconds(20)), Key(65, "Up")));
        await using var preparer = new SavedMacroPlanPreparer(source);
        var host = new HostInputOperationArbiter();
        var operation = new CompanionMacroOperations(host, preparer, f.Runner);
        // The target window is moved or resized after the press; the release still reaches it.
        f.Transport.Validation = GeometryChangesAfterFirstInput();
        try
        {
            var result = await operation.RunAsync(source.Reference, [f.A], host.StopGeneration).WaitAsync(HostDeadline);
            Assert.Equal(CompanionMacroOperationStatus.Finished, result.Status);
            var run = Assert.Single(result.Recipients).Run!;
            Assert.Equal(TargetedMacroResultStatus.Faulted, run.Status);
            Assert.Equal(TargetedMacroProblem.InputRejected, run.Problem);
            Assert.Equal(TargetedInputRunState.Stopped, run.Snapshot.Input.State);
            Assert.Collection(f.Transport.Commands,
                pressed => { Assert.False(pressed.Cleanup); Assert.Equal(TargetedInputKind.KeyDown, pressed.Signal.Kind); Assert.Equal(65, pressed.Signal.Key); },
                released => { Assert.True(released.Cleanup); Assert.Equal(TargetedInputKind.KeyUp, released.Signal.Kind); Assert.Equal(65, released.Signal.Key); });
            Assert.False(host.Snapshot.IsQuarantined);
            Assert.False(host.Snapshot.IsBusy);

            f.Transport.Validation = null;
            var lease = f.Input.TryOpen(f.A, TargetedInputPurpose.OwnedWindowDiagnostic);
            Assert.NotNull(lease.Run);
            await lease.Run!.StopAsync();
            var next = await operation.RunAsync(source.Reference, [f.A], host.StopGeneration).WaitAsync(HostDeadline);
            Assert.Equal(CompanionMacroOperationStatus.Finished, next.Status);
            Assert.Equal(TargetedMacroResultStatus.Completed, Assert.Single(next.Recipients).Run!.Status);
        }
        finally
        {
            f.Transport.Validation = null;
            if (host.Snapshot is { IsQuarantined: true, OperationId: { } id })
                await operation.RecoverAsync(id).WaitAsync(HostDeadline);
        }
    }

    [Fact]
    public async Task TrackedStopDuringPreparationKeepsTheSameLeaseUntilTheReadEnds()
    {
        await using var f = new Fixture();
        var entered = Gate();
        var unblock = Gate();
        var source = new HostDocument(Model(Key()))
        {
            BeforeRead = () =>
            {
                entered.TrySetResult();
                unblock.Task.WaitAsync(HostDeadline).GetAwaiter().GetResult();
                return Task.CompletedTask;
            }
        };
        await using var preparer = new SavedMacroPlanPreparer(source);
        var host = new HostInputOperationArbiter();
        var operations = new CompanionMacroOperations(host, preparer, f.Runner);
        TargetedInputTarget[] recipients = [f.A, f.B];
        var start = operations.Start(source.Reference, recipients, host.StopGeneration);
        var handle = Assert.IsType<CompanionMacroOperation>(start.Operation);
        try
        {
            Assert.Null(start.Rejection);
            await entered.Task.WaitAsync(HostDeadline);
            recipients[1] = Target(99);
            var preparing = handle.GetSnapshot();
            Assert.Equal(host.Snapshot.OperationId, preparing.OperationId);
            Assert.Equal(CompanionMacroOperationPhase.Preparing, preparing.Phase);
            Assert.Equal(new[] { f.A, f.B }, preparing.Recipients.Select(row => row.Target));
            Assert.All(preparing.Recipients, row => Assert.Null(row.StartStatus));
            Assert.Null(await handle.StopRecipientAsync(Guid.NewGuid()));
            handle.SignalStop();
            handle.SignalStop();
            Assert.Equal(CompanionMacroOperationPhase.Stopping, handle.GetSnapshot().Phase);
            Assert.False(handle.Completion.IsCompleted);
            Assert.False(host.TryAcquire(HostInputOperationKind.Picker, out _, out _));
        }
        finally { unblock.TrySetResult(); }

        var result = await handle.Completion.WaitAsync(HostDeadline);
        var finished = handle.GetSnapshot();
        Assert.Equal(handle.Id, result.OperationId);
        Assert.Equal(CompanionMacroOperationStatus.Stopped, finished.Result);
        Assert.Equal(CompanionMacroOperationPhase.Finished, finished.Phase);
        Assert.Equal(SavedMacroPreparationStatus.Cancelled, finished.Preparation);
        Assert.All(finished.Recipients, row => Assert.Null(row.Run));
        Assert.False(host.Snapshot.IsBusy);
        Assert.Empty(f.Transport.Commands);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TrackedRunIsVisibleBeforeItsFirstInputAndBeforeTheNextPreflightFinishes(bool allowSecond)
    {
        var unblock = Gate();
        var second = Gate();
        var first = new TaskCompletionSource<TargetedInputCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = Gate();
        await using var f = new Fixture(eligible: (target, _) =>
        {
            if (target.ProcessId == 2)
            {
                second.TrySetResult();
                unblock.Task.WaitAsync(HostDeadline).GetAwaiter().GetResult();
                return allowSecond;
            }
            return true;
        });
        f.Transport.Before = async command =>
        {
            if (command.Target == f.A && command.Signal.Kind == TargetedInputKind.KeyDown)
            {
                first.TrySetResult(command);
                await commit.Task;
            }
        };
        var source = new HostDocument(Model(Key()));
        await using var preparer = new SavedMacroPlanPreparer(source);
        var host = new HostInputOperationArbiter();
        var operations = new CompanionMacroOperations(host, preparer, f.Runner);
        var handle = Assert.IsType<CompanionMacroOperation>(operations.Start(source.Reference, [f.A, f.B], host.StopGeneration).Operation);
        try
        {
            var input = await first.Task.WaitAsync(HostDeadline);
            await second.Task.WaitAsync(HostDeadline);
            var snapshot = handle.GetSnapshot();
            Assert.Equal(CompanionMacroOperationPhase.Executing, snapshot.Phase);
            Assert.Equal(SavedMacroPreparationStatus.Ready, snapshot.Preparation);
            Assert.Equal(TargetedMacroStartStatus.Started, snapshot.Recipients[0].StartStatus);
            Assert.Equal(input.RunId, snapshot.Recipients[0].Run?.RunId);
            Assert.Null(snapshot.Recipients[1].StartStatus);
            Assert.Empty(f.Transport.Commands);
        }
        finally
        {
            commit.TrySetResult();
            unblock.TrySetResult();
        }

        var result = await handle.Completion.WaitAsync(HostDeadline);
        Assert.Equal(CompanionMacroOperationStatus.Finished, result.Status);
        var finished = handle.GetSnapshot().Recipients;
        Assert.Equal(TargetedMacroResultStatus.Completed, finished[0].Result);
        Assert.Equal(allowSecond ? TargetedMacroStartStatus.Started : TargetedMacroStartStatus.Rejected, finished[1].StartStatus);
        Assert.Equal(allowSecond ? TargetedMacroResultStatus.Completed : (TargetedMacroResultStatus?)null, finished[1].Result);
    }

    [Fact]
    public async Task TrackedRecipientControlsKeepPeersRunningAndReleaseOnlyTheSelectedHolds()
    {
        await using var f = new Fixture();
        var source = new HostDocument(Model(Key(65, "Down", 8_000), Key(65, "Up")));
        await using var preparer = new SavedMacroPlanPreparer(source);
        var host = new HostInputOperationArbiter();
        var operations = new CompanionMacroOperations(host, preparer, f.Runner);
        var handle = Assert.IsType<CompanionMacroOperation>(operations.Start(source.Reference, [f.A, f.B], host.StopGeneration).Operation);
        try
        {
            await Until(() => f.Transport.Commands.Count == 2);
            var recipients = handle.GetSnapshot().Recipients;
            var runA = Assert.IsType<TargetedMacroRunSnapshot>(recipients[0].Run).RunId;
            var runB = Assert.IsType<TargetedMacroRunSnapshot>(recipients[1].Run).RunId;
            Assert.NotEqual(runA, runB);
            Assert.Null(await handle.PauseRecipientAsync(Guid.NewGuid()));
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handle.PauseRecipientAsync(runB, cancel.Token));
            Assert.Equal(TargetedMacroState.Paused, (await handle.PauseRecipientAsync(runA).WaitAsync(HostDeadline))?.State);
            Assert.Equal(TargetedMacroState.Running, handle.GetSnapshot().Recipients[1].Run?.State);
            Assert.Equal(1, f.Transport.Count(runA, TargetedInputKind.KeyUp));
            Assert.Equal(TargetedMacroState.Running, (await handle.ResumeRecipientAsync(runA).WaitAsync(HostDeadline))?.State);
            Assert.Equal(2, f.Transport.Count(runA, TargetedInputKind.KeyDown));
            Assert.Equal(TargetedMacroResultStatus.Stopped, (await handle.StopRecipientAsync(runA).WaitAsync(HostDeadline))?.Status);
            Assert.Equal(2, f.Transport.Count(runA, TargetedInputKind.KeyUp));
            Assert.Equal(0, f.Transport.Count(runB, TargetedInputKind.KeyUp));
            Assert.Null(await handle.ResumeRecipientAsync(runA));
            Assert.False(handle.Completion.IsCompleted);
        }
        finally
        {
            handle.SignalStop();
            await handle.Completion.WaitAsync(HostDeadline);
        }
        Assert.All(handle.GetSnapshot().Recipients, row => Assert.Equal(TargetedMacroResultStatus.Stopped, row.Result));
        Assert.False(host.Snapshot.IsBusy);
    }

    [Fact]
    public async Task RetiredHandleCannotStopOrPauseAReplacementForTheSameWindow()
    {
        await using var f = new Fixture();
        var source = new HostDocument(Model(Key()));
        await using var preparer = new SavedMacroPlanPreparer(source);
        var host = new HostInputOperationArbiter();
        var operations = new CompanionMacroOperations(host, preparer, f.Runner);
        var old = Assert.IsType<CompanionMacroOperation>(operations.Start(source.Reference, [f.A], host.StopGeneration).Operation);
        var prior = await old.Completion.WaitAsync(HostDeadline);
        var oldRun = Assert.Single(prior.Recipients).Run!.Snapshot.RunId;

        var entered = Gate();
        var unblock = Gate();
        source.BeforeRead = async () => { entered.TrySetResult(); await unblock.Task; };
        var current = Assert.IsType<CompanionMacroOperation>(operations.Start(source.Reference, [f.A], host.StopGeneration).Operation);
        try
        {
            await entered.Task.WaitAsync(HostDeadline);
            old.SignalStop();
            Assert.Null(await old.PauseRecipientAsync(oldRun));
            Assert.Null(await current.StopRecipientAsync(oldRun));
            Assert.NotEqual(old.Id, current.Id);
            Assert.Equal(CompanionMacroOperationPhase.Preparing, current.GetSnapshot().Phase);
            Assert.False(source.ReadToken.IsCancellationRequested);
        }
        finally { unblock.TrySetResult(); }

        Assert.Equal(CompanionMacroOperationStatus.Finished, (await current.Completion.WaitAsync(HostDeadline)).Status);
        Assert.NotEqual(oldRun, Assert.Single(current.GetSnapshot().Recipients).Run?.RunId);
    }

    [Fact]
    public async Task TrackedGroupStopFencesAnInFlightRecipientResumeBeforeInputCommit()
    {
        await using var f = new Fixture();
        var source = new HostDocument(Model(Key(65, "Down", 8_000), Key(65, "Up")));
        await using var preparer = new SavedMacroPlanPreparer(source);
        var host = new HostInputOperationArbiter();
        var operations = new CompanionMacroOperations(host, preparer, f.Runner);
        var handle = Assert.IsType<CompanionMacroOperation>(operations.Start(source.Reference, [f.A], host.StopGeneration).Operation);
        var restoring = Gate();
        var unblock = Gate();
        Task<TargetedMacroRunSnapshot?>? resume = null;
        try
        {
            await Until(() => f.Transport.Commands.Count == 1);
            var run = Assert.IsType<TargetedMacroRunSnapshot>(Assert.Single(handle.GetSnapshot().Recipients).Run);
            Assert.Equal(TargetedMacroState.Paused, (await handle.PauseRecipientAsync(run.RunId).WaitAsync(HostDeadline))?.State);
            f.Transport.Before = async command =>
            {
                if (command.Signal.Kind != TargetedInputKind.KeyDown) return;
                restoring.TrySetResult();
                await unblock.Task;
            };
            resume = handle.ResumeRecipientAsync(run.RunId);
            await restoring.Task.WaitAsync(HostDeadline);
            handle.SignalStop();
            Assert.Equal(CompanionMacroOperationPhase.Stopping, handle.GetSnapshot().Phase);
            Assert.Null(await handle.ResumeRecipientAsync(run.RunId));
            Assert.False(handle.Completion.IsCompleted);
            Assert.False(host.TryAcquire(HostInputOperationKind.Recording, out _, out _));
        }
        finally
        {
            handle.SignalStop();
            unblock.TrySetResult();
            await handle.Completion.WaitAsync(HostDeadline);
        }
        Assert.NotEqual(TargetedMacroState.Running, (await resume!.WaitAsync(HostDeadline))?.State);
        Assert.Equal(CompanionMacroOperationStatus.Stopped, handle.GetSnapshot().Result);
        Assert.Equal(new[] { TargetedInputKind.KeyDown, TargetedInputKind.KeyUp }, f.Transport.Commands.Select(c => c.Signal.Kind));
        Assert.False(host.Snapshot.IsBusy);
    }

    private sealed class HostDocument(MacroModel model) : ISavedMacroDocumentSource
    {
        public SavedMacroReference Reference { get; } = new(Guid.NewGuid(), model.Id, Guid.NewGuid());
        public int Reads;
        public CancellationToken ReadToken;
        public Func<Task>? BeforeRead;

        public async Task<SavedMacroDocumentReadResult> ReadAsync(SavedMacroReference reference, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Reads);
            ReadToken = ct;
            if (BeforeRead is { } before) await before();
            var receipt = new SavedMacroDocument(model, new(model.Id, new string('B', 64), 128));
            return new(SavedMacroDocumentReadStatus.Ready, SavedMacroDocumentPin.Create(reference, receipt, () => true));
        }
    }
}
