using System.IO;
using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    [Fact]
    public Task ThrowingSignedServerCancellationCannotRetainOtherPrivateAccountObservations()=>OnUi(async()=>
    {
        var servers=new ServerData{Hold=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        await using var shell=await HealthShell(new HealthSignIn(),new HealthClient(),1,accountData:new ObservationData(),signedServers:servers);
        shell.SelectedAccount=HealthRow(shell,1);shell.ServerBrowser.UsePreferredPlace(123);
        shell.RefreshPrivateDetails.Execute(null);await shell.RefreshPrivateDetails.Execution;
        Assert.Contains("938572614",shell.PrivateAccountDetails);
        shell.BrowseSignedServers.Execute("OwnedPrivate");await servers.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var registration=servers.LastToken.Register(()=>throw new InvalidOperationException("Synthetic cancellation callback"));
        try
        {
            var clear=typeof(Zenith.Sessions.App.ShellViewModel).GetMethod("ClearPrivateAccountObservations",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
            Assert.Null(Record.Exception(()=>clear.Invoke(shell,null)));
            Assert.DoesNotContain("938572614",shell.PrivateAccountDetails);
            Assert.Empty(shell.OwnedPrivateServers);
        }
        finally{servers.Hold.SetResult();await shell.BrowseSignedServers.Execution;}
    });
    private sealed class ServerData : IRobloxSignedInServerClient
    {
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Hold;
        public CancellationToken LastToken;
        public int Browses,Renames;
        public string? SeenCookie;
        public string? Cursor;
        public string? NextCursor="synthetic-next";
        public async Task<SignedInServerResponse> BrowseAsync(SignedInServerQuery query,long user,RobloxSessionCredential cookie,CancellationToken ct)
        {
            Browses++;SeenCookie=cookie.CopyValue();Cursor=query.Cursor;LastToken=ct;Entered.TrySetResult();
            if(Hold is not null)await Hold.Task;
            return new(RobloxAccountOutcome.Success,query.Order==SignedInServerOrder.OwnedPrivate
                ?new([],[new(99,query.PlaceId,user,"Synthetic private name",1,10)],NextCursor)
                :new([new(query.PlaceId,"8c782b5d-2121-4bfa-9dca-435b63316b05",1,10,null,15,DateTimeOffset.UtcNow,2)],[],NextCursor));
        }
        public Task<SignedInServerResponse> RenameAsync(OwnedPrivateServer server,string name,RobloxSessionCredential cookie,CancellationToken ct)
        {Renames++;return Task.FromResult(new SignedInServerResponse(RobloxAccountOutcome.Success));}
    }
    [Fact]
    public Task SignedServerPagesAreExplicitReplaceOnePageAndPinOnlyAfterUse()=>OnUi(async()=>
    {
        var data=new ServerData();await using var shell=await HealthShell(new HealthSignIn(),new HealthClient(),1,signedServers:data);
        shell.SelectedAccount=HealthRow(shell,1);shell.ServerBrowser.UsePreferredPlace(123);
        shell.Accounts.CollectionChanged+=(_,args)=>{if(args.Action==System.Collections.Specialized.NotifyCollectionChangedAction.Reset)shell.SelectedAccount=null;};
        Assert.Equal(0,data.Browses);Assert.True(shell.CanBrowseSignedServers);
        shell.BrowseSignedServers.Execute("Recommended");await shell.BrowseSignedServers.Execution;
        Assert.Equal(HealthCookie(1),data.SeenCookie);Assert.Single(shell.SignedPublicServers);Assert.True(shell.CanNextSignedServers);
        Assert.Null(shell.ServerBrowser.Destination!.JobId);
        shell.SelectedSignedServer=shell.SignedPublicServers.Single();shell.UseSignedServer.Execute(null);
        Assert.Equal(shell.SelectedSignedServer.JobId,shell.ServerBrowser.Destination!.JobId);Assert.Empty(shell.Launches);
        data.NextCursor="next-two";shell.NextSignedServers.Execute(null);await shell.NextSignedServers.Execution;
        Assert.Equal("synthetic-next",data.Cursor);Assert.Equal(2,data.Browses);Assert.Single(shell.SignedPublicServers);
    });
    [Fact]
    public Task OwnedRenameRequiresTheExactUnchangedSingleUseReviewAndClearsPrivateDataOnAccountChange()=>OnUi(async()=>
    {
        var data=new ServerData();await using var shell=await HealthShell(new HealthSignIn(),new HealthClient(),2,signedServers:data);
        shell.SelectedAccount=HealthRow(shell,1);shell.ServerBrowser.UsePreferredPlace(123);
        shell.BrowseSignedServers.Execute("OwnedPrivate");await shell.BrowseSignedServers.Execution;
        shell.SelectedOwnedServer=shell.OwnedPrivateServers.Single();shell.PrivateServerName="New name";
        var review=shell.CapturePrivateServerRenameReview();Assert.NotNull(review);
        shell.RenameOwnedPrivateServer.Execute(review with{});await shell.RenameOwnedPrivateServer.Execution;Assert.Equal(0,data.Renames);
        review=shell.CapturePrivateServerRenameReview();shell.RenameOwnedPrivateServer.Execute(review);await shell.RenameOwnedPrivateServer.Execution;
        Assert.Equal(1,data.Renames);Assert.Contains("accepted",shell.SignedServerStatus);Assert.Contains("Fetch",shell.SignedServerStatus);
        shell.RenameOwnedPrivateServer.Execute(review);await shell.RenameOwnedPrivateServer.Execution;Assert.Equal(1,data.Renames);
        foreach(string path in Directory.EnumerateFiles(_root,"*.json",SearchOption.AllDirectories))Assert.DoesNotContain("Synthetic private name",await File.ReadAllTextAsync(path));
        shell.SelectedAccount=HealthRow(shell,2);Assert.Empty(shell.OwnedPrivateServers);Assert.Empty(shell.PrivateServerName);Assert.False(shell.CanNextSignedServers);
    });
    [Theory]
    [InlineData("Panic")]
    [InlineData("Lock")]
    [InlineData("Selection")]
    [InlineData("Deselect")]
    [InlineData("Place")]
    [InlineData("Clear")]
    public Task LateSignedPagesNeverRepopulateAfterTheirOwnerChanges(string action)=>OnUi(async()=>
    {
        var data=new ServerData{Hold=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        await using var shell=await HealthShell(new HealthSignIn(),new HealthClient(),2,signedServers:data);
        shell.SelectedAccount=HealthRow(shell,1);shell.ServerBrowser.UsePreferredPlace(123);
        shell.BrowseSignedServers.Execute("OwnedPrivate");await data.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        switch(action){case "Panic":shell.Panic.Execute(null);break;case "Lock":shell.Lock.Execute(null);break;case "Selection":shell.SelectedAccount=HealthRow(shell,2);break;case "Deselect":shell.SelectedAccount=null;break;case "Place":shell.ServerBrowser.UsePreferredPlace(999);break;default:shell.ClearSignedServers.Execute(null);break;}
        Assert.True(data.LastToken.IsCancellationRequested);data.Hold.SetResult();await shell.BrowseSignedServers.Execution;
        Assert.Empty(shell.OwnedPrivateServers);Assert.False(shell.CanNextSignedServers);Assert.False(shell.IsAuthorizing);
    });
    [Fact]
    public Task SignedServerExitWaitsForIgnoredCancellationAndDiscardsPrivateResult()=>OnUi(async()=>
    {
        var data=new ServerData{Hold=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        var shell=await HealthShell(new HealthSignIn(),new HealthClient(),1,signedServers:data);
        shell.SelectedAccount=HealthRow(shell,1);shell.ServerBrowser.UsePreferredPlace(123);
        shell.BrowseSignedServers.Execute("OwnedPrivate");await data.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var exit=shell.DisposeAsync().AsTask();Assert.False(exit.IsCompleted);Assert.True(data.LastToken.IsCancellationRequested);
        data.Hold.SetResult();await exit;Assert.Empty(shell.OwnedPrivateServers);Assert.False(shell.IsAuthorizing);
    });
    [Fact]
    public Task RepeatedSignedCursorStopsWithoutReplacingTheLastCompletePage()=>OnUi(async()=>
    {
        var data=new ServerData();await using var shell=await HealthShell(new HealthSignIn(),new HealthClient(),1,signedServers:data);
        shell.SelectedAccount=HealthRow(shell,1);shell.ServerBrowser.UsePreferredPlace(123);
        shell.BrowseSignedServers.Execute("OwnedPrivate");await shell.BrowseSignedServers.Execution;
        var row=shell.OwnedPrivateServers.Single();shell.NextSignedServers.Execute(null);await shell.NextSignedServers.Execution;
        Assert.Contains("repeated",shell.SignedServerStatus);Assert.Same(row,shell.OwnedPrivateServers.Single());Assert.False(shell.CanNextSignedServers);
    });
}
