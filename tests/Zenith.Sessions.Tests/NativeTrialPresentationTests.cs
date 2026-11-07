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
        var context = await child.Entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
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
        Assert.Contains(buttons, button => (string?)button.Attribute("Command") == "{Binding QueueSelected}");
        Assert.Contains(buttons, button => (string?)button.Attribute("Command") == "{Binding DataContext.QueueTargetedAccounts, RelativeSource={RelativeSource AncestorType=Window}}");
        Assert.Contains(buttons, button => (string?)button.Attribute("Command") == "{Binding CancelNativeTrial}");
        // The redesign folds the trial into an "advanced" expander on Find a game; its header still names it as a trial.
        Assert.Contains(document.Descendants(ui + "Expander"), expander => ((string?)expander.Attribute("Header"))?.Contains("Native account trial", StringComparison.Ordinal) == true);
    }
}
