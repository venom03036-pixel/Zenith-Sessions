using System.IO;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Memory-only child/trust owners. Candidate paths are lexical fixtures; no files, browser,
// network, authentication flow, native executable or process inventory is accessed.
public sealed partial class ConfiguredNativeLaunchProviderTests
{
    private const string Secret = "synthetic-configured-protected-value";
    private static ClientInstallationCandidate Candidate(string name = "A") => new(ClientInstallationProduct.Roblox,
        @"C:\Synthetic\" + name + @"\RobloxPlayerBeta.exe", "Synthetic player", "1", ClientInstallationFileState.PresentUnverified,
        [new(ClientInstallationHintSource.StandardInstallPath, ClientInstallationProduct.Roblox)]);
    private static LaunchPreparationContext Context() => new(new(Guid.NewGuid(), Guid.NewGuid(), 5, 123, 1818, 7,
        DateTimeOffset.UtcNow, 4, HasExplicitDestination: true));
    private static LaunchDispatchBoundary Boundary(LaunchIntent intent) => new(intent, (_, _) => Task.FromResult(true), () => true, () => new(true, "Synthetic admission"));

    [Fact]
    public async Task RealConstructorBorrowsServicesAndNeverEnablesOrdinaryLaunch()
    {
        var borrowed = new Borrowed(); var slot = new WindowsConfiguredNativeLaunchProvider(borrowed, borrowed, borrowed);
        Assert.False(slot.Selected); Assert.True(slot.CleanupHealthy); Assert.False(slot.IsBusy);
        Assert.Equal(CapabilityState.Unverified, slot.AccountLaunch.State);
        Assert.Equal(ProviderLaunchStatus.RejectedBeforeStart, (await slot.LaunchAsync(Context().OriginalIntent, default)).Status);
        Assert.False(await slot.ConfirmArrivalAsync(Context().OriginalIntent, new(ProviderLaunchStatus.VerifiedArrival), default));
        Assert.Equal(LaunchPreparationFailure.Rejected, (await slot.PrepareAsync(Context(), default)).Failure);
        await slot.DisposeAsync(); await slot.DisposeAsync();
        Assert.Equal(0, borrowed.Calls); Assert.Equal(0, borrowed.Disposals);
    }

    [Theory]
    [InlineData(ProviderLaunchStatus.ClientStartedUnverified, true)]
    [InlineData(ProviderLaunchStatus.Uncertain, true)]
    [InlineData(ProviderLaunchStatus.Uncertain, false)]
    public async Task TheChildsTrackerIsKeptOnlyBesideAValidReceipt(ProviderLaunchStatus status, bool receipt)
    {
        await using var h = new Harness(); h.Slot.Configure(Candidate());
        var ready = await h.Slot.PrepareAsync(Context(), default); await using var handle = ready.Prepared!;
        h.Children[0].Handle!.Reply = new(status, ProcessId: receipt ? 12345 : null,
            ProcessStartUtcTicks: receipt ? DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks : null, BrowserTrackerId: 1234567890);
        using var boundary = Boundary(handle.DispatchIntent);
        var result = await handle.DispatchAsync(boundary, default);
        Assert.Equal(status, result.Status); Assert.Equal(receipt ? 1234567890 : null, result.BrowserTrackerId);
    }

    [Fact]
    public async Task SelectionIsExplicitTransientCopiedAndNeverCreatesAChildByItself()
    {
        await using var h = new Harness(); var evidence = new List<ClientInstallationEvidence> { new(ClientInstallationHintSource.VersionDirectory, ClientInstallationProduct.Roblox) };
        Assert.Equal(LaunchPreparationFailure.Rejected, (await h.Slot.PrepareAsync(Context(), default)).Failure);
        h.Slot.Configure(Candidate() with { Evidence = evidence }); evidence.Clear();
        Assert.True(h.Slot.Selected); Assert.Empty(h.Created);
        Assert.Equal(ProviderLaunchStatus.RejectedBeforeStart, (await h.Slot.LaunchAsync(Context().OriginalIntent, default)).Status);
        var result = await h.Slot.PrepareAsync(Context(), default); await using var handle = result.Prepared!;
        Assert.Single(h.Created[0].Evidence); Assert.Equal(ClientInstallationHintSource.VersionDirectory, h.Created[0].Evidence[0].Source);
        Assert.Equal(CapabilityState.Unverified, h.Slot.AccountLaunch.State); Assert.Equal(0, h.Trust.Disposals);
        await handle.DisposeAsync(); h.Slot.Configure(null); Assert.False(h.Slot.Selected); Assert.False(h.Slot.IsBusy);
    }

