using System.IO;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class DirectoryIntegrationTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-directory-ui-tests-"+Guid.NewGuid().ToString("N"));
    [Fact]
    public Task QueueLabelsFollowLoadedProfileNamesAfterRenameAndRestart()=>OnUi(async () =>
    {
        await using(var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new Monitor(),new PublicDirectory(),new Servers()))
        {
            await vm.InitializeAsync();await vm.SaveAccountAsync(AccountProfile.Create("Original sample") with {PreferredPlaceId=6564161046});
            vm.QueueSelected.Execute(null);await vm.QueueSelected.Execution;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("Original sample",Assert.Single(vm.Launches).Label);
            var notifications=new List<string?>();vm.Launches[0].PropertyChanged+=(_,e)=>notifications.Add(e.PropertyName);
            await vm.SaveAccountAsync(vm.Accounts[0].Profile with {Label="Renamed sample"});
            Assert.Equal("Renamed sample",vm.Launches[0].Label);Assert.Contains("Label",notifications);
        }
        await using(var reopened=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new Monitor(),new PublicDirectory(),new Servers()))
        {
            await reopened.InitializeAsync();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var row=Assert.Single(reopened.Launches);Assert.Equal("Renamed sample",row.Label);
            // A never-started request is recovered as a cancellable non-reserving row, not a possible client.
            Assert.Equal(LaunchState.Blocked,row.Operation.State);Assert.Equal(LaunchProblem.Interrupted,row.Operation.Problem);
            // M-L2-2: it cannot be reviewed, so it says it never started instead of asking for review, and the
            // account list and that account's Details notice use the queue row's words.
            Assert.Equal("Not started · interrupted",row.State);Assert.True(row.CanCancel);Assert.False(row.CanReview);
            var account=Assert.Single(reopened.Accounts);Assert.Equal(row.State,account.LaunchStatus);Assert.Equal("Details",account.PowerLabel);
            reopened.AccountPower.Execute(account);await reopened.AccountPower.Execution;
            Assert.Equal("This request never started a client. Cancel it and queue again if it is still needed.",reopened.Notice);
            Assert.Same(row,Assert.Single(reopened.Launches));Assert.Equal(LaunchState.Blocked,row.Operation.State);
        }
    });
    [Fact]
    public Task ProfileAndTabRestorationNeverAutomaticallyContactsThePublicDirectory()=>OnUi(async ()=>
    {
        var directory=new PublicDirectory();await using var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new Monitor(),directory,new Servers());
        await vm.InitializeAsync();await vm.SaveAccountAsync(AccountProfile.Create("Synthetic metadata"));
        foreach(string tab in new[]{"Accounts","Sessions","JoinGame","Automation","Settings","Guide","Overview","Accounts"}) vm.Navigate.Execute(tab);
        vm.SelectedAccount=Assert.Single(vm.Accounts);vm.Refresh.Execute(null);await vm.Refresh.Execution;
        Assert.Equal(0,directory.Calls);Assert.Null(vm.SelectedAccount!.Avatar);Assert.Equal("No verified client",vm.SelectedAccount.LaunchStatus);
    });
    [Fact]
    public Task CheckedRecipientsAndExplicitServerTargetRemainFrozenWhileTheVisibleSelectionChanges()=>OnUi(async ()=>
    {
        var directory=new PublicDirectory();await using var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new Monitor(),directory,new Servers());
        await vm.InitializeAsync();await vm.SaveAccountAsync(AccountProfile.Create("First") with { PreferredPlaceId=1 });await vm.SaveAccountAsync(AccountProfile.Create("Second") with { PreferredPlaceId=2 });
        foreach(var row in vm.Accounts) row.IsChecked=true;
        const string job="9a073cb7-73b1-42bd-844b-83a09cbec848";
        vm.ServerBrowser.LinkInput=$"https://www.roblox.com/games/start?placeId=6564161046&gameInstanceId={job}";vm.ServerBrowser.Resolve.Execute(null);
        vm.QueueTargetedAccounts.Execute(null);
        vm.ServerBrowser.UsePreferredPlace(999);vm.SelectedAccount=vm.Accounts.First();foreach(var row in vm.Accounts) row.IsChecked=false;
        await vm.QueueTargetedAccounts.Execution;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(2,vm.Launches.Count);
        Assert.All(vm.Launches,l=>
        {
            Assert.Equal(6564161046,l.Operation.Intent.PlaceId);Assert.Equal(job,l.Operation.Intent.RequestedJobId);Assert.True(l.Operation.Intent.HasExplicitDestination);
            Assert.Equal(LaunchState.Blocked,l.Operation.State);Assert.Equal(LaunchProblem.ProviderUnavailable,l.Operation.Problem);
            Assert.Contains(job,l.RequestedDestination);
        });
        Assert.Equal(new long?[]{1,2},vm.Accounts.OrderBy(r=>r.Label).Select(r=>r.Profile.PreferredPlaceId));
        Assert.Contains("2 blocked",vm.Notice);Assert.Equal(0,directory.Calls);
    });
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task NewServerDraftCannotQueueThePreviousServerForCheckedAccountsOrABundle(bool bundle)=>OnUi(async () =>
    {
        await using var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new Monitor(),new PublicDirectory(),new Servers());
        await vm.InitializeAsync();
        await vm.SaveAccountAsync(AccountProfile.Create("First"));
        await vm.SaveAccountAsync(AccountProfile.Create("Second"));
        foreach(var row in vm.Accounts) row.IsChecked=true;
        await vm.RecipientBundles.SaveCheckedAsync("Saved pair");
        const string oldJob="9a073cb7-73b1-42bd-844b-83a09cbec848";
        const string newJob="2e5f96ac-8e17-4d91-bf0e-bd2492a47bea";
        vm.ServerBrowser.LinkInput=$"https://www.roblox.com/games/start?placeId=6564161046&gameInstanceId={oldJob}";
        vm.ServerBrowser.Resolve.Execute(null);
        vm.ServerBrowser.LinkInput=$"https://www.roblox.com/games/start?placeId=6564161046&gameInstanceId={newJob}";
        Assert.Equal(oldJob,vm.ServerBrowser.Destination!.JobId);
        await QueueAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Empty(vm.Launches);
        Assert.Null(vm.BuildRequestedDestinationLink());
        Assert.False(vm.RecipientBundles.CanQueueSelected);

        vm.Navigate.Execute("JoinGame");vm.SetPreviewWindowVisible(true);
        await vm.DestinationSearch.SearchOrUseAsync();
        Assert.True(vm.RecipientBundles.CanQueueSelected);
        Assert.Contains(newJob,vm.BuildRequestedDestinationLink());
        await QueueAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(2,vm.Launches.Count);
        Assert.All(vm.Launches,row=>
        {
            Assert.Equal(6564161046,row.Operation.Intent.PlaceId);
            Assert.Equal(newJob,row.Operation.Intent.RequestedJobId);
            Assert.Equal(LaunchState.Blocked,row.Operation.State);
        });

        async Task QueueAsync()
        {
            if(bundle) await vm.RecipientBundles.QueueSelectedAsync();
            else {vm.QueueTargetedAccounts.Execute(null);await vm.QueueTargetedAccounts.Execution;}
        }
    });
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task QueueingCheckedAccountsOrABundleCommitsOneQueueWrite(bool bundle)=>OnUi(async () =>
    {
        await using var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new Monitor(),new PublicDirectory(),new Servers());
        await vm.InitializeAsync();
        foreach(string label in new[]{"First","Second","Third"}) await vm.SaveAccountAsync(AccountProfile.Create(label));
        foreach(var row in vm.Accounts) row.IsChecked=true;
        if(bundle) await vm.RecipientBundles.SaveCheckedAsync("Saved trio");
        vm.ServerBrowser.UsePreferredPlace(42);
        if(bundle) Assert.True(vm.RecipientBundles.CanQueueSelected);
        await QueueAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(3,vm.Launches.Count);
        Assert.All(vm.Launches,row=>{Assert.Equal(42,row.Operation.Intent.PlaceId);Assert.Equal(LaunchState.Blocked,row.Operation.State);});
        Assert.Contains("0 queued, 3 blocked, 0 already pending or unavailable",Summary());
        // Every queue save moves the previous generation to .bak, so one batch write leaves the
        // empty startup generation there; per-account writes would leave a partly filled queue.
        Assert.Empty(await ReadQueueBackupAsync());

        // Accounts that already have pending work are reported without another queue write.
        await QueueAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(3,vm.Launches.Count);
        Assert.Contains("0 queued, 0 blocked, 3 already pending or unavailable",Summary());
        Assert.Empty(await ReadQueueBackupAsync());

        async Task QueueAsync()
        {
            if(bundle) await vm.RecipientBundles.QueueSelectedAsync();
            else {vm.QueueTargetedAccounts.Execute(null);await vm.QueueTargetedAccounts.Execution;}
        }
        string Summary()=>bundle?vm.RecipientBundles.Status:vm.Notice;
    });
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SpreadingGivesEachCheckedAccountItsOwnLoadedServerUnlessAServerIsPinned(bool pinned)=>OnUi(async () =>
    {
        var servers=new Servers();
        await using var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new Monitor(),new PublicDirectory(),servers);
        await vm.InitializeAsync();
        foreach(string label in new[]{"First","Second","Third","Fourth"}) await vm.SaveAccountAsync(AccountProfile.Create(label));
        foreach(var row in vm.Accounts) row.IsChecked=true;
        const long place=42;var now=DateTimeOffset.UtcNow;
        string[] jobs=["31111111-2222-4333-8444-000000000001","31111111-2222-4333-8444-000000000002","31111111-2222-4333-8444-000000000003"];
        // 9/10 has room for one more player but no spare seat, so only two of these servers are handed out.
        servers.Rows=[new(place,jobs[0],1,10,null,null,now),new(place,jobs[1],9,10,null,null,now),new(place,jobs[2],5,10,null,null,now)];
        vm.ServerBrowser.UsePreferredPlace(place);
        vm.ServerBrowser.Browse.Execute(null);await vm.ServerBrowser.Browse.Execution;
        Assert.Equal(3,vm.ServerBrowser.Servers.Count);
        // Off by default: the batch keeps one destination.
        Assert.False(vm.ServerBrowser.SpreadAcrossServers);
        Assert.Equal("Same destination for everyone",vm.ServerBrowser.BatchDestinationCaption);
        if(pinned) {vm.ServerBrowser.SelectedServer=vm.ServerBrowser.Servers[1];vm.ServerBrowser.UseServer.Execute(null);}
        vm.ServerBrowser.SpreadAcrossServers=true;
        Assert.Equal(pinned?"A server is pinned, so everyone goes to it":"Each account gets its own listed server",vm.ServerBrowser.BatchDestinationCaption);
        vm.QueueTargetedAccounts.Execute(null);await vm.QueueTargetedAccounts.Execution;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(4,vm.Launches.Count);
        var requested=vm.Launches.Select(l=>l.Operation.Intent.RequestedJobId).ToArray();
        Assert.All(vm.Launches,l=>{Assert.Equal(place,l.Operation.Intent.PlaceId);Assert.True(l.Operation.Intent.HasExplicitDestination);});
        if(pinned)
        {
            Assert.All(requested,job=>Assert.Equal(jobs[1],job));
            Assert.Contains("A server is pinned, so every account was sent to it",vm.Notice);
            return;
        }
        var own=requested.Where(job=>job is not null).ToArray();
        Assert.Equal(2,own.Length);Assert.Equal(2,own.Distinct().Count());
        Assert.All(own,job=>Assert.Contains(job,new[]{jobs[0],jobs[2]}));
        Assert.Equal(2,requested.Count(job=>job is null));
        // Every request is blocked (the general provider is unavailable), so the advice counts recorded requests, not accounts that got in.
        Assert.Contains("0 queued, 4 blocked, 0 already pending or unavailable",vm.Notice);
        Assert.Contains("2 of 4 requests got their own listed server",vm.Notice);
        Assert.Contains("the other 2 ask for the place without a chosen server",vm.Notice);
        Assert.DoesNotContain("accounts got",vm.Notice);
    });
    [Fact]
    public Task SpreadingGivesNoServerToAnAccountThatIsAlreadyPending()=>OnUi(async () =>
    {
        var servers=new Servers();
        await using var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new Monitor(),new PublicDirectory(),servers);
        await vm.InitializeAsync();
        foreach(string label in new[]{"First","Second","Third","Fourth"}) await vm.SaveAccountAsync(AccountProfile.Create(label));
        const long place=42;var now=DateTimeOffset.UtcNow;
        string[] jobs=["51111111-2222-4333-8444-000000000001","51111111-2222-4333-8444-000000000002","51111111-2222-4333-8444-000000000003"];
        servers.Rows=[new(place,jobs[0],1,10,null,null,now),new(place,jobs[1],2,10,null,null,now),new(place,jobs[2],3,10,null,null,now)];
        vm.ServerBrowser.UsePreferredPlace(place);
        // The first two accounts in the list already have a request waiting.
        var waiting=vm.Accounts.Take(2).Select(row=>row.Profile.Id).ToArray();
        foreach(var row in vm.Accounts.Take(2)) row.IsChecked=true;
        vm.QueueTargetedAccounts.Execute(null);await vm.QueueTargetedAccounts.Execution;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(2,vm.Launches.Count);

        vm.ServerBrowser.Browse.Execute(null);await vm.ServerBrowser.Browse.Execution;
        Assert.Equal(3,vm.ServerBrowser.Servers.Count);
        foreach(var row in vm.Accounts) row.IsChecked=true;
        vm.ServerBrowser.SpreadAcrossServers=true;
        vm.QueueTargetedAccounts.Execute(null);await vm.QueueTargetedAccounts.Execution;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(4,vm.Launches.Count);
        var added=vm.Launches.Where(l=>!waiting.Contains(l.Operation.Intent.ProfileId)).Select(l=>l.Operation.Intent.RequestedJobId).ToArray();
        // Both new requests get a listed server; the waiting accounts use none up.
        Assert.Equal(2,added.Length);Assert.All(added,job=>Assert.Contains(job,jobs));Assert.Equal(2,added.Distinct().Count());
        Assert.Contains("0 queued, 2 blocked, 2 already pending or unavailable",vm.Notice);
        Assert.Contains("Each of the 2 requests got its own listed server",vm.Notice);
        Assert.DoesNotContain("of 4",vm.Notice);
    });
    private async Task<IReadOnlyList<LaunchOperation>> ReadQueueBackupAsync()
    {
        string copy=Path.Combine(_root,"queue-backup-check-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(copy);
        File.Copy(Path.Combine(_root,"launch-queue.json.bak"),Path.Combine(copy,"launch-queue.json"));
        using var store=new LaunchQueueStore(copy);
        return (await store.LoadAsync(CancellationToken.None)).Operations;
    }
    [Fact]
    public Task DiscardingDraftReenablesTheLastExplicitRequest()=>OnUi(async () =>
    {
        await using var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new Monitor(),new PublicDirectory(),new Servers());
        await vm.InitializeAsync();vm.Navigate.Execute("JoinGame");vm.SetPreviewWindowVisible(true);
        vm.ServerBrowser.UsePreferredPlace(42);
        vm.ServerBrowser.LinkInput="43";
        Assert.True(vm.DestinationSearch.CanCancel);Assert.Null(vm.BuildRequestedDestinationLink());
        vm.DestinationSearch.Cancel.Execute(null);
        Assert.False(vm.ServerBrowser.HasDestinationDraft);Assert.False(vm.DestinationSearch.CanCancel);
        Assert.Contains("placeId=42",vm.BuildRequestedDestinationLink());Assert.Empty(vm.Launches);
    });
    [Fact]
    public Task ClearingCommittedDraftNeverExposesThePreviousServerToAReentrantCopy()=>OnUi(async () =>
    {
        await using var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new Monitor(),new PublicDirectory(),new Servers());
        await vm.InitializeAsync();
        vm.ServerBrowser.UsePreferredPlace(1);
        vm.ServerBrowser.LinkInput="2";
        var copies=new List<string?>();
        vm.ServerBrowser.PropertyChanged+=(_,args)=>
        {
            if(args.PropertyName==nameof(ServerBrowserViewModel.LinkInput)&&vm.ServerBrowser.LinkInput.Length==0)
                copies.Add(vm.BuildRequestedDestinationLink());
        };
        vm.ServerBrowser.Resolve.Execute(null);
        string? observed=Assert.Single(copies);
        if(observed is not null) Assert.Contains("placeId=2",observed);
        Assert.Contains("placeId=2",vm.BuildRequestedDestinationLink());
    });
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CheckedRequestFreezesBeforeCommandNotificationsAndObservesPanic(bool panic)=>OnUi(async () =>
    {
        await using var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new Monitor(),new PublicDirectory(),new Servers());
        await vm.InitializeAsync();await vm.SaveAccountAsync(AccountProfile.Create("First"));await vm.SaveAccountAsync(AccountProfile.Create("Second"));
        var first=vm.Accounts.Single(row=>row.Label=="First");var second=vm.Accounts.Single(row=>row.Label=="Second");
        first.IsChecked=true;vm.ServerBrowser.UsePreferredPlace(42);
        vm.QueueTargetedAccounts.CanExecuteChanged+=(_,_)=>
        {
            if(vm.QueueTargetedAccounts.CanExecute(null))return;
            first.IsChecked=false;second.IsChecked=true;vm.ServerBrowser.UsePreferredPlace(43);
            if(panic)vm.Panic.Execute(null);
        };
        vm.QueueTargetedAccounts.Execute(null);await vm.QueueTargetedAccounts.Execution;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if(panic) Assert.Empty(vm.Launches);
        else
        {
            var intent=Assert.Single(vm.Launches).Operation.Intent;
            Assert.Equal(first.Profile.Id,intent.ProfileId);Assert.Equal(42,intent.PlaceId);
        }
        Assert.Equal(43,vm.ServerBrowser.Destination!.PlaceId);
    });
    [Fact]
    public Task LatePublicPresentationCannotReappearAfterLockAndNeverChangesLinkedIdentity()=>OnUi(async ()=>
    {
        using(var profiles=new AccountRepository(_root))
        {
            await profiles.InitializeAsync(default);var profile=await profiles.SaveAsync(AccountProfile.Create("Alias"),default);
            using var vault=new ProtectedCredentialVault(_root);
            await vault.SaveAsync(new(profile.Id,"roblox-oauth",123,1,"","synthetic-refresh",DateTimeOffset.UtcNow.AddMinutes(10),"linked_name",Issuer:RobloxOAuthIdentity.Issuer,Subject:"123"),default);
        }
        var directory=new PublicDirectory {Pending=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        await using var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new Monitor(),directory,new Servers());await vm.InitializeAsync();
        var row=Assert.Single(vm.Accounts);vm.SelectedAccount=row;Assert.Equal("linked_name",row.Label);Assert.Equal(0,directory.Calls);
        vm.RefreshPublicAccount.Execute(row);await directory.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));Assert.Equal(1,directory.Calls);vm.LockDashboard();
        directory.Pending.SetResult(directory.Profile);await vm.RefreshPublicAccount.Execution;
        Assert.True(vm.IsLocked);Assert.Equal("",row.PublicDisplayName);Assert.Null(row.Avatar);Assert.Equal("linked_name",row.Label);
        directory.Pending=null;vm.Unlock.Execute(null);vm.RefreshPublicAccount.Execute(row);await vm.RefreshPublicAccount.Execution;
        Assert.Equal("Public display",row.PublicDisplayName);Assert.Equal("linked_name",row.Label);Assert.Equal("@linked_name",row.Username);Assert.Equal(123,row.KnownUserId);
        vm.Panic.Execute(null);Assert.Equal("",row.PublicDisplayName);Assert.Null(row.Avatar);
    });
    private sealed class PublicDirectory : IPublicRobloxDirectory
    {
        public int Calls;
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<PublicDirectoryResult<PublicRobloxUser>>? Pending;
        public PublicDirectoryResult<PublicRobloxUser> Profile=>new(PublicDirectoryState.Success,new(123,"public_name","Public display",DateTimeOffset.UtcNow),"Synthetic public profile");
        public Task<PublicDirectoryResult<PublicRobloxUser>> LookupUserAsync(long id,CancellationToken ct) {Interlocked.Increment(ref Calls);Started.TrySetResult();return Pending?.Task??Task.FromResult(Profile);}
        public Task<PublicDirectoryResult<PublicRobloxAvatar>> GetAvatarAsync(long id,CancellationToken ct)=>Task.FromResult(new PublicDirectoryResult<PublicRobloxAvatar>(PublicDirectoryState.Unknown,null,"Synthetic no avatar"));
        public Task<PublicDirectoryResult<EncodedPublicAvatar>> FetchAvatarAsync(PublicRobloxAvatar avatar,CancellationToken ct)=>throw new InvalidOperationException("No synthetic avatar was supplied.");
        public void ClearMemoryCache() { }
    }
    private sealed class Servers : IRobloxPublicServerDirectory
    {
        public IReadOnlyList<RobloxPublicServer> Rows {get;set;}=[];
        public Task<RobloxPublicServerResult> BrowseAsync(RobloxPublicServerQuery query,CancellationToken ct)=>Task.FromResult(new RobloxPublicServerResult(RobloxPublicServerStatus.Complete,Rows,null,null,"Synthetic only"));
    }
    private sealed class Monitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)=>Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,64*CapacityPolicy.GiB,20*CapacityPolicy.GiB,20),[]));
        public Task<bool> FocusAsync(SessionObservation observation,CancellationToken ct)=>Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation observation,CancellationToken ct)=>Task.FromResult(false);
    }
    private static Task OnUi(Func<Task> work)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;Task operation=Task.CompletedTask;
            dispatcher.BeginInvoke(()=>operation=RunAsync());
            async Task RunAsync() {try {await work();done.TrySetResult();}catch(Exception ex) {done.TrySetException(ex);}finally {dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}}
            Dispatcher.Run();operation.GetAwaiter().GetResult();
        }) {IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-directory-ui-tests-",StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if(Directory.Exists(full)) Directory.Delete(full,true);
    }
}
