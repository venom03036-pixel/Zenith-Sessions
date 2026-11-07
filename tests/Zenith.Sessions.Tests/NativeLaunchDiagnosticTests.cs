using System.IO;
using System.Text.Json;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class NativeLaunchDiagnosticContractsTests
{
    private static NativeLaunchDiagnosticSnapshot Snapshot() => new(Guid.NewGuid(), 1, DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow, NativeLaunchDiagnosticStage.VerifyingExecutable, NativeLaunchCleanupStatus.Pending);

    [Fact]
    public void FixedSnapshotHasNoRawMetadataFieldsAndLegacyPreparationArgumentsStillWork()
    {
        var value = Snapshot().Validated();
        Assert.All(typeof(NativeLaunchDiagnosticSnapshot).GetProperties(), property =>
        {
            Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            Assert.True(type.IsEnum || type == typeof(Guid) || type == typeof(long) || type == typeof(bool) || type == typeof(DateTimeOffset));
        });
        Assert.Null(new NativeHandoffPreparationResult(NativeHandoffPreparationStatus.Failed).BrowserFacts);
        Assert.Null(value.TrustStatus); Assert.Null(value.PreparationStatus); Assert.Null(value.BrowserOutcome); Assert.Null(value.DispatchStatus);
        var browser = new NativeBrowserPreparationFacts(RobloxHandoffProbeOutcome.MalformedHandoff, NativeHandoffCaptureStatus.InvalidShape,
            value.StartedUtc).Validated();
        string json = JsonSerializer.Serialize(new NativeHandoffPreparationResult(NativeHandoffPreparationStatus.CaptureRejected, BrowserFacts: browser));
        Assert.DoesNotContain("Candidate", json); Assert.DoesNotContain("Credential", json); Assert.DoesNotContain("gameinfo", json);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("revision")]
    [InlineData("time")]
    [InlineData("stage")]
    [InlineData("cleanup")]
    [InlineData("trust-pair")]
    [InlineData("verified-pair")]
    [InlineData("browser")]
    [InlineData("shape")]
    [InlineData("dispatch")]
    [InlineData("stopped")]
    public void InvalidFixedFactsCannotBePresentedAsAValidatedSnapshot(string kind)
    {
        var value = Snapshot(); value = kind switch
        {
            "id" => value with { OperationId = Guid.Empty }, "revision" => value with { Revision = 0 },
            "time" => value with { ExecutableObservedUtc = default(DateTimeOffset) },
            "stage" => value with { Stage = (NativeLaunchDiagnosticStage)100 }, "cleanup" => value with { CleanupStatus = (NativeLaunchCleanupStatus)100 },
            "trust-pair" => value with { TrustStatus = ClientExecutableTrustStatus.Unknown },
            "verified-pair" => value with { TrustStatus = ClientExecutableTrustStatus.VerifiedRobloxPublisher, TrustReason = ClientExecutableTrustReason.RevocationUnavailable },
            "browser" => value with { BrowserOutcome = (RobloxHandoffProbeOutcome)100 }, "shape" => value with { ShapeStatus = (NativeHandoffCaptureStatus)100 },
            "dispatch" => value with { DispatchStatus = (NativeHandoffAttemptStatus)100 }, _ => value with { Stopped = true }
        };
        Assert.Throws<ArgumentException>(() => value.Validated());
    }

    [Fact]
    public void ActualClockRollbackDoesNotInventNewFactTimesOrReplaceRevisionOrdering()
    {
        var value = Snapshot();
        var rollback = value with { UpdatedUtc = value.StartedUtc.AddHours(-1), Revision = value.Revision + 1 };
        Assert.Equal(rollback, rollback.Validated());
    }
}

public sealed partial class NativeAccountLaunchProviderTests
{
    [Fact]
    public async Task DiagnosticTrustRefusalLeavesBrowserAndDispatchExplicitlyUnrecorded()
    {
        await using var f = new Fixture(_root, NativeClientTrustOutcome.Unsigned);
        var result = await f.Provider.PrepareAsync(f.Context, default);
        Assert.Equal(LaunchPreparationFailure.Rejected, result.Failure);
        var diagnostic = Assert.IsType<NativeLaunchDiagnosticSnapshot>(f.Provider.GetDiagnostic(f.Intent.OperationId));
        Assert.Equal(ClientExecutableTrustReason.Unsigned, diagnostic.TrustReason);
        Assert.NotNull(diagnostic.ExecutableObservedUtc); Assert.Null(diagnostic.PreparationStatus); Assert.Null(diagnostic.BrowserOutcome);
        Assert.Null(diagnostic.DispatchStatus); Assert.Equal(NativeLaunchCleanupStatus.Complete, diagnostic.CleanupStatus);
        Assert.False(diagnostic.Stopped); Assert.Equal(0, f.Preparer.Calls); Assert.Equal(0, f.Starter.Calls);
    }

