using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    [Fact]
    public Task BundleUsesSavedReferencesAfterLibraryRowsChangeAndKeepsLaunchGuards()=>OnUi(async()=>
    {
        await using var shell=new ShellViewModel(_root,true,Dispatcher.CurrentDispatcher,new Monitor());
        await shell.InitializeAsync();Assert.True(shell.IsReady,shell.Notice);
        Assert.True(shell.RecipientBundles.CanManage);
        shell.Accounts[0].IsChecked=true;shell.Accounts[1].IsChecked=true;
        var ids=shell.Accounts.Take(2).Select(row=>row.Profile.Id).ToArray();
        await shell.RecipientBundles.SaveCheckedAsync("Saved pair");
        shell.Refresh.Execute(null);await shell.Refresh.Execution;
        Assert.DoesNotContain(shell.Accounts,row=>row.IsChecked);
        shell.ServerBrowser.UsePreferredPlace(6564161046);
        Assert.True(shell.RecipientBundles.CanQueueSelected);
        await shell.RecipientBundles.QueueSelectedAsync();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while(shell.Launches.Count<2)await Task.Delay(5,deadline.Token);
        Assert.Equal(ids.Order(),shell.Launches.Select(row=>row.Operation.Intent.ProfileId).Order());
        Assert.All(shell.Launches,row=>
        {
            Assert.Equal(6564161046,row.Operation.Intent.PlaceId);Assert.True(row.Operation.Intent.HasExplicitDestination);
            Assert.Equal(LaunchState.Blocked,row.Operation.State);Assert.Equal(LaunchProblem.ProviderUnavailable,row.Operation.Problem);
            Assert.Null(row.Operation.ProcessReceipt);
        });
        Assert.Contains("2 blocked",shell.RecipientBundles.Status);
        shell.LockDashboard();Assert.Empty(shell.RecipientBundles.Bundles);Assert.Empty(shell.RecipientBundles.Members);
    });
    [Fact]
    public void StartedUnverifiedQueueRowExposesProcessReceiptAndRequiresReview()
    {
        var now=DateTimeOffset.UtcNow;var profile=Guid.NewGuid();
        var operation=new LaunchOperation(new(Guid.NewGuid(),profile,1,123,42,0,now,7),LaunchState.ClientStartedUnverified,
            LaunchProblem.ClientStartedUnverified,now,new(777,now.AddMinutes(-1).UtcTicks,now));
        var row=new LaunchRow(operation);
        Assert.Contains("unverified",row.State);Assert.Contains("PID 777",row.ProcessReceipt);
        Assert.False(row.CanCancel);Assert.True(row.CanReview);
        row.Update(operation with{State=LaunchState.Cancelled});Assert.True(row.CanReview);Assert.Contains("PID 777",row.ProcessReceipt);
        row.Update(operation with{State=LaunchState.Cancelled,Problem=LaunchProblem.ClientClosureAcknowledged});
        Assert.False(row.CanReview);Assert.Contains("PID 777",row.ProcessReceipt);
    }
}
