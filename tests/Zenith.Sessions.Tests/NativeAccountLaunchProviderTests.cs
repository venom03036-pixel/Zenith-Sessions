using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

/// <summary>Actual composition and owned synthetic file handles; all authentication, browser and process starts are fake.</summary>
public sealed partial class NativeAccountLaunchProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-native-provider-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CapabilityRemainsUnverifiedAndLegacyEntryPointsCannotPrepareOrStart()
    {
        await using var f = new Fixture(_root);
        Assert.Equal(CapabilityState.Unverified, f.Provider.AccountLaunch.State);
        Assert.Equal(ProviderLaunchStatus.RejectedBeforeStart, (await f.Provider.LaunchAsync(f.Intent, default)).Status);
        Assert.False(await f.Provider.ConfirmArrivalAsync(f.Intent, new(ProviderLaunchStatus.VerifiedArrival), default));
        Assert.Equal(0, f.Policy.Calls); Assert.Equal(0, f.Preparer.Calls); Assert.Equal(0, f.Capture.Calls); Assert.Equal(0, f.Starter.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task OwnedCompositionAdoptsOnlyFinalMetadataAndUsesTheExactDispatchBoundary(int revisionDelta)
    {
        await using var f = new Fixture(_root); f.Preparer.RevisionDelta = revisionDelta;
        var result = await f.Provider.PrepareAsync(f.Context, default);
        Assert.Equal(LaunchPreparationFailure.None, result.Failure); Assert.NotNull(result.Prepared);
        await using var handle = result.Prepared!;
        Assert.Same(f.Context, handle.Context); Assert.False(handle.IsExpired);
        Assert.Equal(f.Intent with { ProfileRevision = f.Intent.ProfileRevision + revisionDelta, AuthorizationGeneration = f.Intent.AuthorizationGeneration + 2 }, handle.DispatchIntent);
        f.AssertPinned(); Assert.Equal(0, f.Starter.Calls);
        int validations = 0, admissions = 0;
        using var boundary = new LaunchDispatchBoundary(handle.DispatchIntent,
            (intent, _) => { validations++; Assert.Equal(handle.DispatchIntent, intent); return Task.FromResult(true); }, () => true,
            () => { admissions++; return new(true, "Synthetic fresh reservation"); });
        var launched = await handle.DispatchAsync(boundary, default);
        Assert.Equal(ProviderLaunchStatus.ClientStartedUnverified, launched.Status);
        Assert.Equal(13579, launched.ProcessId); Assert.NotNull(launched.ProcessStartUtcTicks);
        Assert.Null(launched.ObservedUserId); Assert.Null(launched.ObservedPlaceId); Assert.Null(launched.ObservedJobId);
        Assert.Equal(1, validations); Assert.Equal(1, admissions); Assert.Equal(1, f.Vault.Calls); Assert.Equal(1, f.Starter.Calls);
        Assert.Same(f.Preparer.Issued!.FinalGrant, f.Vault.Seen); Assert.True(f.Capture.Candidate!.IsConsumed); Assert.True(handle.IsExpired);
        Assert.Equal(SyntheticHandoff.Uri(f.Capture.Candidate.Context, f.Capture.Clock), f.Starter.ObservedArgument);
        Assert.Empty(f.Starter.LastStart!.ArgumentList); f.AssertReleased();
        Assert.Equal(ProviderLaunchStatus.RejectedBeforeStart, (await handle.DispatchAsync(boundary, default)).Status);
        Assert.Equal(1, f.Starter.Calls);
        string diagnostic = result + handle.ToString() + JsonSerializer.Serialize(result) + JsonSerializer.Serialize(handle) + JsonSerializer.Serialize(launched);
        Assert.DoesNotContain(SyntheticHandoff.Ticket, diagnostic); Assert.DoesNotContain("gameinfo", diagnostic); Assert.DoesNotContain("synthetic-native-current", diagnostic);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("profile")]
    [InlineData("user")]
    [InlineData("place")]
    [InlineData("job")]
    [InlineData("kind")]
    [InlineData("priority")]
    [InlineData("request-time")]
    [InlineData("explicit")]
    [InlineData("revision")]
    [InlineData("generation")]
    [InlineData("owner")]
    [InlineData("grant-profile")]
    [InlineData("grant-generation")]
    [InlineData("issuer")]
    [InlineData("hash")]
    public async Task PreparationCannotReinterpretTheOriginalQueueIntentOrFinalGrant(string mismatch)
    {
        await using var f = new Fixture(_root);
        f.Preparer.Transform = native =>
        {
            var intent = mismatch switch
            {
                "operation" => native.Intent with { OperationId = Guid.NewGuid() },
                "profile" => native.Intent with { ProfileId = Guid.NewGuid() },
                "user" => native.Intent with { UserId = 124 },
                "place" => native.Intent with { PlaceId = 1819 },
                "job" => native.Intent with { RequestedJobId = "bbbbbbbb-bbbb-4ccc-8ddd-eeeeeeeeeeee" },
                "kind" => native.Intent with { RequestedServerKind = RobloxServerKind.Unknown },
                "priority" => native.Intent with { Priority = native.Intent.Priority + 1 },
                "request-time" => native.Intent with { RequestedUtc = native.Intent.RequestedUtc.AddTicks(1) },
                "explicit" => native.Intent with { HasExplicitDestination = false },
                "revision" => native.Intent with { ProfileRevision = native.Intent.ProfileRevision + 2 },
                "generation" => native.Intent with { AuthorizationGeneration = native.Intent.AuthorizationGeneration + 1 },
                _ => native.Intent
            };
            var grant = mismatch switch
            {
                "owner" => native.FinalGrant with { HighestMutationId = Guid.NewGuid() },
                "grant-profile" => native.FinalGrant with { ProfileId = Guid.NewGuid() },
                "grant-generation" => native.FinalGrant with { LastCommitted = native.FinalGrant.LastCommitted - 1 },
                "issuer" => native.FinalGrant with { Record = native.FinalGrant.Record! with { Issuer = "https://invalid.example/" } },
                "hash" => native.FinalGrant with { RecordSha256 = "not-a-snapshot-hash" },
                _ => native.FinalGrant
            };
            return new(native.Candidate, grant, intent);
        };
        var result = await f.Provider.PrepareAsync(f.Context, default);
        Assert.Equal(LaunchPreparationFailure.Rejected, result.Failure); Assert.Null(result.Prepared);
        Assert.True(f.Capture.Candidate!.IsConsumed); Assert.Equal(0, f.Starter.Calls); Assert.Equal(0, f.Vault.Calls); f.AssertReleased();
        Assert.Equal(NativeHandoffPreparationStatus.RequestChanged, f.Provider.GetDiagnostic(f.Intent.OperationId)?.PreparationStatus);
    }

    [Fact]
    public async Task AForeignCapturedContextCannotBeAdoptedEvenWhenTheFinalIntentWasUnchanged()
    {
        await using var f = new Fixture(_root);
        f.Capture.ChangeContext = context => new(context.LaunchOperationId, Guid.NewGuid(), context.ExpectedUserId,
            context.PendingAuthorizationGeneration, context.Destination);
        var result = await f.Provider.PrepareAsync(f.Context, default);
        Assert.Equal(LaunchPreparationFailure.Rejected, result.Failure); Assert.Null(result.Prepared);
        Assert.True(f.Capture.Candidate!.IsConsumed); Assert.Equal(0, f.Starter.Calls); f.AssertReleased();
        Assert.Equal(NativeHandoffPreparationStatus.RequestChanged, f.Provider.GetDiagnostic(f.Intent.OperationId)?.PreparationStatus);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("consumed")]
    [InlineData("closed")]
    [InlineData("cancelled")]
    public async Task PreparedHandleCannotReuseExpiredConsumedClosedOrCancelledCandidates(string stopped)
    {
        await using var f = new Fixture(_root); using var stop = new CancellationTokenSource();
        var result = await f.Provider.PrepareAsync(f.Context, stop.Token); await using var handle = result.Prepared!;
        switch (stopped)
        {
            case "expired": f.Capture.Clock.Advance(TimeSpan.FromSeconds(10)); break;
            case "consumed": f.Capture.Candidate!.Dispose(); break;
            case "closed": f.Capture.Candidate!.Context.Dispose(); Assert.False(f.Capture.Candidate.IsConsumed); break;
            default: stop.Cancel(); break;
        }
        Assert.True(handle.IsExpired); using var boundary = Boundary(handle.DispatchIntent);
        Assert.Equal(ProviderLaunchStatus.RejectedBeforeStart, (await handle.DispatchAsync(boundary, default)).Status);
        Assert.Equal(0, f.Starter.Calls); Assert.Equal(0, f.Vault.Calls); Assert.True(f.Capture.Candidate!.IsConsumed); f.AssertReleased();
    }

    [Theory]
    [InlineData(NativeHandoffPreparationStatus.Expired, LaunchPreparationFailure.Expired)]
    [InlineData(NativeHandoffPreparationStatus.CleanupFailed, LaunchPreparationFailure.CleanupFailed)]
    [InlineData(NativeHandoffPreparationStatus.CaptureRejected, LaunchPreparationFailure.Rejected)]
    [InlineData(NativeHandoffPreparationStatus.IdentityMismatch, LaunchPreparationFailure.Rejected)]
    [InlineData(NativeHandoffPreparationStatus.StorageUnavailable, LaunchPreparationFailure.Rejected)]
    public async Task FailedPreparationDisposesUnexpectedReturnedCandidatesAndTheHeldExecutable(NativeHandoffPreparationStatus status, LaunchPreparationFailure expected)
    {
        await using var f = new Fixture(_root); f.Preparer.Status = status;
        var result = await f.Provider.PrepareAsync(f.Context, default);
        Assert.Equal(expected, result.Failure); Assert.Null(result.Prepared); Assert.True(f.Capture.Candidate!.IsConsumed);
        Assert.Equal(0, f.Starter.Calls); f.AssertReleased();
    }

    [Fact]
    public async Task UntrustedExecutableRejectsBeforeAnyAccountOrBrowserWork()
    {
        await using var f = new Fixture(_root, NativeClientTrustOutcome.RevocationUnavailable);
        var result = await f.Provider.PrepareAsync(f.Context, default);
        Assert.Equal(LaunchPreparationFailure.Rejected, result.Failure); Assert.Null(result.Prepared);
        Assert.Equal(1, f.Policy.Calls); Assert.Equal(0, f.Preparer.Calls); Assert.Equal(0, f.Capture.Calls); Assert.Equal(0, f.Vault.Calls); Assert.Equal(0, f.Starter.Calls); f.AssertReleased();
    }

    [Fact]
    public async Task OneActiveHandleOccupiesTheProviderUntilItIsActuallyDisposed()
    {
        await using var f = new Fixture(_root);
        var first = await f.Provider.PrepareAsync(f.Context, default); var handle = first.Prepared!;
        var busy = await f.Provider.PrepareAsync(new(f.Intent with { OperationId = Guid.NewGuid() }), default);
        Assert.Equal(LaunchPreparationFailure.Rejected, busy.Failure); Assert.Null(busy.Prepared); Assert.Equal(1, f.Preparer.Calls);
        await handle.DisposeAsync(); await handle.DisposeAsync(); f.AssertReleased();
        var next = await f.Provider.PrepareAsync(new(f.Intent with { OperationId = Guid.NewGuid() }), default);
        Assert.NotNull(next.Prepared); Assert.Equal(2, f.Preparer.Calls); Assert.True(handle.IsExpired);
        await next.Prepared!.DisposeAsync(); Assert.Equal(0, f.Starter.Calls);
    }

    [Theory]
    [InlineData("boundary")]
    [InlineData("validation")]
    [InlineData("current")]
    [InlineData("admission")]
    [InlineData("grant")]
    [InlineData("pre-cancel")]
    public async Task DispatchRefusalsNeverStartAndAlwaysReleasePreparation(string refusal)
    {
        await using var f = new Fixture(_root); var result = await f.Provider.PrepareAsync(f.Context, default);
        await using var handle = result.Prepared!; f.Vault.Authorized = refusal != "grant";
        using var boundary = new LaunchDispatchBoundary(refusal == "boundary" ? handle.DispatchIntent with { PlaceId = 1819 } : handle.DispatchIntent,
            (_, _) => Task.FromResult(refusal != "validation"), () => refusal != "current", () => new(refusal != "admission", "Synthetic admission"));
        using var stop = new CancellationTokenSource(); if (refusal == "pre-cancel") stop.Cancel();
        Assert.Equal(ProviderLaunchStatus.RejectedBeforeStart, (await handle.DispatchAsync(boundary, stop.Token)).Status);
        Assert.Equal(0, f.Starter.Calls); Assert.True(f.Capture.Candidate!.IsConsumed); Assert.True(handle.IsExpired); f.AssertReleased();
    }

    [Fact]
    public async Task CancellationAndProviderDisposeJoinTheActualLateBrowserAndItsReturnedCandidate()
    {
        await using var f = new Fixture(_root); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var stop = new CancellationTokenSource();
        f.Capture.BeforeReturn = async ct => { entered.TrySetResult(); await release.Task; };
        var preparation = f.Provider.PrepareAsync(f.Context, stop.Token); Task? disposing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel();
            disposing = f.Provider.DisposeAsync().AsTask(); Assert.False(disposing.IsCompleted); Assert.False(preparation.IsCompleted);
            Assert.True(f.Capture.Token.IsCancellationRequested); f.AssertPinned();
        }
        finally
        {
            release.TrySetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation.WaitAsync(TimeSpan.FromSeconds(3)));
            await (disposing ?? f.Provider.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.True(f.Capture.Candidate!.IsConsumed); Assert.Equal(0, f.Starter.Calls); f.AssertReleased();
        Assert.Equal(0, f.Capture.Disposals); Assert.Equal(0, f.Preparer.Disposals); Assert.Equal(0, f.Vault.Disposals);
        Assert.Equal(ClientExecutableTrustStatus.VerifiedRobloxPublisher, (await f.Trust.VerifyAsync(f.Installation, default)).Status);
    }

    [Fact]
    public async Task HandleDisposeJoinsAnUncooperativeValidationWithoutDispatchingOrReleasingItsFileEarly()
    {
        await using var f = new Fixture(_root); var preparation = await f.Provider.PrepareAsync(f.Context, default); var handle = preparation.Prepared!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var boundary = new LaunchDispatchBoundary(handle.DispatchIntent, async (_, _) => { entered.TrySetResult(); await release.Task; return true; }, () => true, () => new(true, "Synthetic"));
        var work = handle.DispatchAsync(boundary, default); Task? disposing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); disposing = handle.DisposeAsync().AsTask();
            Assert.True(handle.IsExpired); Assert.False(disposing.IsCompleted); Assert.False(work.IsCompleted); f.AssertPinned();
        }
        finally
        {
            release.TrySetResult(); await work.WaitAsync(TimeSpan.FromSeconds(3));
            await (disposing ?? handle.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Equal(ProviderLaunchStatus.RejectedBeforeStart, (await work).Status); Assert.Equal(0, f.Starter.Calls); f.AssertReleased();
    }

    [Fact]
    public async Task NativeStartStaysOwnedAndItsOriginalReceiptSurvivesLateCancellationAndProviderDispose()
    {
        await using var f = new Fixture(_root); using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Starter.Run = _ => { entered.TrySetResult(); release.Wait(); return new ProcessReceipt(); };
        var preparation = await f.Provider.PrepareAsync(f.Context, default); await using var handle = preparation.Prepared!;
        using var stop = new CancellationTokenSource(); using var boundary = Boundary(handle.DispatchIntent);
        var work = handle.DispatchAsync(boundary, stop.Token); Task? disposing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel(); disposing = f.Provider.DisposeAsync().AsTask();
            Assert.False(work.IsCompleted); Assert.False(disposing.IsCompleted); f.AssertPinned();
        }
        finally
        {
            release.Set(); await work.WaitAsync(TimeSpan.FromSeconds(3));
            await (disposing ?? f.Provider.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(3));
        }
        var result = await work; Assert.Equal(ProviderLaunchStatus.ClientStartedUnverified, result.Status);
        Assert.Equal(13579, result.ProcessId); Assert.NotNull(result.ProcessStartUtcTicks); Assert.Null(result.ObservedUserId);
        Assert.Equal(1, f.Starter.Calls); Assert.True(f.Capture.Candidate!.IsConsumed); f.AssertReleased();
    }

    [Fact]
    public async Task MissingProcessReceiptIsUncertainAndCannotAutomaticallyReplayTheCandidate()
    {
        await using var f = new Fixture(_root); f.Starter.Run = _ => null;
        var preparation = await f.Provider.PrepareAsync(f.Context, default); await using var handle = preparation.Prepared!;
        using var boundary = Boundary(handle.DispatchIntent);
        var result = await handle.DispatchAsync(boundary, default);
        Assert.Equal(ProviderLaunchStatus.Uncertain, result.Status); Assert.Null(result.ProcessId); Assert.Null(result.ObservedUserId);
        Assert.Equal(ProviderLaunchStatus.RejectedBeforeStart, (await handle.DispatchAsync(boundary, default)).Status);
        Assert.Equal(1, f.Starter.Calls); f.AssertReleased();
    }

    [Fact]
    public async Task ProviderExceptionsCannotEscapeAsRawCredentialOrHandoffMessages()
    {
        await using var f = new Fixture(_root); f.Preparer.Throw = true;
        var result = await f.Provider.PrepareAsync(f.Context, default);
        Assert.Equal(LaunchPreparationFailure.Rejected, result.Failure); Assert.Null(result.Prepared);
        Assert.DoesNotContain(SyntheticHandoff.Ticket, result + JsonSerializer.Serialize(result)); Assert.Equal(0, f.Starter.Calls); f.AssertReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedOwnedCleanupPermanentlyBlocksFurtherPreparationOnThisProvider(bool reportedByPreparer)
    {
        await using var f = new Fixture(_root);
        if (reportedByPreparer) f.Preparer.Status = NativeHandoffPreparationStatus.CleanupFailed;
        else f.Preparer.Transform = native =>
        {
            // Dispose the valid synthetic candidate before injecting an unusable owned
            // container whose Dispose cannot complete. No live resources are leaked.
            native.Dispose(); return new(null!, native.FinalGrant, native.Intent);
        };
        var failed = await f.Provider.PrepareAsync(f.Context, default);
        Assert.Equal(LaunchPreparationFailure.CleanupFailed, failed.Failure); Assert.Null(failed.Prepared);
        Assert.True(f.Capture.Candidate!.IsConsumed); f.AssertReleased();
        Assert.Equal(reportedByPreparer ? NativeHandoffPreparationStatus.CleanupFailed : NativeHandoffPreparationStatus.CaptureRejected,
            f.Provider.GetDiagnostic(f.Intent.OperationId)?.PreparationStatus);
        f.Preparer.Transform = null; f.Preparer.Status = NativeHandoffPreparationStatus.Ready;
        var retry = await f.Provider.PrepareAsync(new(f.Intent with { OperationId = Guid.NewGuid() }), default);
        Assert.Equal(LaunchPreparationFailure.CleanupFailed, retry.Failure); Assert.Null(retry.Prepared);
        Assert.Equal(1, f.Policy.Calls); Assert.Equal(1, f.Preparer.Calls); Assert.Equal(1, f.Capture.Calls);
        Assert.Equal(0, f.Vault.Calls); Assert.Equal(0, f.Starter.Calls);
        Assert.DoesNotContain(SyntheticHandoff.Ticket, failed + JsonSerializer.Serialize(failed) + retry);
    }

    [Fact]
    public async Task CancelledPreparerCleanupFailureRemainsVisibleAndPermanentlyBlocksReuse()
    {
        await using var f = new Fixture(_root); using var stop = new CancellationTokenSource();
        f.Preparer.Status = NativeHandoffPreparationStatus.CleanupFailed;
        f.Preparer.Transform = native => { stop.Cancel(); return native; };

        var failed = await f.Provider.PrepareAsync(f.Context, stop.Token);
        Assert.True(stop.IsCancellationRequested);
        Assert.Equal(LaunchPreparationFailure.CleanupFailed, failed.Failure); Assert.Null(failed.Prepared);
        Assert.True(f.Capture.Candidate!.IsConsumed); f.AssertReleased();

        f.Preparer.Status = NativeHandoffPreparationStatus.Ready; f.Preparer.Transform = null;
        var retry = await f.Provider.PrepareAsync(new(f.Intent with { OperationId = Guid.NewGuid() }), default);
        Assert.Equal(LaunchPreparationFailure.CleanupFailed, retry.Failure); Assert.Null(retry.Prepared);
        Assert.Equal(1, f.Policy.Calls); Assert.Equal(1, f.Preparer.Calls); Assert.Equal(1, f.Capture.Calls);
        Assert.Equal(0, f.Vault.Calls); Assert.Equal(0, f.Starter.Calls);
        Assert.Equal(0, f.Capture.Disposals); Assert.Equal(0, f.Preparer.Disposals); Assert.Equal(0, f.Vault.Disposals);
        Assert.DoesNotContain(SyntheticHandoff.Ticket, failed + JsonSerializer.Serialize(failed) + retry);
    }

    private static LaunchDispatchBoundary Boundary(LaunchIntent intent) => new(intent, (_, _) => Task.FromResult(true), () => true, () => new(true, "Synthetic fresh reservation"));
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly string Executable;
        public readonly Policy Policy;
        public readonly Starter Starter = new();
        public readonly Preparer Preparer = new();
        public readonly Capture Capture = new();
        public readonly Vault Vault = new();
        public readonly WindowsClientExecutableTrust Trust;
        public readonly WindowsNativeAccountLaunchProvider Provider;
        public readonly ClientInstallationCandidate Installation;
        public readonly LaunchIntent Intent = new(Guid.NewGuid(), Guid.NewGuid(), 5, 123, 1818, 7, DateTimeOffset.UtcNow, 4, SyntheticHandoff.Job, RobloxServerKind.Public, true);
        public LaunchPreparationContext Context { get; }
        public Fixture(string root, NativeClientTrustOutcome outcome = NativeClientTrustOutcome.VerifiedCached)
        {
            Directory.CreateDirectory(root); Executable = Path.Combine(root, "RobloxPlayerBeta.exe"); File.WriteAllText(Executable, "Synthetic ordinary bytes, never executed");
            Installation = new(ClientInstallationProduct.Roblox, Executable, "Synthetic", "1", ClientInstallationFileState.PresentUnverified, []);
            Policy = new(outcome); Trust = new(Policy, starter: Starter); Context = new(Intent);
            Provider = new(Installation, Trust, Preparer, Capture, Vault);
        }
        public void AssertPinned() => Assert.Throws<IOException>(() => File.Open(Executable, FileMode.Open, FileAccess.Write, FileShare.None).Dispose());
        public void AssertReleased() { using var file = File.Open(Executable, FileMode.Open, FileAccess.Write, FileShare.None); }
        public async ValueTask DisposeAsync()
        { await Provider.DisposeAsync(); await Trust.DisposeAsync(); await Capture.DisposeAsync(); await Preparer.DisposeAsync(); Vault.Dispose(); }
    }
    private sealed class Policy(NativeClientTrustOutcome outcome) : IClientAuthenticodePolicy
    {
        public int Calls;
        public NativeClientTrustEvidence Verify(SafeFileHandle handle, string path, DateTimeOffset now, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref Calls);
            return new(outcome, new("Roblox Corporation", "Roblox Corporation", new string('A', 64),
                new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2099, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        }
    }
    private sealed class Capture : IRobloxNativeHandoffCapture
    {
        public readonly HandoffClock Clock = new();
        public int Calls, Disposals;
        public CancellationToken Token;
        public NativeHandoffCandidate? Candidate;
        public Func<NativeHandoffContext, NativeHandoffContext>? ChangeContext;
        public Func<CancellationToken, Task>? BeforeReturn;
        public async Task<NativeBrowserCapture> CaptureAsync(NativeHandoffContext context, RobloxSessionCredential credential, CancellationToken ct)
        {
            Calls++; Token = ct; Candidate = SyntheticHandoff.Capture(ChangeContext?.Invoke(context) ?? context, Clock);
            if (BeforeReturn is not null) await BeforeReturn(ct);
            return new(RobloxHandoffProbeOutcome.CapturedAndPrevented, Candidate, new("synthetic-native-current"), NativeHandoffCaptureStatus.AcceptedSourceGrammar);
        }
        public ValueTask DisposeAsync() { Disposals++; Candidate?.Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class Preparer : IRobloxNativeHandoffPreparer, IAsyncDisposable
    {
        public int Calls, Disposals, RevisionDelta;
        public bool Throw;
        public NativeHandoffPreparationStatus Status = NativeHandoffPreparationStatus.Ready;
        public NativeBrowserPreparationFacts? BrowserFacts;
        public NativeHandoffPreparation? Issued;
        public Func<NativeHandoffPreparation, NativeHandoffPreparation>? Transform;
        public async Task<NativeHandoffPreparationResult> PrepareNativeHandoffAsync(LaunchIntent intent, IRobloxNativeHandoffCapture browser, CancellationToken ct)
        {
            Calls++; if (Throw) throw new IOException(SyntheticHandoff.Ticket);
            var context = new NativeHandoffContext(intent.OperationId, intent.ProfileId, intent.UserId, intent.AuthorizationGeneration + 1,
                new(intent.PlaceId, intent.RequestedJobId, intent.RequestedServerKind));
            using var credential = new RobloxSessionCredential("synthetic-native-input");
            using var capture = await browser.CaptureAsync(context, credential, ct);
            var candidate = capture.TakeCandidate()!;
            var record = new AuthorizationRecord(intent.ProfileId, RobloxSessionIdentity.Provider, intent.UserId, intent.AuthorizationGeneration + 2,
                "", "", DateTimeOffset.MaxValue, "synthetic_player", AuthorizationHealth.Linked, RobloxSessionIdentity.Issuer,
                intent.UserId.ToString(CultureInfo.InvariantCulture), "synthetic-native-current");
            var grant = new AuthorizationWriteSnapshot(intent.ProfileId, record, record.Generation, record.Generation, context.OperationId, new string('A', 64));
            var native = new NativeHandoffPreparation(candidate, grant, intent with { ProfileRevision = intent.ProfileRevision + RevisionDelta, AuthorizationGeneration = record.Generation });
            Issued = Transform?.Invoke(native) ?? native; return new(Status, Issued, BrowserFacts);
        }
        public ValueTask DisposeAsync() { Disposals++; Issued?.Candidate?.Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class Vault : IAuthorizationDispatchVault, IDisposable
    {
        public int Calls, Disposals; public bool Authorized = true; public AuthorizationWriteSnapshot? Seen;
        public Task<AuthorizationDispatchResult> TryDispatchCurrentAsync(AuthorizationWriteSnapshot expected, Func<NativeHandoffDispatchResult> dispatch, CancellationToken ct)
        { Calls++; Seen = expected; ct.ThrowIfCancellationRequested(); return Task.FromResult(Authorized ? new AuthorizationDispatchResult(AuthorizationDispatchStatus.CallbackInvoked, dispatch()) : new(AuthorizationDispatchStatus.NotAuthorized)); }
        public void Dispose() => Disposals++;
    }
    private sealed class Starter : INativeHandoffProcessStarter
    {
        public int Calls; public string? ObservedArgument; public ProcessStartInfo? LastStart;
        public Func<ProcessStartInfo, INativeHandoffStartedProcess?>? Run;
        public INativeHandoffStartedProcess? Start(ProcessStartInfo start)
        { Calls++; LastStart = start; ObservedArgument = Assert.Single(start.ArgumentList); return Run is null ? new ProcessReceipt() : Run(start); }
    }
    private sealed class ProcessReceipt : INativeHandoffStartedProcess
    {
        public LaunchProcessReceipt? CaptureReceipt(DateTimeOffset at) => new(13579, at.AddMinutes(-1).UtcTicks, at);
        public void Dispose() { }
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-native-provider-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected native provider fixture path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
