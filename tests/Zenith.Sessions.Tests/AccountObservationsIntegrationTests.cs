using System.IO;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    private sealed class ObservationData : IRobloxAccountDataClient
    {
        private readonly List<(RobloxAccountRead Kind,long User,string Cookie)> _calls=[];
        public (RobloxAccountRead Kind,long User,string Cookie)[] Calls { get { lock(_calls)return _calls.ToArray(); } }
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Hold;
        public CancellationToken LastToken;
        public async Task<RobloxAccountResponse> ReadAsync(RobloxAccountRead kind,long user,long? target,RobloxSessionCredential credential,CancellationToken ct)
        {
            lock(_calls)_calls.Add((kind,user,credential.CopyValue())); LastToken=ct; Entered.TrySetResult();
            if(Hold is not null)await Hold.Task;
            return new(RobloxAccountOutcome.Success,kind switch
            {
                RobloxAccountRead.Presence=>new(Presence:new(RobloxReportedPresence.InGame,123,null)),
                RobloxAccountRead.FriendshipStatus=>new(Friendship:RobloxFriendshipStatus.Friends),
                RobloxAccountRead.Robux=>new(Number:938572614),
                RobloxAccountRead.TwoStepMethods=>new(Methods:"Authenticator"),
                RobloxAccountRead.Premium=>null, // Unknown, not false.
                RobloxAccountRead.FriendsCount or RobloxAccountRead.FollowersCount=>new(Number:12),
                _=>new(Flag:true)
            });
        }
    }
    [Fact]
    public Task AccountObservationsAreExplicitAndPresenceUsesEachCheckedAccountsOwnSession()=>OnUi(async()=>
    {
        var data=new ObservationData();var signIn=new HealthSignIn();var identity=new HealthClient();
        await using var shell=await HealthShell(signIn,identity,linked:3,accountData:data);
        Assert.Empty(data.Calls);
        HealthRow(shell,1).IsChecked=true;HealthRow(shell,3).IsChecked=true;
        shell.SelectedAccount=HealthRow(shell,2);
        shell.RefreshPresence.Execute(null);await shell.RefreshPresence.Execution;
        Assert.Equal(2,data.Calls.Length);Assert.Equal(new[]{1001L,1003L},data.Calls.Select(c=>c.User).Order());
        Assert.All(data.Calls,c=>Assert.Equal(HealthCookie((int)c.User-1000),c.Cookie));
        Assert.Contains("Roblox reports in game",HealthRow(shell,1).ServerPresenceText);Assert.Equal("Not refreshed",HealthRow(shell,2).ServerPresenceText);
        Assert.Empty(shell.Launches);HealthNoSecrets(shell);Assert.False(shell.IsAuthorizing);
    });
    [Fact]
    public Task PrivateDetailsStayUnknownWhenMissingAndClearOnSelectionChangeWithoutPersisting()=>OnUi(async()=>
    {
        var data=new ObservationData();await using var shell=await HealthShell(new HealthSignIn(),new HealthClient(),linked:2,accountData:data);
        shell.SelectedAccount=HealthRow(shell,1);shell.RefreshPrivateDetails.Execute(null);await shell.RefreshPrivateDetails.Execution;
        Assert.Contains("Robux: 938572614",shell.PrivateAccountDetails);Assert.Contains("Premium: Unknown",shell.PrivateAccountDetails);Assert.Contains("Authenticator",shell.PrivateAccountDetails);
        Assert.Equal(7,data.Calls.Length);Assert.All(data.Calls,c=>Assert.Equal(1001,c.User));
        Assert.DoesNotContain("938572614",shell.Notice);Assert.DoesNotContain(shell.Events,e=>e.Contains("938572614"));
        foreach(string path in Directory.EnumerateFiles(_root,"*.json",SearchOption.AllDirectories))Assert.DoesNotContain("938572614",await File.ReadAllTextAsync(path));
        shell.SelectedAccount=HealthRow(shell,2);Assert.DoesNotContain("938572614",shell.PrivateAccountDetails);Assert.False(shell.IsAuthorizing);
    });
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PanicAndLockCancelLatePrivateReadsAndDiscardEveryPrivateObservation(bool locking)=>OnUi(async()=>
    {
        var data=new ObservationData{Hold=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        await using var shell=await HealthShell(new HealthSignIn(),new HealthClient(),linked:1,accountData:data);
        shell.SelectedAccount=HealthRow(shell,1);shell.RefreshPrivateDetails.Execute(null);await data.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if(locking)shell.Lock.Execute(null);else shell.Panic.Execute(null);
        Assert.True(data.LastToken.IsCancellationRequested);Assert.DoesNotContain("938572614",shell.PrivateAccountDetails);
        data.Hold.SetResult();await shell.RefreshPrivateDetails.Execution;
        Assert.Single(data.Calls);Assert.DoesNotContain("938572614",shell.PrivateAccountDetails);Assert.Contains(locking?"locked":"Panic",shell.Notice,StringComparison.OrdinalIgnoreCase);
        Assert.False(shell.IsAuthorizing);
    });
    [Fact]
    public Task PrivateReadCannotRepopulateDetailsAfterChangingAccounts()=>OnUi(async()=>
    {
        var data=new ObservationData{Hold=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        await using var shell=await HealthShell(new HealthSignIn(),new HealthClient(),linked:2,accountData:data);
        shell.SelectedAccount=HealthRow(shell,1);shell.RefreshPrivateDetails.Execute(null);await data.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        shell.SelectedAccount=HealthRow(shell,2);Assert.True(data.LastToken.IsCancellationRequested);
        data.Hold.SetResult();await shell.RefreshPrivateDetails.Execution;
        Assert.DoesNotContain("938572614",shell.PrivateAccountDetails);Assert.Equal(1002,shell.SelectedAccount!.KnownUserId);Assert.Single(data.Calls);
    });
    [Fact]
    public Task FriendshipReviewRequiresExplicitSameReviewAndPanicDiscardsIt()=>OnUi(async()=>
    {
        var data=new ObservationData();await using var shell=await HealthShell(new HealthSignIn(),new HealthClient(),linked:2,accountData:data);
        foreach(var row in shell.Accounts)row.IsChecked=true;
        var review=shell.CaptureFriendshipReview();Assert.NotNull(review);Assert.Contains("1001",review.Summary);Assert.Contains("1002",review.Summary);Assert.Empty(data.Calls);
        shell.BefriendReviewedAccounts.Execute(new AccountFriendshipReview(review.Batch,review.Summary));await shell.BefriendReviewedAccounts.Execution;Assert.Empty(data.Calls);
        shell.Panic.Execute(null);shell.BefriendReviewedAccounts.Execute(review);await shell.BefriendReviewedAccounts.Execution;Assert.Empty(data.Calls);
        var next=shell.CaptureFriendshipReview();Assert.NotNull(next);
        shell.BefriendReviewedAccounts.Execute(next);await shell.BefriendReviewedAccounts.Execution;
        Assert.Single(data.Calls);Assert.Contains("Already friends",shell.FriendshipResults);
        shell.BefriendReviewedAccounts.Execute(next);await shell.BefriendReviewedAccounts.Execution;Assert.Single(data.Calls);
    });
    [Fact]
    public Task ExitAwaitsUncooperativePrivateReadAndClearsItsLateResult()=>OnUi(async()=>
    {
        var data=new ObservationData{Hold=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        var shell=await HealthShell(new HealthSignIn(),new HealthClient(),linked:1,accountData:data);
        shell.SelectedAccount=HealthRow(shell,1);shell.RefreshPrivateDetails.Execute(null);await data.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disposal=shell.DisposeAsync().AsTask();Assert.False(disposal.IsCompleted);Assert.True(data.LastToken.IsCancellationRequested);
        data.Hold.SetResult();await disposal;Assert.DoesNotContain("938572614",shell.PrivateAccountDetails);Assert.Single(data.Calls);
    });
    [Fact]
    public Task DisconnectClearsPrivateDetailsForTheSameSelectedAccount()=>OnUi(async()=>
    {
        var data=new ObservationData();await using var shell=await HealthShell(new HealthSignIn(),new HealthClient(),linked:1,accountData:data);
        shell.SelectedAccount=HealthRow(shell,1);shell.RefreshPrivateDetails.Execute(null);await shell.RefreshPrivateDetails.Execution;
        Assert.Contains("938572614",shell.PrivateAccountDetails);
        shell.DisconnectAccount.Execute(null);await shell.DisconnectAccount.Execution;
        Assert.DoesNotContain("938572614",shell.PrivateAccountDetails);
    });
    [Fact]
    public Task CancellingFriendshipKeepsAnHonestPerPairReportInsteadOfUnderwayText()=>OnUi(async()=>
    {
        var data=new ObservationData{Hold=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        await using var shell=await HealthShell(new HealthSignIn(),new HealthClient(),linked:3,accountData:data);
        foreach(var row in shell.Accounts)row.IsChecked=true;
        var review=shell.CaptureFriendshipReview();Assert.NotNull(review);shell.BefriendReviewedAccounts.Execute(review);
        await data.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));shell.CancelAuthorization.Execute(null);data.Hold.SetResult();
        await shell.BefriendReviewedAccounts.Execution;
        Assert.DoesNotContain("underway",shell.FriendshipResults);Assert.Contains("Interrupted",shell.FriendshipResults);
        Assert.Equal(2,shell.FriendshipResults.Split("Not attempted").Length-1);Assert.Single(data.Calls);
    });
}