    [Fact]
    public async Task DiagnosticFactsSurviveActualSyntheticDispatchAndHarmlessCompletedDisposal()
    {
        await using var f = new Fixture(_root);
        var browserAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        f.Preparer.BrowserFacts = new(RobloxHandoffProbeOutcome.CapturedAndPrevented, NativeHandoffCaptureStatus.AcceptedSourceGrammar, browserAt);
        var result = await f.Provider.PrepareAsync(f.Context, default); await using var handle = result.Prepared!;
        var ready = Assert.IsType<NativeLaunchDiagnosticSnapshot>(f.Provider.GetDiagnostic(f.Intent.OperationId));
        Assert.Equal(NativeLaunchDiagnosticStage.Prepared, ready.Stage); Assert.Equal(NativeLaunchCleanupStatus.Pending, ready.CleanupStatus);
        Assert.Equal(ClientExecutableTrustStatus.VerifiedRobloxPublisher, ready.TrustStatus);
        Assert.Equal(NativeHandoffPreparationStatus.Ready, ready.PreparationStatus); Assert.Equal(browserAt, ready.BrowserObservedUtc);
        Assert.Null(f.Provider.GetDiagnostic(f.Capture.Candidate!.Context.OperationId));
        using var boundary = Boundary(handle.DispatchIntent); await handle.DispatchAsync(boundary, default);
        var complete = Assert.IsType<NativeLaunchDiagnosticSnapshot>(f.Provider.GetDiagnostic(f.Intent.OperationId));
        Assert.Equal(NativeHandoffAttemptStatus.StartedUnverified, complete.DispatchStatus); Assert.NotNull(complete.DispatchObservedUtc);
        Assert.Equal(NativeLaunchCleanupStatus.Complete, complete.CleanupStatus); Assert.NotNull(complete.CleanupObservedUtc); Assert.False(complete.Stopped);
        Assert.Equal(ready.ExecutableObservedUtc, complete.ExecutableObservedUtc); Assert.Equal(browserAt, complete.BrowserObservedUtc);
        await handle.DisposeAsync(); await f.Provider.DisposeAsync();
        Assert.Equal(complete, f.Provider.GetDiagnostic(f.Intent.OperationId));
        Assert.NotSame(complete, f.Provider.GetDiagnostic(f.Intent.OperationId));
        string text = JsonSerializer.Serialize(complete) + complete;
        foreach (string secret in new[] { SyntheticHandoff.Ticket, "synthetic-native-current", f.Executable, f.Intent.ProfileId.ToString() })
            Assert.DoesNotContain(secret, text);
        Assert.Equal(CapabilityState.Unverified, f.Provider.AccountLaunch.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticDispatchRefusalRecordsTheActualFinalBoundaryWithoutStarting(bool capacity)
    {
        await using var f = new Fixture(_root);
        var ready = await f.Provider.PrepareAsync(f.Context, default); await using var handle = ready.Prepared!;
        using var boundary = new LaunchDispatchBoundary(handle.DispatchIntent, (_, _) => Task.FromResult(capacity), () => true,
            () => new(false, "Synthetic refusal"));
        Assert.Equal(ProviderLaunchStatus.RejectedBeforeStart, (await handle.DispatchAsync(boundary, default)).Status);
        var diagnostic = f.Provider.GetDiagnostic(f.Intent.OperationId)!;
        Assert.Equal(capacity ? NativeHandoffAttemptStatus.AdmissionDenied : NativeHandoffAttemptStatus.RequestChanged, diagnostic.DispatchStatus);
        Assert.Equal(0, f.Starter.Calls); Assert.False(diagnostic.Stopped); Assert.Equal(NativeLaunchCleanupStatus.Complete, diagnostic.CleanupStatus);
    }

    [Fact]
    public async Task DiagnosticPreparerThrowIsKnownFailedRatherThanAnUnreachedNullStage()
    {
        await using var f = new Fixture(_root); f.Preparer.Throw = true;
        Assert.Equal(LaunchPreparationFailure.Rejected, (await f.Provider.PrepareAsync(f.Context, default)).Failure);
        var diagnostic = f.Provider.GetDiagnostic(f.Intent.OperationId)!;
        Assert.Equal(NativeHandoffPreparationStatus.Failed, diagnostic.PreparationStatus); Assert.NotNull(diagnostic.PreparationObservedUtc);
        Assert.Null(diagnostic.BrowserOutcome); Assert.Null(diagnostic.DispatchStatus); Assert.Equal(0, f.Starter.Calls);
        Assert.DoesNotContain(SyntheticHandoff.Ticket, JsonSerializer.Serialize(diagnostic) + diagnostic);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("consumed")]
    [InlineData("closed")]
    public async Task ReadyPreparerCannotHideTheCompositeCandidatesLifetimeRefusal(string lifetime)
    {
        await using var f = new Fixture(_root);
        f.Preparer.Transform = native =>
        {
            if (lifetime == "expired") f.Capture.Clock.Advance(TimeSpan.FromSeconds(10));
            else if (lifetime == "consumed") native.Candidate.Dispose();
            else native.Candidate.Context.Dispose();
            return native;
        };
        Assert.Equal(LaunchPreparationFailure.Expired, (await f.Provider.PrepareAsync(f.Context, default)).Failure);
        var diagnostic = f.Provider.GetDiagnostic(f.Intent.OperationId)!;
        Assert.Equal(NativeHandoffPreparationStatus.Expired, diagnostic.PreparationStatus);
        Assert.Null(diagnostic.DispatchStatus); Assert.Equal(0, f.Starter.Calls); Assert.Equal(NativeLaunchCleanupStatus.Complete, diagnostic.CleanupStatus);
    }

    [Fact]
    public async Task DiagnosticCancellationIsImmediateWhileTheActualLateBrowserStillRemainsOwned()
    {
        await using var f = new Fixture(_root); using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Capture.BeforeReturn = async _ => { entered.TrySetResult(); await release.Task; };
        f.Preparer.BrowserFacts = new(RobloxHandoffProbeOutcome.CapturedAndPrevented, NativeHandoffCaptureStatus.AcceptedSourceGrammar);
        var work = f.Provider.PrepareAsync(f.Context, stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
            var pending = f.Provider.GetDiagnostic(f.Intent.OperationId)!;
            Assert.True(pending.Stopped); Assert.Equal(NativeLaunchDiagnosticStage.Stopped, pending.Stage);
            Assert.Null(pending.PreparationStatus); Assert.Equal(NativeLaunchCleanupStatus.Pending, pending.CleanupStatus); Assert.False(work.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        var finished = f.Provider.GetDiagnostic(f.Intent.OperationId)!;
        Assert.True(finished.Stopped); Assert.Equal(NativeLaunchDiagnosticStage.Stopped, finished.Stage);
        Assert.Equal(NativeHandoffPreparationStatus.Ready, finished.PreparationStatus); // Late observation cannot resume the stopped owner.
        Assert.Equal(NativeLaunchCleanupStatus.Complete, finished.CleanupStatus); Assert.Equal(0, f.Starter.Calls);
    }

    [Fact]
    public async Task DiagnosticCancelledCleanupFailureRetainsItsPriorityAndCannotBeReadAsSuccess()
    {
        await using var f = new Fixture(_root); using var stop = new CancellationTokenSource();
        f.Preparer.Status = NativeHandoffPreparationStatus.CleanupFailed; f.Preparer.Transform = native => { stop.Cancel(); return native; };
        f.Preparer.BrowserFacts = new(RobloxHandoffProbeOutcome.CleanupFailed);
        Assert.Equal(LaunchPreparationFailure.CleanupFailed, (await f.Provider.PrepareAsync(f.Context, stop.Token)).Failure);
        var diagnostic = f.Provider.GetDiagnostic(f.Intent.OperationId)!;
        Assert.True(diagnostic.Stopped); Assert.Equal(NativeHandoffPreparationStatus.CleanupFailed, diagnostic.PreparationStatus);
        Assert.Equal(RobloxHandoffProbeOutcome.CleanupFailed, diagnostic.BrowserOutcome); Assert.Equal(NativeLaunchCleanupStatus.Failed, diagnostic.CleanupStatus);
        Assert.Null(diagnostic.DispatchStatus); Assert.True(f.Provider.CleanupBlocked); Assert.Equal(0, f.Starter.Calls);
    }
}

public sealed partial class NativeHandoffPreparationTests
{
    [Theory]
    [InlineData(RobloxHandoffProbeOutcome.TimedOut, NativeHandoffPreparationStatus.CaptureRejected)]
    [InlineData(RobloxHandoffProbeOutcome.MalformedHandoff, NativeHandoffPreparationStatus.CaptureRejected)]
    [InlineData(RobloxHandoffProbeOutcome.CleanupFailed, NativeHandoffPreparationStatus.CleanupFailed)]
    [InlineData(RobloxHandoffProbeOutcome.Cancelled, NativeHandoffPreparationStatus.Cancelled)]
    public async Task BrowserReturnFactsRemainAvailableAfterCaptureDisposalAndFailureCleanup(RobloxHandoffProbeOutcome browserOutcome,
        NativeHandoffPreparationStatus expected)
    {
        await using var f = await Fixture.Open(_root); using var browser = new Browser();
        browser.Run = (_, _, _) => Task.FromResult(new NativeBrowserCapture(browserOutcome, shapeStatus: NativeHandoffCaptureStatus.InvalidShape));
        var result = await f.Service.PrepareNativeHandoffAsync(f.Intent, browser, default);
        Assert.Equal(expected, result.Status); Assert.Null(result.Preparation);
        Assert.Equal(browserOutcome, result.BrowserFacts?.Outcome); Assert.Equal(NativeHandoffCaptureStatus.InvalidShape, result.BrowserFacts?.ShapeStatus);
        Assert.NotNull(result.BrowserFacts?.ObservedUtc); Assert.Null(browser.Returned!.Candidate); Assert.Null(browser.Returned.CurrentCredential);
        string text = JsonSerializer.Serialize(result) + result;
        foreach (string secret in new[] { Original, Current, SyntheticHandoff.Ticket }) Assert.DoesNotContain(secret, text);
    }
}
