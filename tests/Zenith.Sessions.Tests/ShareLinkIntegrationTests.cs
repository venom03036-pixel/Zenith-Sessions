using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    // A signed-in resolve that waits for the test, then answers even when its token was cancelled: publication must be fenced.
    private sealed class HeldShareLinks:IRobloxShareLinkClient
    {
        public readonly TaskCompletionSource<CancellationToken> Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<RobloxShareLinkResolution> ResolveAsync(RobloxShareLink link,RobloxSessionCredential credential,CancellationToken ct)
        {Entered.TrySetResult(ct);await Release.Task;return new(RobloxShareLinkOutcome.PrivateServer,5,6,7,new ProtectedPrivateServerCode("98765432109876543210"));}
    }

    [Fact]
    public Task SelectingAnotherAccountOnJoinGameCancelsTheRunningShareLinkCheck()=>OnUi(async()=>
    {
        var links=new HeldShareLinks();var directory=new SearchDirectory(new());
        await using var shell=new ShellViewModel(_root,true,Dispatcher.CurrentDispatcher,new Monitor(),publicDirectory:directory,serverDirectory:new SearchServers(),
            gameDirectory:directory,signIn:new HandoffSignIn(),sessionClient:new HandoffIdentity(),shareLinkClient:links);
        await shell.InitializeAsync();shell.SelectedAccount=shell.Accounts[0];shell.LinkAccount.Execute(null);await shell.LinkAccount.Execution;
        var checker=shell.SelectedAccount!;Assert.Equal(456,checker.KnownUserId);
        shell.Navigate.Execute("JoinGame");
        shell.ShareLink.LinkInput="https://www.roblox.com/share?code=4b9c7e0ae7b2f84bb7a7d24f6d0f6c2e&type=Server";Task check=shell.ShareLink.CheckAsync();
        var token=await links.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            shell.SelectedAccount=shell.Accounts.First(row=>row.Profile.Id!=checker.Profile.Id);
            Assert.True(token.IsCancellationRequested);
            Assert.Equal("Share link check stopped because the selected account changed. Paste the link again to check it with the selected account.",shell.ShareLink.Status);
        }
        finally {links.Release.TrySetResult();await check.WaitAsync(TimeSpan.FromSeconds(5));}
        Assert.False(shell.ShareLink.HasResult);Assert.Equal("",shell.ShareLink.ResultDetail);
        await shell.ShareLink.ActiveOperation.WaitAsync(TimeSpan.FromSeconds(5)); // The new selection also reloads the saved list.
        Assert.False(shell.ShareLink.IsBusy);
        Assert.Equal("Share link check stopped because the selected account changed. Paste the link again to check it with the selected account.",shell.ShareLink.Status);
    });
}