    [Theory]
    [InlineData("RobloxPlayerBeta.exe")]
    [InlineData("RobloxPlayer.exe")]
    [InlineData("RobloxPlayerLauncher.exe")]
    [InlineData("Roblox.exe")]
    public async Task OnlyTheSharedDirectPlayerFilenamesCanBecomeTheTrialInstallation(string name)
    {
        await using var h = new Harness(); var candidate = Candidate() with { ExecutablePath = @"C:\Synthetic\" + name };
        bool direct = RobloxNativePlayerFiles.IsDirectPlayer(candidate.ExecutablePath);
        Assert.Equal(name is "RobloxPlayerBeta.exe" or "RobloxPlayer.exe", direct);
        // The shell asks CanSelect before revoking trial permission; it must agree with Configure exactly.
        Assert.Equal(direct, WindowsConfiguredNativeLaunchProvider.CanSelect(candidate));
        if (direct) h.Slot.Configure(candidate); else Assert.Throws<ArgumentException>(() => h.Slot.Configure(candidate));
        Assert.Equal(direct, h.Slot.Selected); Assert.Empty(h.Created);
    }

    [Fact]
    public void CanSelectIsFalseForNoSelectionAndTrueForAnAcceptedNativePlayer()
    {
        Assert.False(WindowsConfiguredNativeLaunchProvider.CanSelect(null));
        Assert.True(WindowsConfiguredNativeLaunchProvider.CanSelect(Candidate()));
    }

    [Theory]
    [InlineData("bootstrapper")]
    [InlineData("launcher")]
    [InlineData("missing")]
    [InlineData("relative")]
    [InlineData("unc")]
    [InlineData("device")]
    [InlineData("ads")]
    [InlineData("traversal")]
    [InlineData("wildcard")]
    [InlineData("control")]
    [InlineData("empty-evidence")]
    [InlineData("duplicate-evidence")]
    [InlineData("foreign-evidence")]
    [InlineData("unknown-evidence")]
    [InlineData("metadata")]
    public async Task InvalidSelectionsCannotBecomeAConfiguredNativeHandler(string invalid)
    {
        await using var h = new Harness(); var candidate = Candidate();
        candidate = invalid switch
        {
            "bootstrapper" => candidate with { Product = ClientInstallationProduct.Bloxstrap },
            "launcher" => candidate with { ExecutablePath = @"C:\Synthetic\RobloxPlayerLauncher.exe" },
            "missing" => candidate with { FileState = ClientInstallationFileState.Missing },
            "relative" => candidate with { ExecutablePath = "RobloxPlayerBeta.exe" },
            "unc" => candidate with { ExecutablePath = @"\\server\share\RobloxPlayerBeta.exe" },
            "device" => candidate with { ExecutablePath = @"\\?\C:\Synthetic\RobloxPlayerBeta.exe" },
            "ads" => candidate with { ExecutablePath = @"C:\Synthetic\RobloxPlayerBeta.exe:stream" },
            "traversal" => candidate with { ExecutablePath = @"C:\Synthetic\..\RobloxPlayerBeta.exe" },
            "wildcard" => candidate with { ExecutablePath = @"C:\Syn*thetic\RobloxPlayerBeta.exe" },
            "control" => candidate with { ExecutablePath = "C:\\Synthetic\\RobloxPlayerBeta.exe\n" },
            "empty-evidence" => candidate with { Evidence = [] },
            "duplicate-evidence" => candidate with { Evidence = [candidate.Evidence[0], candidate.Evidence[0]] },
            "foreign-evidence" => candidate with { Evidence = [new(ClientInstallationHintSource.VersionDirectory, ClientInstallationProduct.Fishstrap)] },
            "unknown-evidence" => candidate with { Evidence = [new((ClientInstallationHintSource)999, ClientInstallationProduct.Roblox)] },
            _ => candidate with { ProductName = "invalid\nmetadata" }
        };
        Assert.False(WindowsConfiguredNativeLaunchProvider.CanSelect(candidate));
        Assert.Throws<ArgumentException>(() => h.Slot.Configure(candidate));
        Assert.False(h.Slot.Selected); Assert.False(h.Slot.IsBusy); Assert.True(h.Slot.CleanupHealthy); Assert.Empty(h.Created);
    }

    [Fact]
    public async Task ConfiguringANewSelectionFencesReadyWorkAndKeepsTheSlotUntilActualHandleCleanup()
    {
        await using var h = new Harness(); h.Slot.Configure(Candidate("A"));
        var context = Context(); var ready = await h.Slot.PrepareAsync(context, default); var handle = ready.Prepared!; var first = h.Children[0];
        first.Handle!.DisposeGate = Gate();
        h.Slot.Configure(Candidate("B"));
        await first.Handle.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            Assert.True(first.Token.IsCancellationRequested); Assert.True(handle.IsExpired); Assert.True(h.Slot.IsBusy); Assert.True(h.Slot.Selected);
            Assert.Equal(LaunchPreparationFailure.Rejected, (await h.Slot.PrepareAsync(Context(), default)).Failure);
            Assert.Single(h.Created); Assert.Equal(0, first.Disposals);
        }
        finally { first.Handle.DisposeGate.TrySetResult(); await handle.DisposeAsync(); }
        Assert.False(h.Slot.IsBusy); Assert.Equal(1, first.Disposals); Assert.Equal(1, first.Handle.Disposals);
        var next = await h.Slot.PrepareAsync(Context(), default); await using var second = next.Prepared!;
        Assert.Equal(Candidate("A").ExecutablePath, h.Created[0].ExecutablePath);
        Assert.Equal(Candidate("B").ExecutablePath, h.Created[1].ExecutablePath); Assert.Equal(0, h.Trust.Disposals);
    }

