using System.IO;
using System.Reflection;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("foreign")]
    [InlineData("clone")]
    public Task NativeTrialReviewRejectsUnownedParametersAndConsumesThePriorReview(string parameterKind)=>OnUi(async ()=>
    {
        var child=new NativeEvidenceChild();
        await using var shell=await NativeEvidenceShellAsync(child,new NativeEvidenceMonitor());
        var review=Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview());
        object? parameter=parameterKind switch
        {
            "null"=>null,
            "foreign"=>new object(),
            _=>typeof(object).GetMethod("MemberwiseClone",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(review,null)
        };
        shell.StartNativeTrial.Execute(parameter);await shell.StartNativeTrial.Execution;
        Assert.Empty(shell.Launches);Assert.Equal(0,child.Preparations);
        shell.StartNativeTrial.Execute(review);await shell.StartNativeTrial.Execution;
        Assert.Empty(shell.Launches);Assert.Equal(0,child.Preparations);
        Assert.Contains("Review",shell.NativeTrialStatus,StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public Task NativeTrialReviewShowsAndStartsOneExactJobAndCannotBeReused()=>OnUi(async ()=>
    {
        var child=new NativeEvidenceChild();
        await using var shell=await NativeEvidenceShellAsync(child,new NativeEvidenceMonitor());
        shell.ServerBrowser.LinkInput="https://www.roblox.com/games/start?placeId=42&gameInstanceId="+EvidenceJob;
        shell.ServerBrowser.Resolve.Execute(null);
        var review=Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview());
        Assert.Contains("expected player 456",review.SelectionSummary,StringComparison.Ordinal);
        Assert.Contains("place 42",review.SelectionSummary,StringComparison.Ordinal);
        Assert.Contains(EvidenceJob,review.SelectionSummary,StringComparison.Ordinal);
        Assert.Equal(shell.SelectedInstallation!.ExecutablePath,review.InstallationSummary);
        shell.StartNativeTrial.Execute(review);await shell.StartNativeTrial.Execution;
        var evidence=await NativeEvidenceStartedAsync(shell);
        Assert.Equal(42,evidence.Operation.Intent.PlaceId);Assert.Equal(EvidenceJob,evidence.Operation.Intent.RequestedJobId);
        Assert.Equal(456,evidence.Operation.Intent.UserId);
        shell.StartNativeTrial.Execute(review);await shell.StartNativeTrial.Execution;
        Assert.Single(shell.Launches);Assert.Equal(1,child.Preparations);Assert.Equal(1,child.Starts);
        Assert.False(evidence.View!.ArrivalVerified);Assert.False(evidence.View.CanAuthorizeAccountAction);
    });

    [Theory]
    [InlineData("account")]
    [InlineData("account-aba")]
    [InlineData("place")]
    [InlineData("server")]
    [InlineData("destination-aba")]
    [InlineData("installation")]
    [InlineData("profile")]
    [InlineData("grant")]
    [InlineData("cached-grant")]
    [InlineData("stop")]
    [InlineData("lock")]
    [InlineData("draft")]
    public Task NativeTrialReviewRefusesStateChangedWhileConfirmationIsOpen(string change)=>OnUi(async ()=>
    {
        var child=new NativeEvidenceChild();
        await using var shell=await NativeEvidenceShellAsync(child,new NativeEvidenceMonitor());
        var selected=shell.SelectedAccount!;
        var review=Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview());
        string shown=review.SelectionSummary,installation=review.InstallationSummary;
        switch(change)
        {
            case "account":case "account-aba":
                shell.SelectedAccount=shell.Accounts.Single(row=>row.Profile.Id!=selected.Profile.Id);
                if(change=="account-aba")shell.SelectedAccount=selected;
                break;
            case "place":case "destination-aba":
                shell.ServerBrowser.UsePreferredPlace(999);
                if(change=="destination-aba")shell.ServerBrowser.UsePreferredPlace(42);
                break;
            case "server":
                shell.ServerBrowser.LinkInput="https://www.roblox.com/games/start?placeId=42&gameInstanceId="+EvidenceJob;
                shell.ServerBrowser.Resolve.Execute(null);break;
            case "installation":
                shell.SelectedInstallation=new(shell.SelectedInstallation!.Candidate with
                {ExecutablePath=Path.Combine(_root,"other-never-created","RobloxPlayerBeta.exe")});break;
            case "profile":
                using(var repository=new AccountRepository(_root))
                {await repository.InitializeAsync(default);await repository.SaveAsync(selected.Profile with{Notes="Changed during review"},default);}
                break;
            case "grant":
                using(var vault=new ProtectedCredentialVault(_root))
                    await vault.AllocateNextGenerationAsync(selected.Profile.Id,selected.AuthorizationGeneration!.Value,default);
                break;
            case "cached-grant":
                using(var vault=new ProtectedCredentialVault(_root))
                {var metadata=Assert.IsType<AuthorizationMetadata>(await vault.GetSummaryAsync(selected.Profile.Id,default));selected.SetCachedAuthorization(metadata with{Generation=metadata.Generation+1});}
                break;
            case "stop":shell.CancelNativeTrial.Execute(null);break;
            case "lock":shell.LockDashboard();break;
            case "draft":shell.ServerBrowser.LinkInput="Some other pending experience";break;
        }
        shell.StartNativeTrial.Execute(review);await shell.StartNativeTrial.Execution;
        Assert.Empty(shell.Launches);Assert.Equal(0,child.Preparations);Assert.Equal(0,child.Starts);
        Assert.Equal(shown,review.SelectionSummary);Assert.Equal(installation,review.InstallationSummary);
    });

    [Fact]
    public Task NativeTrialReviewPendingDestinationDraftRequiresExplicitSearchUseAndANewReview()=>OnUi(async ()=>
    {
        var child=new NativeEvidenceChild();
        await using var shell=await NativeEvidenceShellAsync(child,new NativeEvidenceMonitor());
        var review=Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview());
        shell.ServerBrowser.LinkInput="https://www.roblox.com/games/999/Other";
        Assert.False(shell.CanRunNativeTrial);Assert.Null(shell.CaptureNativeTrialReview());
        Assert.Contains("Search / Use",shell.NativeTrialSelection,StringComparison.Ordinal);
        Assert.Equal(42,shell.ServerBrowser.Destination!.PlaceId);
        shell.StartNativeTrial.Execute(review);await shell.StartNativeTrial.Execution;Assert.Empty(shell.Launches);
        shell.ServerBrowser.Resolve.Execute(null);
        Assert.Equal(999,shell.ServerBrowser.Destination!.PlaceId);Assert.True(shell.CanRunNativeTrial);
        shell.StartNativeTrial.Execute(review);await shell.StartNativeTrial.Execution;Assert.Empty(shell.Launches);
        var next=Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview());
        Assert.Contains("place 999",next.SelectionSummary,StringComparison.Ordinal);
        shell.DiscardNativeTrialReview(next);
        shell.StartNativeTrial.Execute(next);await shell.StartNativeTrial.Execution;
        Assert.Empty(shell.Launches);Assert.Equal(0,child.Preparations);
    });

    [Fact]
    public Task NativeTrialReviewNewCaptureReplacesPriorReviewEvenWhenValuesAreIdentical()=>OnUi(async ()=>
    {
        var child=new NativeEvidenceChild();
        await using var shell=await NativeEvidenceShellAsync(child,new NativeEvidenceMonitor());
        var prior=Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview());
        var current=Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview());
        Assert.NotSame(prior,current);Assert.Equal(prior.SelectionSummary,current.SelectionSummary);
        shell.StartNativeTrial.Execute(prior);await shell.StartNativeTrial.Execution;
        shell.StartNativeTrial.Execute(current);await shell.StartNativeTrial.Execution;
        Assert.Empty(shell.Launches);Assert.Equal(0,child.Preparations);
    });

    [Theory]
    [InlineData("destination")]
    [InlineData("account")]
    [InlineData("stop")]
    [InlineData("lock")]
    [InlineData("grant")]
    public Task NativeTrialReviewChangedDuringOwnedValidationCannotReachEnqueue(string change)=>OnUi(async ()=>
    {
        var repository=new NativeReviewRepository(_root);var child=new NativeEvidenceChild();
        await using var shell=await NativeEvidenceShellAsync(child,new NativeEvidenceMonitor(),repository);
        var selected=shell.SelectedAccount!;
        var review=Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview());
        repository.BlockFind=true;
        shell.StartNativeTrial.Execute(review);
        try
        {
            await repository.FindEntered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.False(shell.StartNativeTrial.Execution.IsCompleted);
            switch(change)
            {
                case "destination":shell.ServerBrowser.UsePreferredPlace(999);break;
                case "account":shell.SelectedAccount=shell.Accounts.Single(row=>row.Profile.Id!=selected.Profile.Id);break;
                case "stop":shell.Panic.Execute(null);break;
                case "lock":shell.LockDashboard();break;
                case "grant":
                    using(var vault=new ProtectedCredentialVault(_root))
                        await vault.AllocateNextGenerationAsync(selected.Profile.Id,selected.AuthorizationGeneration!.Value,default);
                    break;
            }
        }
        finally
        {
            repository.FindRelease.TrySetResult();
            await shell.StartNativeTrial.Execution.WaitAsync(TimeSpan.FromSeconds(4));
        }
        Assert.Empty(shell.Launches);Assert.Equal(0,child.Preparations);Assert.Equal(0,child.Starts);
    });

    [Fact]
    public Task NativeTrialReviewReentrantCommandNotificationCannotChangeTheConfirmedAccount()=>OnUi(async ()=>
    {
        var child=new NativeEvidenceChild();
        await using var shell=await NativeEvidenceShellAsync(child,new NativeEvidenceMonitor());
        var selected=shell.SelectedAccount!;
        var review=Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview());
        bool changed=false;
        void ChangeSelection(object? sender,EventArgs args)
        {
            if(changed)return;changed=true;
            shell.SelectedAccount=shell.Accounts.Single(row=>row.Profile.Id!=selected.Profile.Id);
        }
        shell.StartNativeTrial.CanExecuteChanged+=ChangeSelection;
        try{shell.StartNativeTrial.Execute(review);await shell.StartNativeTrial.Execution;}
        finally{shell.StartNativeTrial.CanExecuteChanged-=ChangeSelection;}
        Assert.True(changed);Assert.Empty(shell.Launches);Assert.Equal(0,child.Preparations);
    });

    private sealed class NativeReviewRepository(string root):IAccountRepository,IDisposable
    {
        private readonly AccountRepository _inner=new(root);
        internal bool BlockFind;
        internal TaskCompletionSource FindEntered {get;}=EvidenceGate();
        internal TaskCompletionSource FindRelease {get;}=EvidenceGate();
        public async Task<AccountProfile?> FindAsync(Guid id,CancellationToken ct)
        {
            if(BlockFind){FindEntered.TrySetResult();await FindRelease.Task.WaitAsync(ct);}
            return await _inner.FindAsync(id,ct);
        }
        public Task InitializeAsync(CancellationToken ct)=>_inner.InitializeAsync(ct);
        public Task<AccountPage> ReadAsync(AccountQuery query,CancellationToken ct)=>_inner.ReadAsync(query,ct);
        public Task<IReadOnlyList<string>> ReadGroupsAsync(CancellationToken ct)=>_inner.ReadGroupsAsync(ct);
        public Task<AccountProfile> SaveAsync(AccountProfile profile,CancellationToken ct)=>_inner.SaveAsync(profile,ct);
        public Task DeleteAsync(Guid id,long revision,CancellationToken ct)=>_inner.DeleteAsync(id,revision,ct);
        public Task CreateManyAsync(IReadOnlyList<AccountProfile> profiles,CancellationToken ct)=>_inner.CreateManyAsync(profiles,ct);
        public Task DeleteManyAsync(IReadOnlyList<AccountProfile> profiles,CancellationToken ct)=>_inner.DeleteManyAsync(profiles,ct);
        public Task<CredentialCleanupPage> ReadCredentialCleanupAsync(CancellationToken ct)=>_inner.ReadCredentialCleanupAsync(ct);
        public Task CompleteCredentialCleanupAsync(Guid id,CancellationToken ct)=>_inner.CompleteCredentialCleanupAsync(id,ct);
        public void Dispose()=>_inner.Dispose();
    }
}
