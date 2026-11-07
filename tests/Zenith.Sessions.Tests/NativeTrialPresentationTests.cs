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
