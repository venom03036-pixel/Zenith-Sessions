using System.IO;
using System.Text.Json;
using Zenith.Sessions.Core;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed partial class ConfiguredNativeLaunchProviderTests
{
    private static NativeLaunchDiagnosticSnapshot FixedDiagnostic(LaunchPreparationContext context, long revision = 1)
    {
        var originalTime = new DateTimeOffset(2026, 1, 1, 1, 2, 3, TimeSpan.Zero);
        return new(context.OriginalIntent.OperationId, revision, originalTime, originalTime,
            NativeLaunchDiagnosticStage.Prepared, NativeLaunchCleanupStatus.Pending,
            TrustStatus: ClientExecutableTrustStatus.VerifiedRobloxPublisher, TrustReason: ClientExecutableTrustReason.Verified,
            PreparationStatus: NativeHandoffPreparationStatus.Ready, BrowserOutcome: RobloxHandoffProbeOutcome.CapturedAndPrevented,
            ShapeStatus: NativeHandoffCaptureStatus.AcceptedSourceGrammar, ExecutableObservedUtc: originalTime,
            PreparationObservedUtc: originalTime, BrowserObservedUtc: originalTime, LaunchMode: NativeLaunchMode.DirectTicket);
    }
    private sealed class DiagnosticReader(Func<Guid, NativeLaunchDiagnosticSnapshot?> read) : INativeLaunchDiagnosticSource
    { public NativeLaunchDiagnosticSnapshot? GetDiagnostic(Guid operationId) => read(operationId); }

    [Fact]
    public async Task WholeDiagnosticSurvivesChildDisposalWithOriginalFactTimesAndActualWrapperCleanup()
    {
        await using var h = new Harness(); var context = Context(); var facts = FixedDiagnostic(context); int afterDisposeReads = 0;
        var child = new Child { DisposeGate = Gate() };
        child.DiagnosticSource = new DiagnosticReader(_ => { if (child.Disposals != 0) afterDisposeReads++; return facts; });
        child.BeforeReturn = _ => child.Handle!.BeforeDispatchReturn = () => facts = facts with
        { Revision = 2, Stage = NativeLaunchDiagnosticStage.Finished, DispatchStatus = NativeHandoffAttemptStatus.StartedUnverified,
            DispatchObservedUtc = facts.UpdatedUtc.AddSeconds(1), CleanupStatus = NativeLaunchCleanupStatus.Complete };
        h.Next.Enqueue(child); h.Slot.Configure(Candidate());
        var ready = await h.Slot.PrepareAsync(context, default); await using var handle = ready.Prepared!;
        using var boundary = Boundary(handle.DispatchIntent); var dispatch = handle.DispatchAsync(boundary, default);
        try
        {
            await child.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var pending = h.Slot.GetDiagnostic(context.OriginalIntent.OperationId)!;
            Assert.Equal(NativeHandoffAttemptStatus.StartedUnverified, pending.DispatchStatus);
            Assert.Equal(NativeLaunchCleanupStatus.Pending, pending.CleanupStatus); Assert.Null(pending.CleanupObservedUtc); Assert.False(dispatch.IsCompleted);
            Assert.Equal(facts.ExecutableObservedUtc, pending.ExecutableObservedUtc); Assert.Equal(facts.BrowserObservedUtc, pending.BrowserObservedUtc);
            Assert.Equal(NativeLaunchMode.DirectTicket, pending.LaunchMode); // The child's recorded mode is copied like its other fixed facts (R-api-7).
        }
        finally { child.DisposeGate.TrySetResult(); }
        Assert.Equal(ProviderLaunchStatus.ClientStartedUnverified, (await dispatch).Status);
        var complete = h.Slot.GetDiagnostic(context.OriginalIntent.OperationId)!;
        Assert.Equal(NativeLaunchCleanupStatus.Complete, complete.CleanupStatus); Assert.NotNull(complete.CleanupObservedUtc); Assert.False(complete.Stopped);
        await handle.DisposeAsync(); h.Slot.Configure(Candidate("B")); h.Slot.Configure(null); await h.Slot.DisposeAsync();
        Assert.Equal(complete, h.Slot.GetDiagnostic(context.OriginalIntent.OperationId)); Assert.Equal(0, afterDisposeReads);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(complete)); Assert.Equal(CapabilityState.Unverified, h.Slot.AccountLaunch.State);
    }

    [Fact]
    public async Task ConfigurationFenceKeepsLateFactsStoppedAndAReusedOperationStartsWithUntouchedStages()
    {
        await using var h = new Harness(); var context = Context(); var child = new Child { PrepareGate = Gate() };
        var facts = FixedDiagnostic(context); child.DiagnosticSource = new DiagnosticReader(_ => facts);
        h.Next.Enqueue(child); h.Slot.Configure(Candidate()); var work = h.Slot.PrepareAsync(context, default);
        try
        {
            await child.PrepareStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)); h.Slot.Configure(Candidate("B"));
            var stopped = h.Slot.GetDiagnostic(context.OriginalIntent.OperationId)!;
            Assert.True(stopped.Stopped); Assert.Equal(NativeLaunchDiagnosticStage.Stopped, stopped.Stage); Assert.False(work.IsCompleted);
        }
        finally { child.PrepareGate.TrySetResult(); }
        Assert.Equal(LaunchPreparationFailure.Rejected, (await work).Failure);
        var old = h.Slot.GetDiagnostic(context.OriginalIntent.OperationId)!;
        Assert.True(old.Stopped); Assert.Equal(NativeLaunchCleanupStatus.Complete, old.CleanupStatus);

        var newer = new Child { PrepareGate = Gate() }; h.Next.Enqueue(newer); var retry = h.Slot.PrepareAsync(context, default);
        try
        {
            await newer.PrepareStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var fresh = h.Slot.GetDiagnostic(context.OriginalIntent.OperationId)!;
            Assert.True(fresh.Revision > old.Revision); Assert.False(fresh.Stopped); Assert.Null(fresh.TrustStatus); Assert.Null(fresh.BrowserOutcome);
            Assert.Null(fresh.DispatchStatus); Assert.Equal(NativeLaunchCleanupStatus.Pending, fresh.CleanupStatus);
        }
        finally { newer.PrepareGate.TrySetResult(); }
        await (await retry).Prepared!.DisposeAsync();
    }

    [Fact]
    public async Task ABlockedOldReaderDoesNotHoldTheGateOrOverwriteAReusedOperationAttempt()
    {
        await using var h = new Harness(); var context = Context(); var first = new Child(); var facts = FixedDiagnostic(context);
        using var releaseReader = new ManualResetEventSlim(); var entered = Gate(); int armed = 0;
        first.DiagnosticSource = new DiagnosticReader(_ =>
        {
            if (Interlocked.CompareExchange(ref armed, 2, 1) == 1)
            { entered.TrySetResult(); if (!releaseReader.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(Secret); }
            return facts;
        });
        h.Next.Enqueue(first); h.Slot.Configure(Candidate()); var ready = await h.Slot.PrepareAsync(context, default);
        Interlocked.Exchange(ref armed, 1);
        var read = Task.Run(() => h.Slot.GetDiagnostic(context.OriginalIntent.OperationId));
        var second = new Child { PrepareGate = Gate() }; Task<LaunchPreparationResult>? newer = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            h.Slot.CancelActive(); await ready.Prepared!.DisposeAsync(); // Would deadlock if the reader held _gate.
            h.Next.Enqueue(second); newer = h.Slot.PrepareAsync(context, default);
            await second.PrepareStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            releaseReader.Set(); var latest = await read.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(latest); Assert.Null(latest.TrustStatus); Assert.Null(latest.PreparationStatus); Assert.False(latest.Stopped);
        }
        finally
        {
            releaseReader.Set(); await read;
            second.PrepareGate.TrySetResult(); if (newer is not null) await (await newer).Prepared!.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrThrowingDiagnosticReaderCannotChangeRealPreparationOrCleanupHealth(bool throws)
    {
        await using var h = new Harness(); var context = Context(); var child = new Child();
        child.DiagnosticSource = new DiagnosticReader(_ => throws ? throw new IOException(Secret)
            : FixedDiagnostic(context) with { OperationId = Guid.NewGuid() });
        h.Next.Enqueue(child); h.Slot.Configure(Candidate());
        var ready = await h.Slot.PrepareAsync(context, default); await using var handle = ready.Prepared!;
        Assert.Equal(LaunchPreparationFailure.None, ready.Failure); Assert.True(h.Slot.CleanupHealthy);
        var observed = h.Slot.GetDiagnostic(context.OriginalIntent.OperationId)!; Assert.Null(observed.TrustStatus);
        using var boundary = Boundary(handle.DispatchIntent);
        Assert.Equal(ProviderLaunchStatus.ClientStartedUnverified, (await handle.DispatchAsync(boundary, default)).Status);
        Assert.True(h.Slot.CleanupHealthy); Assert.Equal(NativeLaunchCleanupStatus.Complete, h.Slot.GetDiagnostic(context.OriginalIntent.OperationId)!.CleanupStatus);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(observed));
    }

    [Fact]
    public async Task HistoryRetainsAtMost128WholeOperationsEvictsOldestCompletedAndKeepsTheActiveEntry()
    {
        await using var h = new Harness(); h.Slot.Configure(Candidate()); var ids = new List<Guid>();
        for (int index = 0; index < NativeLaunchDiagnosticLimits.MaximumSnapshots + 1; index++)
        {
            var context = Context(); ids.Add(context.OriginalIntent.OperationId);
            var ready = await h.Slot.PrepareAsync(context, default); await ready.Prepared!.DisposeAsync();
        }
        var active = Context(); var accepted = await h.Slot.PrepareAsync(active, default); await using var handle = accepted.Prepared!;
        ids.Add(active.OriginalIntent.OperationId);
        for (int index = 0; index < 4; index++) Assert.Equal(LaunchPreparationFailure.Rejected, (await h.Slot.PrepareAsync(Context(), default)).Failure);
        Assert.Null(h.Slot.GetDiagnostic(ids[0])); Assert.Null(h.Slot.GetDiagnostic(ids[1])); Assert.NotNull(h.Slot.GetDiagnostic(ids[2]));
        Assert.NotNull(h.Slot.GetDiagnostic(active.OriginalIntent.OperationId));
        Assert.Equal(NativeLaunchDiagnosticLimits.MaximumSnapshots, ids.Count(id => h.Slot.GetDiagnostic(id) is not null));
    }

    [Fact]
    public async Task WrapperCleanupFailureIsRecordedOnlyAfterTheActualFailingOwnerFinishes()
    {
        await using var h = new Harness { ExpectPoison = true }; var context = Context(); var child = new Child { DisposeGate = Gate(), ThrowDispose = true };
        child.DiagnosticSource = new DiagnosticReader(_ => FixedDiagnostic(context)); h.Next.Enqueue(child); h.Slot.Configure(Candidate());
        var ready = await h.Slot.PrepareAsync(context, default); var disposing = ready.Prepared!.DisposeAsync().AsTask();
        try
        {
            await child.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(NativeLaunchCleanupStatus.Pending, h.Slot.GetDiagnostic(context.OriginalIntent.OperationId)!.CleanupStatus); Assert.False(disposing.IsCompleted);
        }
        finally { child.DisposeGate.TrySetResult(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => disposing);
        var failed = h.Slot.GetDiagnostic(context.OriginalIntent.OperationId)!;
        Assert.Equal(NativeLaunchCleanupStatus.Failed, failed.CleanupStatus); Assert.NotNull(failed.CleanupObservedUtc); Assert.False(h.Slot.CleanupHealthy);
        h.Slot.Configure(Candidate("B")); Assert.Equal(failed, h.Slot.GetDiagnostic(context.OriginalIntent.OperationId));
    }
}
