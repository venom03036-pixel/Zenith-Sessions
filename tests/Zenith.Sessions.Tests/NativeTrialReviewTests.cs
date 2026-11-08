using System.IO;
using System.Net.Http;
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
            await repository.FindEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
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
    public Task NativeTrialReviewChangedAfterTheQueueSavedTheTrialCancelsItSoAGeneralResumeCannotStartIt()=>OnUi(async ()=>
    {
        // The queue saved the trial request and passed its own stop check, then the review changed before
        // the handler continued. A review change does not stop the queue, so the saved row still holds this
        // request's in-memory trial consent; a later general Resume queue must not start it.
        var child=new NativeEvidenceChild();
        await using var shell=await NativeEvidenceShellAsync(child,new NativeEvidenceMonitor());
        var request=await ChangeTrialReviewAfterTheQueueSavedItAsync(shell,()=>{});
        Assert.Equal(0,child.Preparations);
        shell.ToggleQueuePause.Execute(null);await shell.ToggleQueuePause.Execution;
        Assert.Equal("Pause queue",shell.QueuePauseLabel);
        await Task.WhenAny(child.Entered.Task,Task.Delay(300));
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal(0,child.Preparations);Assert.Equal(0,child.Starts);
        Assert.Equal(LaunchState.Cancelled,Assert.Single(shell.Launches,row=>row.Id==request.Intent.OperationId).Operation.State);
        Assert.False(shell.HasErrorNotice);
        Assert.DoesNotContain("Trial queued",shell.NativeTrialStatus,StringComparison.Ordinal);
    });

    [Fact]
    public Task NativeTrialReviewChangedAfterTheQueueSavedTheTrialReportsACancelThatCouldNotBeSaved()=>OnUi(async ()=>
    {
        var child=new NativeEvidenceChild();
        await using var shell=await NativeEvidenceShellAsync(child,new NativeEvidenceMonitor());
        var diagnostics=(ManagerDiagnostics)typeof(ShellViewModel).GetField("_diagnostics",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(shell)!;
        long before=diagnostics.GetSnapshot().LatestSequence;
        // A kept interrupted save makes the cancel's write fail, which also stops the queue.
        await ChangeTrialReviewAfterTheQueueSavedItAsync(shell,()=>File.WriteAllText(Path.Combine(_root,"launch-queue.json.tmp"),"kept"));
        Assert.Equal("The trial review changed after its request was queued, and that request could not be cancelled. Review it in the launch queue before choosing Resume queue.",shell.ErrorNotice);
        Assert.Contains(diagnostics.GetSnapshot().Events,e=>e.Sequence>before&&e.Kind==ManagerEventKind.OperationFailed
            &&e.Subsystem==ManagerSubsystem.Queue&&e.ErrorType==nameof(IOException));
        Assert.False(queue(shell).IsHealthy);
        shell.ToggleQueuePause.Execute(null);await shell.ToggleQueuePause.Execution;
        await Task.WhenAny(child.Entered.Task,Task.Delay(300));
        Assert.Equal(0,child.Preparations);Assert.Equal(0,child.Starts);
        static LaunchCoordinator queue(ShellViewModel shell)=>(LaunchCoordinator)typeof(ShellViewModel).GetField("_launchQueue",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(shell)!;
    });

    // Starts the trial, lets the queue save it and pass its own stop check, then changes the review (after
    // beforeChange) while the handler's continuation is still queued behind this UI thread.
    private static async Task<LaunchOperation> ChangeTrialReviewAfterTheQueueSavedItAsync(ShellViewModel shell,Action beforeChange)
    {
        const BindingFlags hidden=BindingFlags.Instance|BindingFlags.NonPublic;
        static object Hidden(object owner,string name)=>owner.GetType().GetField(name,hidden)!.GetValue(owner)!;
        var queue=(LaunchCoordinator)Hidden(shell,"_launchQueue");
        var serial=(SemaphoreSlim)Hidden(queue,"_serial");var writer=(SemaphoreSlim)Hidden(Hidden(shell,"_queueStore"),"_writer");
        var operations=(List<LaunchOperation>)Hidden(queue,"_operations");
        using(var settle=new CancellationTokenSource(TimeSpan.FromSeconds(4)))
            while((long)Hidden(queue,"_generation")!=(long)Hidden(queue,"_persistedStopGeneration"))await Task.Delay(5,settle.Token);
        Assert.Empty(operations);
        var review=Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview());
        // Hold the queue file so the enqueue suspends inside its save and finishes on a pool thread.
        Assert.True(await writer.WaitAsync(TimeSpan.FromSeconds(4)));
        bool holdingWriter=true;
        try
        {
            shell.StartNativeTrial.Execute(review);
            using(var added=new CancellationTokenSource(TimeSpan.FromSeconds(4)))
                while(operations.Count==0)await Task.Delay(5,added.Token);
            writer.Release();holdingWriter=false;
            // Block this UI thread until the enqueue leaves the queue lane, past its own stop check.
            Assert.True(serial.Wait(TimeSpan.FromSeconds(4)));serial.Release();
            Assert.False(shell.StartNativeTrial.Execution.IsCompleted);
            var request=Assert.Single(operations.ToArray());Assert.Equal(LaunchState.Queued,request.State);
            Assert.True(queue.IsTrialOperation(request.Intent.OperationId));
            beforeChange();
            shell.ServerBrowser.UsePreferredPlace(999);
            await shell.StartNativeTrial.Execution.WaitAsync(TimeSpan.FromSeconds(4));
            return request;
        }
        finally{if(holdingWriter)writer.Release();}
    }

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

    [Fact]
    public Task NativeTrialInstallationChangeDuringPreparationReportsTheStop()=>OnUi(async ()=>
    {
        // Changing the selected installation fences the trial by design; the user must be told it stopped.
        var child=new NativeEvidenceChild{PreparationGate=EvidenceGate()};
        await using var shell=await NativeEvidenceShellAsync(child,new NativeEvidenceMonitor());
        Assert.True(shell.CanRefreshInstallations);
        shell.StartNativeTrial.Execute(Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview()));await shell.StartNativeTrial.Execution;
        await child.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        bool refreshReenabled=false;
        void Watch(object? sender,System.ComponentModel.PropertyChangedEventArgs args)
        {if(args.PropertyName==nameof(ShellViewModel.CanRefreshInstallations)&&shell.CanRefreshInstallations)refreshReenabled=true;}
        shell.PropertyChanged+=Watch;
        try
        {
            // Refresh would clear the trial installation; it stays unavailable while the provider owns a preparation.
            Assert.False(shell.CanRefreshInstallations);
            shell.SelectedInstallation=new(shell.SelectedInstallation!.Candidate with
            {ExecutablePath=Path.Combine(_root,"other-never-created","RobloxPlayerBeta.exe")});
        }
        finally{child.PreparationGate!.TrySetResult();}
        try
        {
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(6));
            while(shell.Launches.Any(row=>row.Operation.State is LaunchState.Queued or LaunchState.Preparing or LaunchState.Validating or LaunchState.Launching))
                await Task.Delay(5,stop.Token);
        }
        finally{shell.PropertyChanged-=Watch;}
        Assert.Contains("stopped",shell.NativeTrialStatus,StringComparison.OrdinalIgnoreCase);
        Assert.Contains("installation selection changed",shell.NativeTrialStatus,StringComparison.Ordinal);
        Assert.DoesNotContain("Native file selected",shell.NativeTrialStatus,StringComparison.Ordinal);
        Assert.Equal(shell.NativeTrialStatus,shell.Notice);
        Assert.DoesNotContain("Trial queued",shell.Notice,StringComparison.Ordinal);
        Assert.Equal(1,child.Preparations);Assert.Equal(0,child.Starts);
        Assert.True(refreshReenabled);Assert.True(shell.CanRefreshInstallations);
    });

    [Fact]
    public Task NativeTrialInstallationInspectionWithoutATrialKeepsTheSelectionStatus()=>OnUi(async ()=>
    {
        var child=new NativeEvidenceChild();
        await using var shell=await NativeEvidenceShellAsync(child,new NativeEvidenceMonitor());
        string notice=shell.Notice;
        shell.SelectedInstallation=new(shell.SelectedInstallation!.Candidate with
        {ExecutablePath=Path.Combine(_root,"other-never-created","RobloxPlayerBeta.exe")});
        Assert.DoesNotContain("stopped",shell.NativeTrialStatus,StringComparison.OrdinalIgnoreCase);
        Assert.Equal(notice,shell.Notice);Assert.True(shell.CanRefreshInstallations);
        Assert.Equal(0,child.Preparations);
    });

    // Rule 13: a start's release read borrows the shell's anonymous public HTTP client. Exit cancels that read first, but a
    // cancelled request can still be finishing when exit reaches the directory owner, so exit waits for the trial command
    // before it releases the client, as it waits for an installation refresh and its read.
    [Fact]
    public Task ExitWaitsForATrialReleaseReadBeforeReleasingThePublicHttpClient()=>OnUi(async ()=>
    {
        var releases=new HeldReleaseRead();
        await using var shell=await NativeEvidenceShellAsync(new NativeEvidenceChild(),new NativeEvidenceMonitor(),releases:releases);
        var http=(HttpClient)typeof(ShellViewModel).GetField("_publicHttp",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(shell)!;
        Task exit;
        try
        {
            shell.StartNativeTrial.Execute(Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview()));
            var read=await releases.Entered.Task.WaitAsync(TimeSpan.FromSeconds(6));
            exit=shell.DisposeAsync().AsTask();
            Assert.True(read.IsCancellationRequested); // Exit asks the read to stop before it waits for any owner.
            // Give exit time to reach the directory owner; it may not release the client while the read is still finishing.
            using(var window=new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                while(!window.IsCancellationRequested&&!exit.IsCompleted&&!IsReleased(http))await Task.Delay(10);
            Assert.False(IsReleased(http),"Exit released the public HTTP client while the trial's release read was still finishing.");
        }
        finally{releases.Finish.TrySetResult();}
        await exit.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(IsReleased(http)); // The owner still releases it once the read has finished.
        Assert.Equal(1,releases.Calls);
        static bool IsReleased(HttpClient client)
        {
            try{client.CancelPendingRequests();return false;}
            catch(ObjectDisposedException){return true;}
        }
    });

    // N-B8-5: a start whose last release answer is old asks Roblox again and waits for it (at most five seconds) before it
    // re-checks the review and queues. The trial card says so while it waits, and the next status replaces that line.
    [Fact]
    public Task ATrialStartSaysItIsCheckingRobloxsReleaseWhileItWaitsForTheAnswer()=>OnUi(async ()=>
    {
        var releases=new HeldReleaseRead();
        await using var shell=await NativeEvidenceShellAsync(new NativeEvidenceChild(),new NativeEvidenceMonitor(),releases:releases);
        try
        {
            shell.StartNativeTrial.Execute(Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview()));
            await releases.Entered.Task.WaitAsync(TimeSpan.FromSeconds(6));
            Assert.StartsWith("Checking Roblox's current release before this trial is queued",shell.NativeTrialStatus,StringComparison.Ordinal);
            Assert.Contains("at most 5 seconds",shell.NativeTrialStatus,StringComparison.Ordinal);
            Assert.Contains("Stop trial",shell.NativeTrialStatus,StringComparison.Ordinal);
        }
        finally{releases.Finish.TrySetResult();}
        await shell.StartNativeTrial.Execution.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("Trial queued for ",shell.NativeTrialStatus,StringComparison.Ordinal);
        Assert.Equal(1,releases.Calls);
    });

    // The waiting line is only this start's own: Stop trial, Panic and Lock replace it with their reason, which is kept. A review
    // change (another account or destination) cancels the check too and sets no line of its own, so the start must say the trial
    // was not started rather than put back what the card said before (O-C9-2); the user has just read that it is being checked.
    [Theory]
    [InlineData("stop")]
    [InlineData("panic")]
    [InlineData("lock")]
    [InlineData("account")]
    [InlineData("destination")]
    public Task AStoppedReleaseCheckNeverLeavesTheTrialCardSayingItIsChecking(string change)=>OnUi(async ()=>
    {
        var releases=new HeldReleaseRead();
        await using var shell=await NativeEvidenceShellAsync(new NativeEvidenceChild(),new NativeEvidenceMonitor(),releases:releases);
        string before=shell.NativeTrialStatus;
        try
        {
            shell.StartNativeTrial.Execute(Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview()));
            var read=await releases.Entered.Task.WaitAsync(TimeSpan.FromSeconds(6));
            Assert.StartsWith("Checking Roblox's current release",shell.NativeTrialStatus,StringComparison.Ordinal);
            switch(change)
            {
                case "stop":shell.CancelNativeTrial.Execute(null);break;
                case "panic":shell.Panic.Execute(null);break;
                case "lock":shell.LockDashboard();break;
                case "account":shell.SelectedAccount=shell.Accounts.Single(row=>row.Profile.Label=="Other synthetic profile");break;
                case "destination":shell.ServerBrowser.UsePreferredPlace(999);break;
            }
            Assert.True(read.IsCancellationRequested);
        }
        finally{releases.Finish.TrySetResult();}
        await shell.StartNativeTrial.Execution.WaitAsync(TimeSpan.FromSeconds(10));
        switch(change)
        {
            case "stop":Assert.StartsWith("Trial stopped.",shell.NativeTrialStatus,StringComparison.Ordinal);break;
            case "panic":case "lock":Assert.StartsWith("Trial stopped by Panic or Lock.",shell.NativeTrialStatus,StringComparison.Ordinal);break;
            default:
                Assert.Equal("Trial not started: the selected account or destination changed while it was being checked. Review the selected account and destination again before starting.",shell.NativeTrialStatus);
                Assert.Equal(shell.NativeTrialStatus,shell.Notice);
                Assert.NotEqual(before,shell.NativeTrialStatus);Assert.False(shell.HasErrorNotice);
                break;
        }
        Assert.DoesNotContain("Checking Roblox's current release",shell.NativeTrialStatus,StringComparison.Ordinal);
        Assert.Empty(shell.Launches);
    });

    // A release request that is still finishing after its cancellation: it returns only when the test lets it.
    private sealed class HeldReleaseRead:IPublicRobloxClientReleaseDirectory
    {
        public int Calls;
        internal TaskCompletionSource<CancellationToken> Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finish {get;}=EvidenceGate();
        public async Task<PublicDirectoryResult<PublicRobloxClientRelease>> GetCurrentWindowsPlayerReleaseAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);Entered.TrySetResult(ct);
            await Finish.Task;
            ct.ThrowIfCancellationRequested();
            return new(PublicDirectoryState.Failure,null,"Synthetic failure.");
        }
    }
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