    [Theory]
    [InlineData("change")]
    [InlineData("clear")]
    [InlineData("stop")]
    public async Task LateReadyAfterConfigurationOrStopIsJoinedDisposedAndNeverPublished(string stop)
    {
        await using var h = new Harness(); var child = new Child { PrepareGate = Gate() }; h.Next.Enqueue(child); h.Slot.Configure(Candidate("A"));
        var work = h.Slot.PrepareAsync(Context(), default);
        try
        {
            await child.PrepareStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (stop == "change") h.Slot.Configure(Candidate("B")); else if (stop == "clear") h.Slot.Configure(null); else h.Slot.CancelActive();
            Assert.True(child.Token.IsCancellationRequested); Assert.True(h.Slot.IsBusy); Assert.False(work.IsCompleted);
            Assert.Equal(stop != "clear", h.Slot.Selected);
            Assert.Equal(LaunchPreparationFailure.Rejected, (await h.Slot.PrepareAsync(Context(), default)).Failure); Assert.Single(h.Created);
        }
        finally { child.PrepareGate.TrySetResult(); }
        var result = await work.WaitAsync(TimeSpan.FromSeconds(2)); Assert.Equal(LaunchPreparationFailure.Rejected, result.Failure); Assert.Null(result.Prepared);
        Assert.Equal(1, child.Handle!.Disposals); Assert.Equal(1, child.Disposals); Assert.False(h.Slot.IsBusy); Assert.True(h.Slot.CleanupHealthy);
        if (stop == "clear") h.Slot.Configure(Candidate("B"));
        var next = await h.Slot.PrepareAsync(Context(), default); await next.Prepared!.DisposeAsync();
        Assert.Equal(Candidate(stop == "stop" ? "A" : "B").ExecutablePath, h.Created[1].ExecutablePath);
    }

