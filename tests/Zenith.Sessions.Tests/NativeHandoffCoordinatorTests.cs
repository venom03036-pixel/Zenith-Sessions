using System.IO;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Actual isolated repository/DPAPI joins; all process starts and handoffs are synthetic delegates.
public sealed class NativeHandoffCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-native-handoff-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExactFinalGrantTargetAndFreshAdmissionProduceOnlyAnUnverifiedOriginalReceipt()
    {
        using var repository = new AccountRepository(_root); using var vault = new ProtectedCredentialVault(_root);
        var fixture = await Prepare(repository, vault, true); using var candidate = SyntheticHandoff.Capture(fixture.Context, fixture.Clock);
        string expected = SyntheticHandoff.Uri(fixture.Context, fixture.Clock); using var prepared = new Prepared();
        await using var coordinator = new NativeHandoffCoordinator(vault);
        int admissions = 0;
        var result = await coordinator.DispatchAsync(candidate, fixture.Grant, fixture.Intent, prepared,
            new LaunchAuthorizationValidator(repository, vault).ValidateAsync, () => true, () => { admissions++; return new(true, "synthetic fresh reservation"); }, default);
        Assert.Equal(NativeHandoffAttemptStatus.StartedUnverified, result.Status); Assert.Same(prepared.Receipt, result.ProcessReceipt);
        Assert.Equal(expected, prepared.Original); Assert.Equal(1, prepared.Starts); Assert.Equal(1, admissions); Assert.True(candidate.IsConsumed);
        Assert.DoesNotContain(SyntheticHandoff.Ticket, JsonSerializer.Serialize(result)); Assert.DoesNotContain("gameinfo", JsonSerializer.Serialize(result));
        // A second attempt cannot reuse the original candidate, even with a still-current grant.
        var second = await coordinator.DispatchAsync(candidate, fixture.Grant, fixture.Intent, prepared,
            new LaunchAuthorizationValidator(repository, vault).ValidateAsync, () => true, Admission, default);
        Assert.Equal(NativeHandoffAttemptStatus.NotAuthorized, second.Status); Assert.Equal(1, prepared.Starts);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("profile")]
    [InlineData("user")]
    [InlineData("generation")]
    [InlineData("place")]
    [InlineData("job")]
    [InlineData("owner")]
    public async Task AChangedFinalBindingCannotSelectOrRetargetThePreparedClient(string change)
    {
        using var repository = new AccountRepository(_root); using var vault = new ProtectedCredentialVault(_root);
        var fixture = await Prepare(repository, vault, true); using var candidate = SyntheticHandoff.Capture(fixture.Context, fixture.Clock); using var prepared = new Prepared();
        var intent = change switch
        {
            "operation" => fixture.Intent with { OperationId = Guid.NewGuid() },
            "profile" => fixture.Intent with { ProfileId = Guid.NewGuid() },
            "user" => fixture.Intent with { UserId = 124 },
            "generation" => fixture.Intent with { AuthorizationGeneration = 1 },
            "place" => fixture.Intent with { PlaceId = 1819 },
            "job" => fixture.Intent with { RequestedJobId = null, RequestedServerKind = RobloxServerKind.Unknown },
            _ => fixture.Intent
        };
        var snapshot = change == "owner" ? fixture.Grant with { HighestMutationId = Guid.NewGuid() } : fixture.Grant;
        await using var coordinator = new NativeHandoffCoordinator(vault);
        var result = await coordinator.DispatchAsync(candidate, snapshot, intent, prepared,
            new LaunchAuthorizationValidator(repository, vault).ValidateAsync, () => true, Admission, default);
        Assert.Equal(NativeHandoffAttemptStatus.NotAuthorized, result.Status); Assert.Equal(0, prepared.Starts); Assert.True(candidate.IsConsumed);
    }

    [Fact]
    public async Task ReconstructingTheSameQueueContextCreatesANewOwnerThatCannotUseTheOldFinalGrant()
    {
        using var repository = new AccountRepository(_root); using var vault = new ProtectedCredentialVault(_root);
        var fixture = await Prepare(repository, vault); using var first = SyntheticHandoff.Capture(fixture.Context, fixture.Clock); using var prepared = new Prepared();
        var validate = new LaunchAuthorizationValidator(repository, vault); await using var coordinator = new NativeHandoffCoordinator(vault);
        var result = await coordinator.DispatchAsync(first, fixture.Grant, fixture.Intent, prepared, validate.ValidateAsync, () => true, Admission, default);
        Assert.Equal(NativeHandoffAttemptStatus.StartedUnverified, result.Status);
        using var replayContext = new NativeHandoffContext(fixture.Context.LaunchOperationId, fixture.Context.ProfileId, 123, 1, fixture.Context.Destination);
        using var replay = SyntheticHandoff.Capture(replayContext, fixture.Clock);
        var rejected = await coordinator.DispatchAsync(replay, fixture.Grant, fixture.Intent, prepared, validate.ValidateAsync, () => true, Admission, default);
        Assert.Equal(NativeHandoffAttemptStatus.NotAuthorized, rejected.Status); Assert.Equal(1, prepared.Starts);
    }

    [Fact]
    public async Task ReplacementAfterRequestValidationStillConflictsAtTheActualVaultBoundary()
    {
        using var repository = new AccountRepository(_root); using var vault = new ProtectedCredentialVault(_root);
        var fixture = await Prepare(repository, vault); using var candidate = SyntheticHandoff.Capture(fixture.Context, fixture.Clock); using var prepared = new Prepared();
        var validate = new LaunchAuthorizationValidator(repository, vault); await using var coordinator = new NativeHandoffCoordinator(vault);
        var result = await coordinator.DispatchAsync(candidate, fixture.Grant, fixture.Intent, prepared, async (intent, ct) =>
        {
            Assert.True(await validate.ValidateAsync(intent, ct));
            var foreign = fixture.Grant.Record! with { Generation = 3, SessionCookie = "synthetic-foreign-grant" };
            Assert.Equal(AuthorizationMutationStatus.Applied, (await vault.TryCommitSnapshotAsync(fixture.Grant, foreign, Guid.NewGuid(), ct)).Status);
            return true;
        }, () => true, Admission, default);
        Assert.Equal(NativeHandoffAttemptStatus.NotAuthorized, result.Status); Assert.Equal(0, prepared.Starts);
        Assert.Equal("synthetic-foreign-grant", (await vault.ReadAsync(fixture.Context.ProfileId, default))!.SessionCookie);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("epoch")]
    [InlineData("admission")]
    [InlineData("admission-throws")]
    [InlineData("expiry")]
    [InlineData("expiry-during-admission")]
    [InlineData("closed-during-admission")]
    public async Task EveryFinalGateCanPreventExecutionWithoutConsumingAnotherAccount(string gate)
    {
        using var repository = new AccountRepository(_root); using var vault = new ProtectedCredentialVault(_root);
        var fixture = await Prepare(repository, vault); using var candidate = SyntheticHandoff.Capture(fixture.Context, fixture.Clock); using var prepared = new Prepared();
        await using var coordinator = new NativeHandoffCoordinator(vault);
        if (gate == "expiry") fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        var result = await coordinator.DispatchAsync(candidate, fixture.Grant, fixture.Intent, prepared,
            (_, _) => Task.FromResult(gate != "request"), () => gate != "epoch", () =>
            {
                if (gate == "admission-throws") throw new IOException("synthetic-secret-must-not-escape");
                if (gate == "expiry-during-admission") fixture.Clock.Advance(TimeSpan.FromSeconds(10));
                if (gate == "closed-during-admission") fixture.Context.Dispose();
                return new(gate != "admission", "synthetic");
            }, default);
        Assert.Equal(0, prepared.Starts); Assert.Null(result.ProcessReceipt); Assert.True(candidate.IsConsumed);
        Assert.Equal(gate switch
        {
            "request" or "epoch" => NativeHandoffAttemptStatus.RequestChanged,
            "admission" or "admission-throws" => NativeHandoffAttemptStatus.AdmissionDenied,
            "closed-during-admission" => NativeHandoffAttemptStatus.NotStarted,
            _ => NativeHandoffAttemptStatus.Expired
        }, result.Status);
    }

    [Fact]
    public async Task WallClockChangesNeverRenewTheLocalMonotonicLifetime()
    {
        using var repository = new AccountRepository(_root); using var vault = new ProtectedCredentialVault(_root);
        var fixture = await Prepare(repository, vault); using var candidate = SyntheticHandoff.Capture(fixture.Context, fixture.Clock); using var prepared = new Prepared();
        fixture.Clock.Advance(TimeSpan.FromSeconds(10)); fixture.Clock.JumpWall(TimeSpan.FromDays(-10));
        await using var coordinator = new NativeHandoffCoordinator(vault);
        var result = await coordinator.DispatchAsync(candidate, fixture.Grant, fixture.Intent, prepared, (_, _) => Task.FromResult(true), () => true, Admission, default);
        Assert.Equal(NativeHandoffAttemptStatus.Expired, result.Status); Assert.Equal(0, prepared.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndDisposalKeepTheActualNativeCallbackOwnedAndPreserveItsReceipt(bool dispose)
    {
        using var repository = new AccountRepository(_root); using var vault = new ProtectedCredentialVault(_root);
        var fixture = await Prepare(repository, vault); using var candidate = SyntheticHandoff.Capture(fixture.Context, fixture.Clock);
        using var release = new ManualResetEventSlim(); using var prepared = new Prepared { Release = release };
        var coordinator = new NativeHandoffCoordinator(vault); using var stop = new CancellationTokenSource();
        Task<NativeHandoffAttempt> work = coordinator.DispatchAsync(candidate, fixture.Grant, fixture.Intent, prepared, (_, _) => Task.FromResult(true), () => true, Admission, stop.Token);
        Task closing = Task.CompletedTask;
        try
        {
            await prepared.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (dispose) { closing = coordinator.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted); }
            else { stop.Cancel(); coordinator.CancelPending(); }
            Assert.False(work.IsCompleted); Assert.Equal(1, prepared.Starts);
            // The original held callback remains the owner, rather than starting a second worker.
            using var otherContext = SyntheticHandoff.Context(); using var other = SyntheticHandoff.Capture(otherContext, fixture.Clock);
            if (!dispose)
            {
                var busy = await coordinator.DispatchAsync(other, fixture.Grant, fixture.Intent, prepared, (_, _) => Task.FromResult(true), () => true, Admission, default);
                Assert.Equal(NativeHandoffAttemptStatus.Busy, busy.Status); Assert.False(other.IsConsumed);
            }
        }
        finally { release.Set(); }
        var result = await work.WaitAsync(TimeSpan.FromSeconds(3)); await closing; await coordinator.DisposeAsync();
        Assert.Equal(NativeHandoffAttemptStatus.StartedUnverified, result.Status); Assert.Same(prepared.Receipt, result.ProcessReceipt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStartedOrUncertainReceiptCarriesTheCandidateTrackerBesideTheOriginalReceipt(bool uncertain)
    {
        using var repository = new AccountRepository(_root); using var vault = new ProtectedCredentialVault(_root);
        var fixture = await Prepare(repository, vault); using var candidate = SyntheticHandoff.Capture(fixture.Context, fixture.Clock);
        using var prepared = new Prepared { Outcome = uncertain ? NativeHandoffDispatchOutcome.Uncertain : NativeHandoffDispatchOutcome.StartedUnverified };
        await using var coordinator = new NativeHandoffCoordinator(vault);
        var result = await coordinator.DispatchAsync(candidate, fixture.Grant, fixture.Intent, prepared, (_, _) => Task.FromResult(true), () => true, Admission, default);
        Assert.Equal(uncertain ? NativeHandoffAttemptStatus.Uncertain : NativeHandoffAttemptStatus.StartedUnverified, result.Status);
        Assert.Same(prepared.Receipt, result.ProcessReceipt); Assert.Null(result.ProcessReceipt!.BrowserTrackerId);
        Assert.Equal(long.Parse(SyntheticHandoff.Tracker, System.Globalization.CultureInfo.InvariantCulture), result.BrowserTrackerId);
        Assert.DoesNotContain(SyntheticHandoff.Ticket, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task ARefusedAttemptHasNoReceiptAndNoTracker()
    {
        using var repository = new AccountRepository(_root); using var vault = new ProtectedCredentialVault(_root);
        var fixture = await Prepare(repository, vault); using var candidate = SyntheticHandoff.Capture(fixture.Context, fixture.Clock); using var prepared = new Prepared();
        await using var coordinator = new NativeHandoffCoordinator(vault);
        var result = await coordinator.DispatchAsync(candidate, fixture.Grant, fixture.Intent, prepared, (_, _) => Task.FromResult(true), () => true,
            () => new(false, "Synthetic refusal"), default);
        Assert.Equal(NativeHandoffAttemptStatus.AdmissionDenied, result.Status); Assert.Equal(0, prepared.Starts);
        Assert.Null(result.ProcessReceipt); Assert.Null(result.BrowserTrackerId);
    }

    [Fact]
    public async Task CancellationWhileRequestValidationWaitsClosesPreparationBeforeStart()
    {
        using var repository = new AccountRepository(_root); using var vault = new ProtectedCredentialVault(_root);
        var fixture = await Prepare(repository, vault); using var candidate = SyntheticHandoff.Capture(fixture.Context, fixture.Clock); using var prepared = new Prepared();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new NativeHandoffCoordinator(vault);
        var work = coordinator.DispatchAsync(candidate, fixture.Grant, fixture.Intent, prepared, (_, _) => { waiting.TrySetResult(); return release.Task; }, () => true, Admission, default);
        try { await waiting.Task.WaitAsync(TimeSpan.FromSeconds(3)); coordinator.CancelPending(); Assert.False(work.IsCompleted); }
        finally { release.TrySetResult(true); }
        var result = await work; Assert.Equal(NativeHandoffAttemptStatus.Cancelled, result.Status); Assert.Equal(0, prepared.Starts);
        Assert.Equal(NativeHandoffCaptureStatus.OperationAlreadyCaptured, NativeRobloxHandoff.Capture(fixture.Context, "https://www.roblox.com/games/1818", RobloxHandoffProbePolicy.OfficialOrigin, SyntheticHandoff.Uri(fixture.Context, fixture.Clock), fixture.Clock).Status);
    }

    [Fact]
    public async Task AThrowAfterEnteringTheNativeBoundaryIsUncertainAndNeverLeaksTheHandoff()
    {
        using var repository = new AccountRepository(_root); using var vault = new ProtectedCredentialVault(_root);
        var fixture = await Prepare(repository, vault); using var candidate = SyntheticHandoff.Capture(fixture.Context, fixture.Clock); using var prepared = new Prepared { Throw = true };
        await using var coordinator = new NativeHandoffCoordinator(vault);
        var result = await coordinator.DispatchAsync(candidate, fixture.Grant, fixture.Intent, prepared, (_, _) => Task.FromResult(true), () => true, Admission, default);
        Assert.Equal(NativeHandoffAttemptStatus.Uncertain, result.Status); Assert.Equal(1, prepared.Starts); Assert.Null(result.ProcessReceipt);
        Assert.DoesNotContain(SyntheticHandoff.Ticket, JsonSerializer.Serialize(result) + result); Assert.True(candidate.IsConsumed);
    }

    private static LaunchAdmission Admission() => new(true, "Synthetic test reservation, not measured resources.");
    private static async Task<Fixture> Prepare(AccountRepository repository, ProtectedCredentialVault vault, bool job = false)
    {
        await repository.InitializeAsync(default); var profile = await repository.SaveAsync(AccountProfile.Create("Synthetic native preparation") with { PreferredPlaceId = 1818 }, default);
        var context = new NativeHandoffContext(Guid.NewGuid(), profile.Id, 123, 1, new(1818, job ? SyntheticHandoff.Job : null, job ? RobloxServerKind.Public : RobloxServerKind.Unknown));
        var record = new AuthorizationRecord(profile.Id, RobloxSessionIdentity.Provider, 123, 1, "", "", DateTimeOffset.MaxValue, "Synthetic", AuthorizationHealth.Linked, RobloxSessionIdentity.Issuer, "123", "synthetic-current-session");
        await vault.SaveAsync(record, default); var pending = await vault.ReadWriteSnapshotAsync(profile.Id, default);
        Assert.Equal(AuthorizationMutationStatus.Applied, (await vault.TryCommitSnapshotAsync(pending, record with { Generation = 2 }, context.OperationId, default)).Status);
        var final = await vault.ReadWriteSnapshotAsync(profile.Id, default);
        var intent = new LaunchIntent(context.LaunchOperationId, profile.Id, profile.Revision, 123, 1818, 0, DateTimeOffset.UtcNow, 2, context.Destination.JobId, context.Destination.ServerKind, true);
        return new(context, final, intent, new HandoffClock());
    }
    private sealed record Fixture(NativeHandoffContext Context, AuthorizationWriteSnapshot Grant, LaunchIntent Intent, HandoffClock Clock);
    private sealed class Prepared : IPreparedNativeHandoffStart
    {
        public int Starts; public string? Original; public bool Throw; public ManualResetEventSlim? Release;
        public NativeHandoffDispatchOutcome Outcome = NativeHandoffDispatchOutcome.StartedUnverified;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LaunchProcessReceipt Receipt { get; } = new(700002, DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks, DateTimeOffset.UtcNow);
        public NativeHandoffDispatchResult Start(string original)
        {
            Interlocked.Increment(ref Starts); Original = original; Started.TrySetResult();
            if (Release is not null && !Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic gate exceeded its bound.");
            if (Throw) throw new IOException(original);
            return new(Outcome, Receipt);
        }
        public void Dispose() => Original = null;
    }
    public void Dispose()
    {
        string root = Path.GetFullPath(_root);
        if (!string.Equals(Path.GetDirectoryName(root), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(root).StartsWith("zenith-native-handoff-test-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected synthetic fixture cleanup path.");
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
