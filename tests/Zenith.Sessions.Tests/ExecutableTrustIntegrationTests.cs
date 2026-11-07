using System.IO;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    [Fact]
    public Task PublisherCheckIsExplicitAndNeverSelectsAnAccountLaunchProvider()=>OnUi(async()=>
    {
        var trust=new TrustProbe();
        await using var shell=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new Monitor(),installationDiscovery:new InstallationProbe(),executableTrust:trust);
        await shell.InitializeAsync();Assert.False(shell.CanVerifyInstallationPublisher);Assert.Equal(0,trust.Calls);
        shell.RefreshInstallations.Execute(null);await shell.RefreshInstallations.Execution;
        shell.SelectedInstallation=shell.InstallationCandidates[1];Assert.False(shell.CanVerifyInstallationPublisher);
        shell.SelectedInstallation=shell.InstallationCandidates[0];Assert.True(shell.CanVerifyInstallationPublisher);
        var saved=shell.SaveStatus;shell.VerifyInstallationPublisher.Execute(null);await shell.VerifyInstallationPublisher.Execution;
        Assert.Equal(1,trust.Calls);Assert.Contains("verified",shell.PublisherVerificationStatus);Assert.Contains("SHA-256",shell.PublisherVerificationStatus);
        Assert.Empty(shell.Launches);Assert.Equal(saved,shell.SaveStatus);
        Assert.Equal(CapabilityState.Unavailable,shell.SelectedInstallation.Candidate.NamedAccountLaunch);
    });

    [Fact]
    public Task PublisherCheckIsUnavailableForTheRobloxLauncherRow()=>OnUi(async()=>
    {
        // Discovery lists RobloxPlayerLauncher.exe as a Roblox row, but the verifier never checks a launcher's
        // publisher, so the button must not offer a check that can only answer "not evaluated".
        var trust=new TrustProbe();
        await using var shell=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new Monitor(),
            installationDiscovery:new InstallationProbe{ExtraPath="C:\\Synthetic\\RobloxPlayerLauncher.exe"},executableTrust:trust);
        await shell.InitializeAsync();shell.RefreshInstallations.Execute(null);await shell.RefreshInstallations.Execution;
        var launcher=shell.InstallationCandidates.Single(row=>row.ExecutablePath.EndsWith("RobloxPlayerLauncher.exe",StringComparison.OrdinalIgnoreCase));
        var player=shell.InstallationCandidates.Single(row=>row.ExecutablePath.EndsWith("RobloxPlayerBeta.exe",StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ClientInstallationProduct.Roblox,launcher.Candidate.Product);
        shell.SelectedInstallation=launcher;
        Assert.False(shell.CanVerifyInstallationPublisher);
        Assert.Contains("launcher",shell.PublisherVerificationStatus,StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RobloxPlayerBeta.exe",shell.PublisherVerificationStatus,StringComparison.Ordinal);
        shell.VerifyInstallationPublisher.Execute(null);await shell.VerifyInstallationPublisher.Execution;
        Assert.Equal(0,trust.Calls);Assert.DoesNotContain(launcher.ExecutablePath,shell.NativeTrialInstallation);
        shell.SelectedInstallation=player;
        Assert.True(shell.CanVerifyInstallationPublisher);
        Assert.Equal("Select a native Roblox installation, then explicitly verify its publisher.",shell.PublisherVerificationStatus);
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task ChangingSelectionOrLockingDiscardsLatePublisherSuccess(bool useLock)=>OnUi(async()=>
    {
        var trust=new TrustProbe{Gate=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        await using var shell=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new Monitor(),installationDiscovery:new InstallationProbe(),executableTrust:trust);
        await shell.InitializeAsync();shell.RefreshInstallations.Execute(null);await shell.RefreshInstallations.Execution;
        shell.SelectedInstallation=shell.InstallationCandidates[0];shell.VerifyInstallationPublisher.Execute(null);
        try
        {
            Assert.True(shell.IsPublisherVerificationBusy);Assert.False(shell.CanRefreshInstallations);
            shell.VerifyInstallationPublisher.Execute(null);Assert.Equal(1,trust.Calls);
            if(useLock){shell.LockDashboard();shell.Unlock.Execute(null);shell.Panic.Execute(null);}
            else shell.SelectedInstallation=shell.InstallationCandidates[1];
            Assert.True(trust.Token.IsCancellationRequested);
        }
        finally{trust.Gate.TrySetResult(TrustProbe.Result(trust.Candidate!));}
        await shell.VerifyInstallationPublisher.Execution;
        Assert.DoesNotContain("SHA-256",shell.PublisherVerificationStatus);Assert.Empty(shell.Launches);
        if(useLock)Assert.Contains("Panic stop",shell.Notice);
    });

    [Theory]
    [InlineData("button")] [InlineData("select")] [InlineData("panic")]
    public Task CancelledPublisherCheckSettlesStatusWhenTheOwnedCheckEnds(string cancelPath)=>OnUi(async()=>
    {
        var trust=new TrustProbe{Gate=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        await using var shell=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new Monitor(),installationDiscovery:new InstallationProbe(),executableTrust:trust);
        await shell.InitializeAsync();shell.RefreshInstallations.Execute(null);await shell.RefreshInstallations.Execution;
        shell.SelectedInstallation=shell.InstallationCandidates[0];shell.VerifyInstallationPublisher.Execute(null);
        try
        {
            if(cancelPath=="button")shell.CancelPublisherVerification.Execute(null);
            else if(cancelPath=="select")shell.SelectedInstallation=shell.InstallationCandidates[1];
            else shell.Panic.Execute(null);
            Assert.True(trust.Token.IsCancellationRequested);Assert.True(shell.IsPublisherVerificationBusy);
            Assert.Contains("Waiting",shell.PublisherVerificationStatus);
        }
        finally{trust.Gate.TrySetResult(TrustProbe.Result(trust.Candidate!));}
        await shell.VerifyInstallationPublisher.Execution;
        Assert.False(shell.IsPublisherVerificationBusy);Assert.Empty(shell.Launches);
        Assert.DoesNotContain("Waiting",shell.PublisherVerificationStatus);Assert.DoesNotContain("SHA-256",shell.PublisherVerificationStatus);
        if(cancelPath=="button")
        {
            Assert.Equal("Verification cancelled. Trust remains unknown; verify again to check this file.",shell.PublisherVerificationStatus);
            Assert.True(shell.CanVerifyInstallationPublisher);
        }
        else Assert.Equal("Select a native Roblox installation, then explicitly verify its publisher.",shell.PublisherVerificationStatus);
        if(cancelPath=="panic")Assert.Contains("Panic stop",shell.Notice);
    });

    [Fact]
    public Task ShutdownCancelsAndJoinsTheActualTrustOwnerBeforeDisposing()=>OnUi(async()=>
    {
        var trust=new TrustProbe{Gate=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        var shell=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new Monitor(),installationDiscovery:new InstallationProbe(),executableTrust:trust);
        await shell.InitializeAsync();shell.RefreshInstallations.Execute(null);await shell.RefreshInstallations.Execution;
        shell.SelectedInstallation=shell.InstallationCandidates[0];shell.VerifyInstallationPublisher.Execute(null);
        var disposing=shell.DisposeAsync().AsTask();
        try{Assert.True(trust.Token.IsCancellationRequested);Assert.False(disposing.IsCompleted);Assert.False(trust.Disposed);}
        finally{trust.Gate.TrySetResult(TrustProbe.Result(trust.Candidate!));}
        await disposing;Assert.True(trust.Disposed);Assert.DoesNotContain("SHA-256",shell.PublisherVerificationStatus);
    });

    [Fact]
    public Task MalformedTrustResultNeverClaimsVerifiedPublisher()=>OnUi(async()=>
    {
        var trust=new TrustProbe{Malformed=true};
        await using var shell=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new Monitor(),installationDiscovery:new InstallationProbe(),executableTrust:trust);
        await shell.InitializeAsync();shell.RefreshInstallations.Execute(null);await shell.RefreshInstallations.Execution;
        shell.SelectedInstallation=shell.InstallationCandidates[0];shell.VerifyInstallationPublisher.Execute(null);await shell.VerifyInstallationPublisher.Execution;
        Assert.Contains("failed",shell.PublisherVerificationStatus);Assert.DoesNotContain("SHA-256",shell.PublisherVerificationStatus);
    });

    [Fact]
    public Task LatePublisherFailureCannotReplacePanicOrCancelledStatus()=>OnUi(async()=>
    {
        var trust=new TrustProbe{Gate=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        await using var shell=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new Monitor(),installationDiscovery:new InstallationProbe(),executableTrust:trust);
        await shell.InitializeAsync();shell.RefreshInstallations.Execute(null);await shell.RefreshInstallations.Execution;
        shell.SelectedInstallation=shell.InstallationCandidates[0];shell.VerifyInstallationPublisher.Execute(null);
        try{shell.Panic.Execute(null);Assert.True(trust.Token.IsCancellationRequested);}
        finally{trust.Gate.TrySetException(new IOException("Synthetic provider failure, not user-facing text"));}
        await shell.VerifyInstallationPublisher.Execution;
        Assert.Contains("Panic stop",shell.Notice);Assert.DoesNotContain("check failed",shell.PublisherVerificationStatus);
        Assert.DoesNotContain("Synthetic",shell.PublisherVerificationStatus);
    });

    private sealed class TrustProbe:IClientExecutableTrustVerifier
    {
        public int Calls;public bool Malformed,Disposed;public CancellationToken Token;public ClientInstallationCandidate? Candidate;
        public TaskCompletionSource<ClientExecutableTrustResult>? Gate;
        public Task<ClientExecutableTrustResult> VerifyAsync(ClientInstallationCandidate candidate,CancellationToken ct)
        {
            Calls++;Token=ct;Candidate=candidate;var result=Result(candidate);
            return Gate?.Task??Task.FromResult(Malformed?result with{Sha256="not a file hash"}:result);
        }
        public static ClientExecutableTrustResult Result(ClientInstallationCandidate candidate)
        {
            var at=DateTimeOffset.UtcNow;
            return new(candidate.Product,candidate.ExecutablePath,ClientExecutableTrustStatus.VerifiedRobloxPublisher,ClientExecutableTrustReason.Verified,at,
                new(candidate.ExecutablePath,100,at.AddDays(-1),at.AddDays(-1),1,new string('A',32)),new string('B',64),
                new("Synthetic publisher","Roblox Corporation",new string('C',64),at.AddDays(-1),at.AddDays(1)));
        }
        public ValueTask DisposeAsync(){Disposed=true;return ValueTask.CompletedTask;}
    }
}
