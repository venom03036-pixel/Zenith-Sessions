using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    // A signed-in resolve that waits for the test, then answers even when its token was cancelled: publication must be fenced.
    private sealed class HeldShareLinks:IRobloxShareLinkClient
    {
        public readonly TaskCompletionSource<CancellationToken> Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<RobloxShareLinkResolution> ResolveAsync(RobloxShareLink link,RobloxSessionCredential credential,RobloxShareLinkTokenScope scope,CancellationToken ct)
        {Entered.TrySetResult(ct);await Release.Task;return new(RobloxShareLinkOutcome.PrivateServer,5,6,7,new ProtectedPrivateServerCode("98765432109876543210"));}
        public void ForgetTokens(Guid profileId){}
        public void ForgetAllTokens(){}
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
        var token=await links.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
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
    // The resolve endpoint behind the real RobloxShareLinkClient: one synthetic Valid private-server answer, never the network.
    private sealed class ShareLinkTransport(string json):HttpMessageHandler
    {
        public readonly List<string> Requests=[];public bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Requests.Add(request.Method+" "+request.RequestUri!.AbsoluteUri+" "+string.Join(";",request.Headers.GetValues("Cookie")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json"),RequestMessage=request});
        }
        protected override void Dispose(bool disposing){Disposed=true;base.Dispose(disposing);}
    }

    // The whole pasted-link chain at shell level: Find box → RouteShareLinkAsync → the linked account's saved session → the real
    // resolve client → the Private server tab → the default protected PrivateServerStore on the data root → clear on leave.
    [Fact]
    public Task PastedPrivateShareLinkRoutesToPrivateTabSavesThroughProtectedStoreAndClearsOnLeave()=>OnUi(async()=>
    {
        const string linkCode="98765432109876543210";
        var transport=new ShareLinkTransport("{\"experienceDetailsInviteData\":null,\"privateServerInviteData\":{\"status\":\"Valid\",\"placeId\":5,\"universeId\":6,\"ownerUserId\":7,\"linkCode\":\""+linkCode+"\"}}");
        var directory=new SearchDirectory(new());
        await using(var shell=new ShellViewModel(_root,true,Dispatcher.CurrentDispatcher,new Monitor(),publicDirectory:directory,serverDirectory:new SearchServers(),
            gameDirectory:directory,signIn:new HandoffSignIn(),sessionClient:new HandoffIdentity(),shareLinkClient:new RobloxShareLinkClient(transport)))
        {
            await shell.InitializeAsync();shell.SelectedAccount=shell.Accounts[0];shell.LinkAccount.Execute(null);await shell.LinkAccount.Execution;
            Assert.Equal(456,shell.SelectedAccount!.KnownUserId);
            ShowSearch(shell);await shell.ShareLink.ActiveOperation.WaitAsync(TimeSpan.FromSeconds(5)); // Opening Join a game reads the (empty) saved list.
            Assert.Empty(shell.ShareLink.Saved);Assert.True(shell.IsJoinFindStep);Assert.False(shell.IsPrivateServerTab);
            shell.ServerBrowser.UsePreferredPlace(42);var requested=shell.ServerBrowser.Destination;Assert.NotNull(requested);

            shell.ServerBrowser.LinkInput="https://www.roblox.com/share?code=4b9c7e0ae7b2f84bb7a7d24f6d0f6c2e&type=Server";
            await shell.DestinationSearch.SearchOrUseAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(shell.IsJoinServerStep);Assert.True(shell.IsPrivateServerTab);
            Assert.Equal("",shell.ServerBrowser.LinkInput);Assert.Empty(directory.Queries); // The share code never reached search.
            Assert.Equal("POST https://apis.roblox.com/sharelinks/v1/resolve-link .ROBLOSECURITY=synthetic-probe-original",Assert.Single(transport.Requests));
            Assert.True(shell.ShareLink.HasPrivateResult);Assert.Equal("Synthetic details · Place 5",shell.ShareLink.ResultTitle);
            Assert.Equal("Private server · Link code …3210 · Owner 7 · Universe 6",shell.ShareLink.ResultDetail);
            Assert.Same(requested,shell.ServerBrowser.Destination); // A private invitation leaves the requested destination unchanged.
            Assert.Empty(shell.Launches);

            shell.ShareLink.SaveName="Friends server";await shell.ShareLink.SaveAsFavoriteAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var saved=Assert.Single(shell.ShareLink.Saved);Assert.Equal("Friends server",saved.Name);Assert.Equal("3210",saved.Last4);
            using(var reopened=new PrivateServerStore(_root))
            {
                var stored=Assert.Single(await reopened.ReadAsync(default));
                Assert.Equal((saved.Id,5L,6L,7L,"Synthetic details"),(stored.Id,stored.PlaceId,stored.UniverseId,stored.OwnerUserId!.Value,stored.GameName));
                using var code=await reopened.ReadCodeAsync(saved.Id,default);string revealed="";code.Reveal(span=>revealed=new string(span));
                Assert.Equal(linkCode,revealed);
            }
            byte[] file=await File.ReadAllBytesAsync(Path.Combine(_root,"private-servers.dat"));
            Assert.Equal(-1,file.AsSpan().IndexOf(Encoding.ASCII.GetBytes(linkCode))); // Protected at rest, not written as text.

            shell.Navigate.Execute("Accounts"); // Leaving Join a game clears the private tab: result, code and saved list.
            Assert.False(shell.ShareLink.HasResult);Assert.Equal("",shell.ShareLink.ResultDetail);Assert.Empty(shell.ShareLink.Saved);

            shell.Navigate.Execute("JoinGame");await shell.ShareLink.ActiveOperation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(saved.Id,Assert.Single(shell.ShareLink.Saved).Id);Assert.False(shell.ShareLink.HasResult);
            Assert.Single(transport.Requests); // Reopening reads the saved list only; nothing is resolved again.
        }
        Assert.True(transport.Disposed); // The shell released the resolve client it was handed.
    });
}
