using System.Reflection;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    // Real temporary DPAPI/repository, saved-session preparation and queue. The browser, identity
    // service, executable trust and monitoring are injected; no network, WebView or process starts.
    [Theory]
    [InlineData(RobloxHandoffProbeOutcome.MalformedHandoff)]
    [InlineData(RobloxHandoffProbeOutcome.RateLimited)]
    [InlineData(RobloxHandoffProbeOutcome.Cancelled)]
    public Task NativePreparationFailureRefreshesFencedSavedSignInWithoutChangingQueueIntent(RobloxHandoffProbeOutcome outcome) => OnUi(async () =>
    {
        var identity = new MetadataIdentity(); var child = new MetadataFailureChild(outcome);
        await using var shell = await MetadataFailureShellAsync(child, identity);
        var original = shell.SelectedAccount!;
        Assert.False(original.NeedsSignIn);
        var review = Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview());
        shell.StartNativeTrial.Execute(review); await shell.StartNativeTrial.Execution;
        var operation = await MetadataFailedOperationAsync(shell);
        await DrainMetadataRefreshAsync(shell);

        Assert.Equal(original.AuthorizationGeneration, operation.Intent.AuthorizationGeneration);
        Assert.Null(operation.ProcessReceipt);
        Assert.Equal(original.Profile.Id, shell.SelectedAccount!.Profile.Id);
        Assert.True(shell.SelectedAccount.NeedsSignIn);
        Assert.Equal("Sign in required", shell.SelectedAccount.Identity);
        Assert.True(shell.SelectedAccount.AuthorizationGeneration > original.AuthorizationGeneration);
        Assert.False(shell.CanRunNativeTrial);
        Assert.Null(shell.CaptureNativeTrialReview());
        Assert.Equal(1, identity.Calls); // Initial synthetic sign-in only; refresh never validates again.
        Assert.Equal(1, child.Captures);
        Assert.Equal(outcome == RobloxHandoffProbeOutcome.Cancelled ? NativeHandoffPreparationStatus.Cancelled
            : NativeHandoffPreparationStatus.CaptureRejected, child.Result?.Status);
        Assert.Single(shell.Launches);
        Assert.False(System.IO.Directory.Exists(System.IO.Path.Combine(_root, "signin-browser-data")));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task NativeTrialReviewIsUnavailableForAKnownUnhealthyOrExpiredSavedSignIn(bool expired) => OnUi(async () =>
    {
        await using var shell = await NativeTrialShellAsync();
        using var vault = new ProtectedCredentialVault(_root);
        var metadata = Assert.IsType<AuthorizationMetadata>(await vault.GetSummaryAsync(shell.SelectedAccount!.Profile.Id, default));
        shell.SelectedAccount.SetCachedAuthorization(metadata with
        {
            Health = expired ? AuthorizationHealth.Linked : AuthorizationHealth.SignInRequired,
            ExpiresUtc = expired ? DateTimeOffset.UtcNow.AddSeconds(-1) : metadata.ExpiresUtc
        });
        Assert.True(shell.SelectedAccount.NeedsSignIn);
        Assert.True(shell.SelectedAccount.KnownUserId > 0);
        Assert.True(shell.SelectedAccount.AuthorizationGeneration > 0);
        Assert.False(shell.CanRunNativeTrial);
        Assert.Null(shell.CaptureNativeTrialReview());
        shell.StartNativeTrial.Execute(null); await shell.StartNativeTrial.Execution;
        Assert.Empty(shell.Launches);
    });

    [Fact]
    public Task NativePreparationRefusedBeforeGrantReservationKeepsSavedSignInUsableAfterLocalRefresh() => OnUi(async () =>
    {
        var identity = new MetadataIdentity();
        var child = new MetadataFailureChild(RobloxHandoffProbeOutcome.MalformedHandoff) { Refusal = RobloxHandoffProbeOutcome.StorageUnavailable };
        await using var shell = await MetadataFailureShellAsync(child, identity);
        var original = shell.SelectedAccount!;
        shell.StartNativeTrial.Execute(Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview()));
        await shell.StartNativeTrial.Execution;
        var operation = await MetadataFailedOperationAsync(shell);
        await DrainMetadataRefreshAsync(shell);
        Assert.Equal(NativeHandoffPreparationStatus.StorageUnavailable, child.Result?.Status);
        Assert.Equal(0, child.Captures);
        Assert.Equal(1, identity.Calls);
        Assert.Equal(original.AuthorizationGeneration, shell.SelectedAccount!.AuthorizationGeneration);
        Assert.False(shell.SelectedAccount.NeedsSignIn);
        Assert.True(shell.CanRunNativeTrial);
        Assert.NotNull(shell.CaptureNativeTrialReview());
        Assert.Equal(original.AuthorizationGeneration, operation.Intent.AuthorizationGeneration);
        Assert.Null(operation.ProcessReceipt);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task NativePreparationFailureRefreshRespectsPanicOrLockAndDoesNotResumeTheTrial(bool locked) => OnUi(async () =>
    {
        var identity = new MetadataIdentity(); var child = new MetadataFailureChild(RobloxHandoffProbeOutcome.MalformedHandoff) { HoldCapture = true };
        await using var shell = await MetadataFailureShellAsync(child, identity);
        var profileId = shell.SelectedAccount!.Profile.Id;
        shell.StartNativeTrial.Execute(Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview()));
        await shell.StartNativeTrial.Execution;
        await child.CaptureEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (locked) shell.LockDashboard(); else shell.Panic.Execute(null);
            Assert.Equal(locked, shell.IsLocked);
        }
        finally { child.ReleaseCapture.TrySetResult(); }
        await MetadataFailedOperationAsync(shell);
        await DrainMetadataRefreshAsync(shell);
        Assert.Equal(locked, shell.IsLocked);
        Assert.Null(shell.SelectedInstallation);
        Assert.False(shell.CanRunNativeTrial);
        Assert.Contains("Panic or Lock", shell.NativeTrialStatus, StringComparison.Ordinal);
        if (locked)
        {
            Assert.False(shell.SelectedAccount!.NeedsSignIn); // Local refresh is deferred while locked.
            shell.Unlock.Execute(null); await shell.Unlock.Execution;
            await DrainMetadataRefreshAsync(shell);
        }
        Assert.Equal(profileId, shell.SelectedAccount!.Profile.Id);
        Assert.True(shell.SelectedAccount.NeedsSignIn);
        Assert.False(shell.CanRunNativeTrial);
        Assert.Equal(1, identity.Calls);
        Assert.Single(shell.Launches);
        Assert.Null(shell.Launches[0].Operation.ProcessReceipt);
    });

    private async Task<ShellViewModel> MetadataFailureShellAsync(MetadataFailureChild child, MetadataIdentity identity)
    {
        var provider = new WindowsConfiguredNativeLaunchProvider(_ => child, new NativeEvidenceTrustOwner());
        var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new NativeEvidenceMonitor(),
            signIn: new HandoffSignIn(), sessionClient: identity, nativeProvider: provider, clientReleases: new NativeEvidenceReleases());
        try
        {
            child.Service = (RobloxSessionLinkService)MetadataField(shell, "_linkService");
            await shell.InitializeAsync();
            await shell.SaveAccountAsync(AccountProfile.Create("Synthetic metadata recovery") with { PreferredPlaceId = 42 });
            shell.SelectedAccount = Assert.Single(shell.Accounts);
            shell.LinkAccount.Execute(null); await shell.LinkAccount.Execution;
            shell.SelectedInstallation = NativeTrialMetadataOnlyInstallation();
            shell.ServerBrowser.UsePreferredPlace(42);
            shell.Refresh.Execute(null); await shell.Refresh.Execution;
            Assert.True(shell.CanRunNativeTrial);
            Assert.Equal(1, identity.Calls);
            return shell;
        }
        catch { child.ReleaseCapture.TrySetResult(); await shell.DisposeAsync(); throw; }
    }

    private static object MetadataField(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static async Task<LaunchOperation> MetadataFailedOperationAsync(ShellViewModel shell)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (shell.Launches.Count != 1 || shell.Launches[0].Operation.State is not (LaunchState.Failed or LaunchState.Cancelled))
            await Task.Delay(5, stop.Token);
        return shell.Launches[0].Operation;
    }
    private static async Task DrainMetadataRefreshAsync(ShellViewModel shell)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        // Queue notifications start the tracked refresh. A cancelled provider may still be joining
        // cleanup, so exercise the same periodic presentation boundary after it releases ownership.
        var provider = (WindowsConfiguredNativeLaunchProvider)MetadataField(shell, "_nativeLaunch");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (provider.IsBusy) await Task.Delay(5, stop.Token);
        typeof(ShellViewModel).GetMethod("RefreshNativeTrialFeatures", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, null);
        await ((Task)MetadataField(shell, "_nativeMetadataRefresh")).WaitAsync(stop.Token);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private sealed class MetadataIdentity : IRobloxSessionClient
    {
        public int Calls;
        public Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref Calls);
            return Task.FromResult(new RobloxSessionValidation(new(456, "metadata_player", RobloxSessionIdentity.Issuer, "456")));
        }
    }
    private sealed class MetadataFailureChild(RobloxHandoffProbeOutcome outcome) : IConfiguredNativeLaunchChild, IRobloxNativeHandoffCapture
    {
        public RobloxSessionLinkService Service = null!;
        public NativeHandoffPreparationResult? Result;
        public RobloxHandoffProbeOutcome? Refusal { get; init; }
        public int Captures;
        public bool HoldCapture;
        public TaskCompletionSource CaptureEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCapture = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CleanupHealthy => true;
        public async Task<LaunchPreparationResult> PrepareAsync(LaunchPreparationContext context, CancellationToken ct)
        {
            Result = await Service.PrepareNativeHandoffAsync(context.OriginalIntent, this, ct);
            Assert.Null(Result.Preparation);
            return new(LaunchPreparationFailure.Rejected);
        }
        public async Task<NativeBrowserCapture> CaptureAsync(NativeHandoffContext context, RobloxSessionCredential credential, CancellationToken ct)
        {
            Interlocked.Increment(ref Captures); CaptureEntered.TrySetResult();
            if (HoldCapture) await ReleaseCapture.Task; // Cleanup ownership must outlive cancellation.
            return new(outcome, shapeStatus: NativeHandoffCaptureStatus.InvalidShape);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