    [Fact]
    public async Task PreCancelledPreparationCreatesNoChildAndDoesNotLoseSelection()
    {
        await using var h = new Harness(); h.Slot.Configure(Candidate()); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Slot.PrepareAsync(Context(), stop.Token));
        Assert.Empty(h.Created); Assert.True(h.Slot.Selected); Assert.False(h.Slot.IsBusy); h.Slot.CancelActive(); Assert.True(h.Slot.CleanupHealthy);
    }

    [Fact]
    public async Task DisposeJoinsAnUncooperativePreparationBeforeItsChildAndOwnedTrust()
    {
        await using var h = new Harness(); var child = new Child { PrepareGate = Gate() }; h.Next.Enqueue(child); h.Slot.Configure(Candidate());
        var preparation = h.Slot.PrepareAsync(Context(), default); Task? disposing = null;
        try
        {
            await child.PrepareStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)); disposing = h.Slot.DisposeAsync().AsTask();
            Assert.True(child.Token.IsCancellationRequested); Assert.False(disposing.IsCompleted); Assert.False(preparation.IsCompleted); Assert.Equal(0, h.Trust.Disposals);
            Assert.Throws<ObjectDisposedException>(() => h.Slot.Configure(null));
        }
        finally { child.PrepareGate.TrySetResult(); await preparation.WaitAsync(TimeSpan.FromSeconds(2)); await (disposing ?? h.Slot.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(2)); }
        Assert.Equal(1, child.Handle!.Disposals); Assert.Equal(1, child.Disposals); Assert.Equal(1, h.Trust.Disposals);
        await h.Slot.DisposeAsync(); Assert.Equal(1, h.Trust.Disposals);
    }

    [Theory]
    [InlineData(LaunchPreparationFailure.Rejected)]
    [InlineData(LaunchPreparationFailure.Expired)]
    public async Task OrdinaryPreparationFailureCleansUnexpectedHandlesBeforePermittingAnotherOwner(LaunchPreparationFailure failure)
    {
        await using var h = new Harness(); var child = new Child { Failure = failure }; h.Next.Enqueue(child); h.Slot.Configure(Candidate());
        var result = await h.Slot.PrepareAsync(Context(), default);
        Assert.Equal(failure, result.Failure); Assert.Null(result.Prepared); Assert.Equal(1, child.Handle!.Disposals); Assert.Equal(1, child.Disposals);
        Assert.True(h.Slot.CleanupHealthy); Assert.False(h.Slot.IsBusy);
        var next = await h.Slot.PrepareAsync(Context(), default); await next.Prepared!.DisposeAsync(); Assert.Equal(2, h.Created.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignOrAlreadyExpiredReadyHandlesAreDisposedInsteadOfAdopted(bool expired)
    {
        await using var h = new Harness(); var child = new Child { ForeignContext = !expired, ExpiredHandle = expired }; h.Next.Enqueue(child); h.Slot.Configure(Candidate());
        var result = await h.Slot.PrepareAsync(Context(), default);
        Assert.Equal(expired ? LaunchPreparationFailure.Expired : LaunchPreparationFailure.Rejected, result.Failure); Assert.Null(result.Prepared);
        Assert.Equal(1, child.Handle!.Disposals); Assert.Equal(1, child.Disposals); Assert.True(h.Slot.CleanupHealthy); Assert.False(h.Slot.IsBusy);
    }

    [Theory]
    [InlineData("reported")]
    [InlineData("cancelled-reported")]
    [InlineData("prepared-dispose")]
    [InlineData("child-dispose")]
    [InlineData("child-poison-before")]
    [InlineData("child-poison-after")]
    [InlineData("child-health-throws")]
    public async Task CleanupFailureOrChildPoisonCannotBeResetBySelectionChanges(string failure)
    {
        await using var h = new Harness { ExpectPoison = true }; var child = new Child(); h.Next.Enqueue(child); h.Slot.Configure(Candidate());
        if (failure is "reported" or "cancelled-reported") child.Failure = LaunchPreparationFailure.CleanupFailed;
        if (failure == "cancelled-reported") child.BeforeReturn = _ => h.Slot.CancelActive();
        if (failure == "child-poison-before") child.Healthy = false;
        if (failure == "child-poison-after") child.PoisonOnDispose = true;
        if (failure == "child-health-throws") child.ThrowHealth = true;
        var result = await h.Slot.PrepareAsync(Context(), default);
        if (result.Prepared is { } prepared)
        {
            child.Handle!.ThrowDispose = failure == "prepared-dispose"; child.ThrowDispose = failure == "child-dispose";
            await Assert.ThrowsAsync<InvalidOperationException>(() => prepared.DisposeAsync().AsTask());
        }
        else Assert.Equal(LaunchPreparationFailure.CleanupFailed, result.Failure);
        Assert.False(h.Slot.CleanupHealthy); Assert.False(h.Slot.IsBusy); Assert.Equal(1, child.Handle!.Disposals); Assert.Equal(1, child.Disposals);
        h.Slot.Configure(null); h.Slot.Configure(Candidate("B")); h.Slot.CancelActive();
        var retry = await h.Slot.PrepareAsync(Context(), default); Assert.Equal(LaunchPreparationFailure.CleanupFailed, retry.Failure); Assert.Null(retry.Prepared);
        Assert.Single(h.Created); Assert.False(h.Slot.CleanupHealthy);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Slot.DisposeAsync().AsTask());
        Assert.DoesNotContain(Secret, error.ToString()); Assert.Equal(1, h.Trust.Disposals);
    }

    [Fact]
    public async Task AChildThatBecomesPoisonedWhileReadyCannotDispatchOrBeReplaced()
    {
        await using var h = new Harness { ExpectPoison = true }; h.Slot.Configure(Candidate());
        var result = await h.Slot.PrepareAsync(Context(), default); var handle = result.Prepared!; var child = h.Children[0]; child.Healthy = false;
        Assert.False(h.Slot.CleanupHealthy); Assert.True(handle.IsExpired); using var boundary = Boundary(handle.DispatchIntent);
        Assert.Equal(ProviderLaunchStatus.Uncertain, (await handle.DispatchAsync(boundary, default)).Status);
        Assert.Equal(0, child.Handle!.Dispatches); Assert.False(h.Slot.IsBusy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handle.DisposeAsync().AsTask());
        Assert.Equal(LaunchPreparationFailure.CleanupFailed, (await h.Slot.PrepareAsync(Context(), default)).Failure);
    }

    [Fact]
    public async Task ReconfigurationDuringDispatchRetainsTheLateUnverifiedReceiptAndJoinsActualWork()
    {
        await using var h = new Harness(); h.Slot.Configure(Candidate("A"));
        var ready = await h.Slot.PrepareAsync(Context(), default); var handle = ready.Prepared!; var child = h.Children[0]; child.Handle!.DispatchGate = Gate();
        using var boundary = Boundary(handle.DispatchIntent); var work = handle.DispatchAsync(boundary, default); Task? closing = null;
        try
        {
            await child.Handle.DispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)); h.Slot.Configure(Candidate("B")); closing = handle.DisposeAsync().AsTask();
            Assert.True(child.Handle.Token.IsCancellationRequested); Assert.True(handle.IsExpired); Assert.True(h.Slot.IsBusy);
            Assert.False(work.IsCompleted); Assert.False(closing.IsCompleted); Assert.Equal(0, child.Handle.Disposals); Assert.Equal(0, child.Disposals);
            Assert.Equal(LaunchPreparationFailure.Rejected, (await h.Slot.PrepareAsync(Context(), default)).Failure); Assert.Single(h.Created);
        }
        finally { child.Handle.DispatchGate.TrySetResult(); await work.WaitAsync(TimeSpan.FromSeconds(2)); await (closing ?? handle.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(2)); }
        var result = await work; Assert.Equal(ProviderLaunchStatus.ClientStartedUnverified, result.Status); Assert.Equal(12345, result.ProcessId); Assert.Null(result.ObservedUserId);
        Assert.Equal(1, child.Handle.Disposals); Assert.Equal(1, child.Disposals); Assert.False(h.Slot.IsBusy);
        var next = await h.Slot.PrepareAsync(Context(), default); await next.Prepared!.DisposeAsync(); Assert.Equal(Candidate("B").ExecutablePath, h.Created[1].ExecutablePath);
    }

    [Fact]
    public async Task DispatchDoesNotReleaseTheSlotBeforeDelayedChildCleanupFinishes()
    {
        await using var h = new Harness(); var child = new Child { DisposeGate = Gate() }; h.Next.Enqueue(child); h.Slot.Configure(Candidate());
        var ready = await h.Slot.PrepareAsync(Context(), default); await using var handle = ready.Prepared!;
        using var boundary = Boundary(handle.DispatchIntent); var work = handle.DispatchAsync(boundary, default);
        try
        {
            await child.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)); Assert.True(h.Slot.IsBusy); Assert.False(work.IsCompleted);
            Assert.Equal(LaunchPreparationFailure.Rejected, (await h.Slot.PrepareAsync(Context(), default)).Failure); Assert.Single(h.Created);
        }
        finally { child.DisposeGate.TrySetResult(); await work.WaitAsync(TimeSpan.FromSeconds(2)); }
        Assert.False(h.Slot.IsBusy); Assert.True(h.Slot.CleanupHealthy); Assert.Equal(1, child.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongBoundaryOrCancelledDispatchCleansWithoutCallingTheChild(bool cancelled)
    {
        await using var h = new Harness(); h.Slot.Configure(Candidate()); var result = await h.Slot.PrepareAsync(Context(), default); await using var handle = result.Prepared!;
        using var boundary = Boundary(cancelled ? handle.DispatchIntent : handle.DispatchIntent with { PlaceId = 1819 }); using var stop = new CancellationTokenSource();
        if (cancelled) stop.Cancel();
        Assert.Equal(ProviderLaunchStatus.RejectedBeforeStart, (await handle.DispatchAsync(boundary, stop.Token)).Status);
        Assert.Equal(0, h.Children[0].Handle!.Dispatches); Assert.Equal(1, h.Children[0].Disposals); Assert.False(h.Slot.IsBusy);
    }

    [Fact]
    public async Task UnexpectedArrivalClaimCannotUpgradeTheConfiguredProvider()
    {
        await using var h = new Harness(); h.Slot.Configure(Candidate()); var ready = await h.Slot.PrepareAsync(Context(), default); await using var handle = ready.Prepared!;
        h.Children[0].Handle!.Reply = new(ProviderLaunchStatus.VerifiedArrival, 123, 1818, 12345, DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks);
        using var boundary = Boundary(handle.DispatchIntent); var result = await handle.DispatchAsync(boundary, default);
        Assert.Equal(ProviderLaunchStatus.Uncertain, result.Status); Assert.Equal(12345, result.ProcessId); Assert.Null(result.ObservedUserId); Assert.Null(result.ObservedPlaceId);
        Assert.Equal(CapabilityState.Unverified, h.Slot.AccountLaunch.State); Assert.False(await h.Slot.ConfirmArrivalAsync(handle.DispatchIntent, result, default));
        Assert.Equal(ProviderLaunchStatus.RejectedBeforeStart, (await handle.DispatchAsync(boundary, default)).Status); Assert.Equal(1, h.Children[0].Handle!.Dispatches);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(result) + ready + handle.ToString());
    }

    [Fact]
    public async Task OwnedTrustDisposalIsJoinedExactlyOnceAndItsFailureIsRedacted()
    {
        await using var h = new Harness { ExpectPoison = true }; h.Trust.Gate = Gate(); h.Trust.Throw = true;
        var disposing = h.Slot.DisposeAsync().AsTask();
        try { await h.Trust.Started.Task.WaitAsync(TimeSpan.FromSeconds(30)); Assert.False(disposing.IsCompleted); Assert.Equal(1, h.Trust.Disposals); }
        finally { h.Trust.Gate.TrySetResult(); }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => disposing);
        Assert.DoesNotContain(Secret, error.ToString()); Assert.False(h.Slot.CleanupHealthy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Slot.DisposeAsync().AsTask()); Assert.Equal(1, h.Trust.Disposals);
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Harness : IAsyncDisposable
    {
        public readonly TrustOwner Trust = new(); public readonly Queue<Child> Next = new();
        public readonly List<Child> Children = []; public readonly List<ClientInstallationCandidate> Created = [];
        public readonly WindowsConfiguredNativeLaunchProvider Slot; public bool ExpectPoison;
        public Harness() => Slot = new(candidate => { var child = Next.Count == 0 ? new Child() : Next.Dequeue(); Created.Add(candidate); Children.Add(child); return child; }, Trust);
        public async ValueTask DisposeAsync()
        { try { await Slot.DisposeAsync(); } catch (InvalidOperationException) when (ExpectPoison && !Slot.CleanupHealthy) { } }
    }
    private sealed class TrustOwner : IAsyncDisposable
    {
        public int Disposals; public bool Throw; public TaskCompletionSource? Gate;
        public TaskCompletionSource Started { get; } = ConfiguredNativeLaunchProviderTests.Gate();
        public async ValueTask DisposeAsync() { Disposals++; Started.TrySetResult(); if (Gate is not null) await Gate.Task; if (Throw) throw new IOException(Secret); }
    }
    private sealed class Child : IConfiguredNativeLaunchChild
    {
        public bool Healthy = true, PoisonOnDispose, ThrowDispose, ThrowHealth, ForeignContext, ExpiredHandle; public int Disposals;
        public bool CleanupHealthy => ThrowHealth ? throw new IOException(Secret) : Healthy;
        public INativeLaunchDiagnosticSource? DiagnosticSource { get; set; }
        public LaunchPreparationFailure Failure; public CancellationToken Token; public Handle? Handle;
        public TaskCompletionSource? PrepareGate, DisposeGate; public Action<CancellationToken>? BeforeReturn;
        public TaskCompletionSource PrepareStarted { get; } = Gate(); public TaskCompletionSource DisposeStarted { get; } = Gate();
        public async Task<LaunchPreparationResult> PrepareAsync(LaunchPreparationContext context, CancellationToken ct)
        { Token = ct; Handle = new(ForeignContext ? Context() : context) { Expired = ExpiredHandle }; PrepareStarted.TrySetResult(); if (PrepareGate is not null) await PrepareGate.Task; BeforeReturn?.Invoke(ct); return new(Failure, Handle); }
        public async ValueTask DisposeAsync()
        { Disposals++; DisposeStarted.TrySetResult(); if (DisposeGate is not null) await DisposeGate.Task; if (PoisonOnDispose) Healthy = false; if (ThrowDispose) throw new IOException(Secret); }
    }
    private sealed class Handle(LaunchPreparationContext context) : IPreparedAccountLaunch
    {
        private bool _closed;
        public LaunchPreparationContext Context { get; } = context;
        public LaunchIntent DispatchIntent { get; } = context.OriginalIntent with { AuthorizationGeneration = context.OriginalIntent.AuthorizationGeneration + 2 };
        public bool IsExpired => _closed || Expired;
        public bool Expired;
        public int Dispatches, Disposals; public bool ThrowDispose; public CancellationToken Token;
        public TaskCompletionSource? DispatchGate, DisposeGate;
        public TaskCompletionSource DispatchStarted { get; } = Gate(); public TaskCompletionSource DisposeStarted { get; } = Gate();
        public ProviderLaunchResult Reply = new(ProviderLaunchStatus.ClientStartedUnverified, ProcessId: 12345, ProcessStartUtcTicks: DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks);
        public Action? BeforeDispatchReturn;
        public async Task<ProviderLaunchResult> DispatchAsync(LaunchDispatchBoundary boundary, CancellationToken ct)
        { Dispatches++; Token = ct; DispatchStarted.TrySetResult(); if (DispatchGate is not null) await DispatchGate.Task; BeforeDispatchReturn?.Invoke(); return Reply; }
        public async ValueTask DisposeAsync()
        { _closed = true; Disposals++; DisposeStarted.TrySetResult(); if (DisposeGate is not null) await DisposeGate.Task; if (ThrowDispose) throw new IOException(Secret); }
    }
    private sealed class Borrowed : IRobloxNativeHandoffPreparer, IRobloxNativeHandoffCapture, IAuthorizationDispatchVault
    {
        public int Calls, Disposals;
        public Task<NativeHandoffPreparationResult> PrepareNativeHandoffAsync(LaunchIntent intent, IRobloxNativeHandoffCapture browser, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("No preparation expected."); }
        public Task<NativeBrowserCapture> CaptureAsync(NativeHandoffContext context, RobloxSessionCredential credential, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("No capture expected."); }
        public Task<AuthorizationDispatchResult> TryDispatchCurrentAsync(AuthorizationWriteSnapshot expected, Func<NativeHandoffDispatchResult> dispatch, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("No dispatch expected."); }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
