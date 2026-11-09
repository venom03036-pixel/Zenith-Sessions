using System.IO;
using System.Reflection;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task AccountLaunchOffersGameSelectionOrInstallationTrialWithoutCreatingBlockedWork(bool preferredGame) => OnUi(async()=>
    {
        await using var shell=await NativeTrialShellAsync(destination:false);
        if(!preferredGame)await shell.SaveAccountAsync(shell.SelectedAccount!.Profile with {PreferredPlaceId=null});
        var chosen=shell.SelectedAccount!;
        var unrelated=new AccountRow(AccountProfile.Create("Unrelated checked account")){IsChecked=true};shell.Accounts.Add(unrelated);
        shell.Navigate.Execute("Accounts");shell.AccountPower.Execute(chosen);await shell.AccountPower.Execution;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Empty(shell.Launches);Assert.True(shell.IsJoinGame);Assert.Same(chosen,shell.SelectedAccount);
        Assert.True(chosen.IsChecked);Assert.False(unrelated.IsChecked);
        Assert.Equal(preferredGame,shell.IsJoinReadyStep);Assert.Equal(!preferredGame,shell.IsJoinFindStep);
        if(preferredGame)Assert.Equal(42,shell.ServerBrowser.ReadyDestination!.PlaceId);
        else Assert.Null(shell.ServerBrowser.ReadyDestination);
        Assert.False(shell.IsNativeLaunchVerified);
    });

    // Only injected observations/sign-in callbacks and owned temporary metadata/DPAPI are used.
    // The selected executable path never exists. No trial is executed while all guards pass;
    // no process, WebView, real browser/account or native child is started by these fixtures.
    private InstallationCandidateRow NativeTrialMetadataOnlyInstallation() => new(new(ClientInstallationProduct.Roblox,
        Path.Combine(_root, "never-created", "RobloxPlayerBeta.exe"), "Synthetic metadata only", "0.0.0",
        ClientInstallationFileState.PresentUnverified, [new(ClientInstallationHintSource.RobloxPlayerProtocol, ClientInstallationProduct.Roblox)]));

    private async Task<ShellViewModel> NativeTrialShellAsync(bool sample = false, bool installation = true, bool destination = true, bool link = true)
    {
        var shell = new ShellViewModel(_root, sample, Dispatcher.CurrentDispatcher, new Monitor(),
            signIn: new HandoffSignIn(), sessionClient: new HandoffIdentity());
        try
        {
            await shell.InitializeAsync();
            if (shell.Accounts.Count == 0) await shell.SaveAccountAsync(AccountProfile.Create("Synthetic trial profile") with { PreferredPlaceId = 42 });
            shell.SelectedAccount = shell.Accounts[0];
            if (link) { shell.LinkAccount.Execute(null); await shell.LinkAccount.Execution; Assert.Equal(456, shell.SelectedAccount!.KnownUserId); }
            if (installation) shell.SelectedInstallation = NativeTrialMetadataOnlyInstallation();
            if (destination) shell.ServerBrowser.UsePreferredPlace(42);
            Assert.True(shell.IsReady); Assert.True(shell.QueueReady);
            return shell;
        }
        catch { await shell.DisposeAsync(); throw; }
    }
    private async Task AssertBlockedTrialCommandAsync(ShellViewModel shell)
    {
        Assert.False(shell.CanRunNativeTrial);
        string status = shell.NativeTrialStatus; int before = shell.Launches.Count;
        shell.StartNativeTrial.Execute(null); await shell.StartNativeTrial.Execution;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(before, shell.Launches.Count); Assert.Equal(status, shell.NativeTrialStatus);
        Assert.False(Directory.Exists(Path.Combine(_root, "signin-browser-data")));
        Assert.False(File.Exists(NativeTrialMetadataOnlyInstallation().ExecutablePath));
    }

    [Fact]
    public Task NativeTrialCannotEnqueueBeforeInitializationEvenWhenCommandIsCalledDirectly() => OnUi(async () =>
    {
        await using var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(),
            signIn: new HandoffSignIn(), sessionClient: new HandoffIdentity());
        Assert.False(shell.IsReady); await AssertBlockedTrialCommandAsync(shell); Assert.Empty(shell.Launches);
    });

    [Fact]
    public Task SampleLibraryCannotEnqueueNativeTrialWithKnownIdentityDestinationAndInstallation() => OnUi(async () =>
    {
        await using var shell = await NativeTrialShellAsync(sample: true);
        Assert.NotNull(shell.SelectedInstallation); Assert.NotNull(shell.ServerBrowser.Destination);
        Assert.True(shell.SelectedAccount!.AuthorizationGeneration > 0); Assert.True(shell.IsSample);
        await AssertBlockedTrialCommandAsync(shell); Assert.Empty(shell.Launches);
    });

    [Theory]
    [InlineData("installation")]
    [InlineData("destination")]
    [InlineData("identity")]
    [InlineData("selection")]
    public Task MissingNativeTrialPrerequisiteCannotBeBypassedByDirectCommandExecution(string missing) => OnUi(async () =>
    {
        await using var shell = await NativeTrialShellAsync(installation: missing != "installation", destination: missing != "destination", link: missing != "identity");
        if (missing == "selection") shell.SelectedAccount = null;
        if (missing == "installation") { Assert.Null(shell.SelectedInstallation); Assert.NotNull(shell.ServerBrowser.Destination); Assert.Equal(456, shell.SelectedAccount!.KnownUserId); }
        if (missing == "destination") { Assert.NotNull(shell.SelectedInstallation); Assert.Null(shell.ServerBrowser.Destination); Assert.Equal(456, shell.SelectedAccount!.KnownUserId); }
        if (missing == "identity") { Assert.NotNull(shell.SelectedInstallation); Assert.NotNull(shell.ServerBrowser.Destination); Assert.Null(shell.SelectedAccount!.KnownUserId); }
        await AssertBlockedTrialCommandAsync(shell); Assert.Empty(shell.Launches);
    });

    [Fact]
    public Task LockStopsThePreviouslyEligibleTrialAndDirectExecutionCannotRestoreIt() => OnUi(async () =>
    {
        await using var shell = await NativeTrialShellAsync(); Assert.True(shell.CanRunNativeTrial);
        shell.LockDashboard(); Assert.True(shell.IsLocked); string notice = shell.Notice;
        await AssertBlockedTrialCommandAsync(shell); Assert.Equal(notice, shell.Notice); Assert.Empty(shell.Launches);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task PanicOrLockKeepsTheTrialStopReasonInTheTrialPanel(bool lockDashboard) => OnUi(async () =>
    {
        await using var shell = await NativeTrialShellAsync(); Assert.True(shell.CanRunNativeTrial);
        if (lockDashboard) shell.LockDashboard(); else shell.Panic.Execute(null);
        Assert.False(shell.CanRunNativeTrial); Assert.Null(shell.SelectedInstallation);
        Assert.Contains("Panic or Lock", shell.NativeTrialStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("not supported", shell.NativeTrialStatus, StringComparison.OrdinalIgnoreCase);
        await AssertBlockedTrialCommandAsync(shell); Assert.Empty(shell.Launches);
        // Unlock does not restart the trial, so the stop guidance stays until the user acts.
        if (lockDashboard) { shell.Unlock.Execute(null); await shell.Unlock.Execution; Assert.False(shell.IsLocked); }
        Assert.Contains("Panic or Lock", shell.NativeTrialStatus, StringComparison.Ordinal);
        // No check was running, so the cleared Settings list must not claim a check was stopped.
        Assert.Empty(shell.InstallationCandidates); Assert.Equal("Not checked", shell.InstallationObserved);
        Assert.DoesNotContain("check stopped", shell.InstallationStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Refresh", shell.InstallationStatus, StringComparison.Ordinal);
    });

    [Fact]
    public Task ClearingTheInstallationSelectionShowsANeutralTrialStatus() => OnUi(async () =>
    {
        await using var shell = await NativeTrialShellAsync(); Assert.True(shell.CanRunNativeTrial);
        shell.CancelInstallationDiscovery.Execute(null);
        Assert.Null(shell.SelectedInstallation); Assert.False(shell.CanRunNativeTrial);
        Assert.DoesNotContain("not supported", shell.NativeTrialStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No native installation is selected", shell.NativeTrialStatus, StringComparison.Ordinal);
        await AssertBlockedTrialCommandAsync(shell); Assert.Empty(shell.Launches);
    });

    [Fact]
    public Task DiscoveryValidLauncherSelectionRemainsVisibleWithoutThrowingOrEnablingANativeTrial() => OnUi(async () =>
    {
        await using var shell = await NativeTrialShellAsync(installation: false);
        var launcher = new InstallationCandidateRow(new(ClientInstallationProduct.Roblox,
            Path.Combine(_root, "never-created", "RobloxPlayerLauncher.exe"), "Synthetic discovered launcher", "0.0.0",
            ClientInstallationFileState.PresentUnverified, [new(ClientInstallationHintSource.RobloxPlayerProtocol, ClientInstallationProduct.Roblox)]));
        shell.InstallationCandidates.Add(launcher);
        Exception? error = Record.Exception(() => { shell.SelectedInstallation = launcher; });
        Assert.Null(error); Assert.Same(launcher, shell.SelectedInstallation); Assert.Contains(launcher, shell.InstallationCandidates);
        Assert.False(shell.CanRunNativeTrial); Assert.Contains("not supported", shell.NativeTrialStatus, StringComparison.OrdinalIgnoreCase);
        await AssertBlockedTrialCommandAsync(shell); Assert.Empty(shell.Launches);
    });

    [Fact]
    public Task BootstrapperManagedPlayerSelectionExplainsOwnershipAndIsNotShownAsTheTrialInstallation() => OnUi(async () =>
    {
        await using var shell = await NativeTrialShellAsync(installation: false);
        var row = new InstallationCandidateRow(new(ClientInstallationProduct.Roblox,
            Path.Combine(_root, "never-created", "Bloxstrap", "Versions", "version-x", "RobloxPlayerBeta.exe"), "Synthetic", "0.0.0",
            ClientInstallationFileState.PresentUnverified, [new(ClientInstallationHintSource.VersionDirectory, ClientInstallationProduct.Bloxstrap)]));
        shell.InstallationCandidates.Add(row);
        shell.SelectedInstallation = row;
        Assert.Equal(ClientInstallationProduct.Bloxstrap, row.ManagedBy);
        Assert.Contains("Bloxstrap", row.Product, StringComparison.Ordinal);
        Assert.Contains("Bloxstrap version folder", row.Evidence, StringComparison.Ordinal);
        Assert.NotEqual(row.ExecutablePath, shell.NativeTrialInstallation);
        Assert.Contains("see the trial status", shell.NativeTrialInstallation, StringComparison.Ordinal);
        Assert.Contains("managed by Bloxstrap", shell.NativeTrialStatus, StringComparison.Ordinal);
        Assert.Contains("not supported", shell.NativeTrialStatus, StringComparison.OrdinalIgnoreCase);
        Assert.False(shell.CanRunNativeTrial);
        await AssertBlockedTrialCommandAsync(shell); Assert.Empty(shell.Launches);

        // A native row owned by Roblox keeps its plain label and becomes the trial installation.
        var native = NativeTrialMetadataOnlyInstallation();
        Assert.Null(native.ManagedBy); Assert.Equal("Roblox", native.Product); Assert.Equal("roblox-player registration", native.Evidence);
        shell.SelectedInstallation = native;
        Assert.Equal(native.ExecutablePath, shell.NativeTrialInstallation);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ConfiguringATrialDoesNotGiveOrdinaryOrCheckedAccountCommandsTrialConsent(bool checkedRoute) => OnUi(async () =>
    {
        await using var shell = await NativeTrialShellAsync(); Assert.True(shell.CanRunNativeTrial);
        var selected = shell.SelectedAccount!;
        if (checkedRoute)
        {
            selected.IsChecked = true; shell.QueueTargetedAccounts.Execute(null); await shell.QueueTargetedAccounts.Execution;
        }
        else { shell.QueueSelected.Execute(null); await shell.QueueSelected.Execution; }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var operation = Assert.Single(shell.Launches).Operation;
        Assert.Equal(selected.Profile.Id, operation.Intent.ProfileId); Assert.Equal(LaunchState.Blocked, operation.State);
        Assert.Equal(LaunchProblem.ProviderUnavailable, operation.Problem); Assert.Null(operation.ProcessReceipt);
        Assert.False(Directory.Exists(Path.Combine(_root, "signin-browser-data")));
        Assert.All(shell.Capabilities.Where(item => item.Name.Contains("launch", StringComparison.OrdinalIgnoreCase)), item => Assert.NotEqual(CapabilityState.Supported, item.State));
        Assert.Equal(CapabilityState.Unavailable, shell.SelectedInstallation!.Candidate.NamedAccountLaunch);
    });

    // Removal and disconnect stop the whole queue by design and wait before changing saved state.
    // The trial card and notice must say so instead of still asking the user to choose Play.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RemovingAnotherAccountDuringANativeTrialFencesTheTrialStatusAndReportsTheQueuePause(bool disconnect) => OnUi(async () =>
    {
        var child = new NativeEvidenceChild { PreparationGate = EvidenceGate() };
        await using var shell = await NativeEvidenceShellAsync(child, new NativeEvidenceMonitor());
        var trial = shell.SelectedAccount!; var other = shell.Accounts.Single(row => row.Profile.Id != trial.Profile.Id);
        shell.StartNativeTrial.Execute(Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview())); await shell.StartNativeTrial.Execution;
        var context = await child.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains("Trial queued for", shell.NativeTrialStatus, StringComparison.Ordinal);
        Task removing;
        try
        {
            if (disconnect) { shell.SelectedAccount = other; shell.DisconnectAccount.Execute(null); removing = shell.DisconnectAccount.Execution; }
            else removing = shell.DeleteAccountsAsync([other]);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(removing.IsCompleted); // The removal still waits for the trial's actual cleanup.
        }
        finally { child.PreparationGate!.TrySetResult(); }
        await removing.WaitAsync(TimeSpan.FromSeconds(6));
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(6)))
            while (shell.Launches.SingleOrDefault(row => row.Id == context.OriginalIntent.OperationId)?.Operation.State != LaunchState.Cancelled)
                await Task.Delay(5, stop.Token);
        Assert.Equal(0, child.Starts);
        Assert.DoesNotContain("Trial queued for", shell.NativeTrialStatus, StringComparison.Ordinal);
        Assert.StartsWith(disconnect ? "Trial stopped because a saved sign-in was removed." : "Trial stopped because a profile was removed.", shell.NativeTrialStatus, StringComparison.Ordinal);
        Assert.StartsWith(disconnect ? "Saved sign-in removed from Zenith." : "Removed 1 profile(s).", shell.Notice, StringComparison.Ordinal);
        Assert.EndsWith(" The native trial in progress was stopped and the launch queue was paused. Review the queue and choose Resume queue when ready.", shell.Notice, StringComparison.Ordinal);
        Assert.Equal("Resume queue", shell.QueuePauseLabel);
        Assert.Contains(shell.Accounts, row => row.Profile.Id == trial.Profile.Id);
        Assert.Equal(!disconnect, shell.Accounts.All(row => row.Profile.Id != other.Profile.Id));
    });

    // While a trial is still being reviewed (before it is enqueued) the provider owns nothing yet, so only
    // the running trial command shows it. Unfenced, a review step that finishes after the removal's stop
    // would enqueue the trial under the queue's new stop generation and start it anyway. Selecting another
    // row already ends the review, so Remove saved sign-in can reach this only for the selected trial account.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RemovingASavedAccountWhileATrialIsStillBeingReviewedFencesItAndNeverEnqueuesIt(bool disconnect) => OnUi(async () =>
    {
        var repository = new NativeReviewRepository(_root); var child = new NativeEvidenceChild();
        await using var shell = await NativeEvidenceShellAsync(child, new NativeEvidenceMonitor(), repository);
        var trial = shell.SelectedAccount!; var other = shell.Accounts.Single(row => row.Profile.Id != trial.Profile.Id);
        var review = Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview());
        repository.BlockFind = true;
        shell.StartNativeTrial.Execute(review);
        Task removing;
        try
        {
            await repository.FindEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(shell.StartNativeTrial.Execution.IsCompleted);
            Assert.True(shell.CanRunNativeTrial); // The provider is not busy yet.
            if (disconnect) { shell.DisconnectAccount.Execute(null); removing = shell.DisconnectAccount.Execution; }
            else removing = shell.DeleteAccountsAsync([other]);
        }
        finally { repository.FindRelease.TrySetResult(); } // The review step may now finish after the removal's stop.
        await removing.WaitAsync(TimeSpan.FromSeconds(6));
        await shell.StartNativeTrial.Execution.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Empty(shell.Launches); Assert.Equal(0, child.Preparations); Assert.Equal(0, child.Starts);
        Assert.StartsWith(disconnect ? "Trial stopped because a saved sign-in was removed." : "Trial stopped because a profile was removed.", shell.NativeTrialStatus, StringComparison.Ordinal);
        Assert.StartsWith(disconnect ? "Saved sign-in removed from Zenith." : "Removed 1 profile(s).", shell.Notice, StringComparison.Ordinal);
        Assert.EndsWith(" The native trial in progress was stopped and the launch queue was paused. Review the queue and choose Resume queue when ready.", shell.Notice, StringComparison.Ordinal);
        // Resuming runs the queue under its new stop generation; the fenced trial left no request there.
        Assert.Equal("Resume queue", shell.QueuePauseLabel);
        shell.ToggleQueuePause.Execute(null); await shell.ToggleQueuePause.Execution;
        Assert.Equal("Pause queue", shell.QueuePauseLabel);
        Assert.Empty(shell.Launches); Assert.Equal(0, child.Preparations);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RemovingAProfileReportsAQueuePauseOnlyWhenTheQueueWasRunning(bool running) => OnUi(async () =>
    {
        await using var shell = await NativeEvidenceShellAsync(new NativeEvidenceChild(), new NativeEvidenceMonitor());
        string trialStatus = shell.NativeTrialStatus;
        if (running) { shell.ToggleQueuePause.Execute(null); await shell.ToggleQueuePause.Execution; Assert.Equal("Pause queue", shell.QueuePauseLabel); }
        else Assert.Equal("Resume queue", shell.QueuePauseLabel);
        var other = shell.Accounts.Single(row => row.Profile.Id != shell.SelectedAccount!.Profile.Id);
        await shell.DeleteAccountsAsync([other]);
        Assert.Equal(trialStatus, shell.NativeTrialStatus);
        Assert.StartsWith("Removed 1 profile(s). Roblox clients were left open.", shell.Notice, StringComparison.Ordinal);
        if (running) Assert.EndsWith(". The launch queue was paused. Review the queue and choose Resume queue when ready.", shell.Notice, StringComparison.Ordinal);
        else Assert.DoesNotContain("queue", shell.Notice, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public void ProductionNativeTrialUsesTheExplicitConfirmationHandlerAndDoesNotRebindOrdinaryCommands()
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProductionMainWindow.xaml"); Assert.NotNull(source);
        var document = XDocument.Load(source!); XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var buttons = document.Descendants(ui + "Button").ToArray();
        var trial = Assert.Single(buttons.Where(button => (string?)button.Attribute("Content") == "Review selected-account trial"));
        Assert.Equal("StartNativeTrialClick", (string?)trial.Attribute("Click")); Assert.Null(trial.Attribute("Command"));
        Assert.Equal("{Binding CanRunNativeTrial}", (string?)trial.Attribute("IsEnabled"));
        Assert.NotNull(typeof(MainWindow).GetMethod("StartNativeTrialClick", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.DoesNotContain(document.Descendants().Attributes("Command"), attribute => attribute.Value.Contains("StartNativeTrial", StringComparison.Ordinal));
        // The inspector now uses the row's state-aware ordinary action (game/setup routing, cancellation,
        // or queueing), never the trial command. Keep the selected-account parameter explicit.
        var ordinary=Assert.Single(buttons.Where(button => (string?)button.Attribute("Command") == "{Binding AccountPower}"));
        Assert.Equal("{Binding SelectedAccount}",(string?)ordinary.Attribute("CommandParameter"));
        Assert.Equal("{Binding SelectedAccount.PowerLabel}",(string?)ordinary.Attribute("Content"));
        Assert.Contains(buttons, button => (string?)button.Attribute("Command") == "{Binding DataContext.QueueTargetedAccounts, RelativeSource={RelativeSource AncestorType=Window}}");
        Assert.Contains(buttons, button => (string?)button.Attribute("Command") == "{Binding CancelNativeTrial}");
        // The redesign folds the trial into an "advanced" expander on Find a game; its header still names it as a trial.
        Assert.Contains(document.Descendants(ui + "Expander"), expander => ((string?)expander.Attribute("Header"))?.Contains("Native account trial", StringComparison.Ordinal) == true);
    }
}
