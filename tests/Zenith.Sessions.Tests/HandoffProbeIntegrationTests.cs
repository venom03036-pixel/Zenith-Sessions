using System.Globalization;
using System.IO;
using System.Windows.Threading;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    private sealed class HandoffSignIn:IRobloxSessionSignIn
    {public Task<RobloxSessionCredential> SignInAsync(Guid id,CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromResult(new RobloxSessionCredential("synthetic-probe-original"));}}
    private sealed class HandoffIdentity:IRobloxSessionClient
    {
        public Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();return Task.FromResult(new RobloxSessionValidation(new(456,"probe_player",RobloxSessionIdentity.Issuer,456L.ToString(CultureInfo.InvariantCulture))));}
    }
    private sealed class HandoffBrowser:IRobloxHandoffProbe
    {
        public readonly TaskCompletionSource<RobloxHandoffProbeContext> Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<RobloxHandoffProbeCapture> Finish=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;public RobloxSessionCredential? Returned;
        public Task<RobloxHandoffProbeCapture> InspectAsync(RobloxHandoffProbeContext context,RobloxSessionCredential credential,CancellationToken ct)
        {Entered.TrySetResult(context);return Finish.Task;}
        public void Complete(RobloxHandoffProbeContext context)
        {
            var facts=RobloxHandoffProbePolicy.InspectExternal(context,RobloxHandoffProbePolicy.CreateGamePageUri(context),
                "https://www.roblox.com","roblox-player:1+launchmode:play+gameinfo:synthetic-ticket-never-expose+placelauncherurl:https%3A%2F%2Fwww.roblox.com%2F");
            Returned=new RobloxSessionCredential("synthetic-probe-current");Finish.TrySetResult(new(facts,Returned));
        }
        public ValueTask DisposeAsync(){Disposed=true;return ValueTask.CompletedTask;}
    }
    [Fact]
    public Task OfficialPageInspectionPinsProfileAndPlaceWithoutQueueingANativeLaunch()=>OnUi(async()=>
    {
        var probe=new HandoffBrowser();
        await using(var shell=new ShellViewModel(_root,true,Dispatcher.CurrentDispatcher,new Monitor(),signIn:new HandoffSignIn(),sessionClient:new HandoffIdentity(),handoffProbe:probe))
        {
            await shell.InitializeAsync();shell.SelectedAccount=shell.Accounts[0];
            shell.LinkAccount.Execute(null);await shell.LinkAccount.Execution;
            var selected=shell.SelectedAccount!;Assert.Equal(456,selected.KnownUserId);
            shell.ServerBrowser.UsePreferredPlace(42);
            shell.ServerBrowser.LinkInput="6564161046";Assert.False(shell.CanInspectNativeHandoff);
            shell.InspectNativeHandoff.Execute(null);await shell.InspectNativeHandoff.Execution;Assert.False(probe.Entered.Task.IsCompleted);
            shell.ServerBrowser.ResolveInput();Assert.True(shell.CanInspectNativeHandoff);
            shell.InspectNativeHandoff.Execute(null);var pinned=await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            try
            {
                Assert.Equal(selected.Profile.Id,pinned.ProfileId);Assert.Equal(456,pinned.ExpectedUserId);Assert.Equal(6564161046,pinned.PlaceId);
                Assert.True(shell.IsAuthorizing);Assert.False(shell.CanInspectNativeHandoff);
                shell.SelectedAccount=shell.Accounts.First(row=>row.Profile.Id!=selected.Profile.Id);shell.ServerBrowser.UsePreferredPlace(42);
                probe.Complete(pinned);await shell.InspectNativeHandoff.Execution;
                Assert.Contains("Place 6564161046",shell.HandoffInspectionStatus);Assert.Contains("captured and cancelled",shell.HandoffInspectionStatus);
                Assert.DoesNotContain("synthetic-ticket",shell.Notice);Assert.Empty(shell.Launches);Assert.False(shell.IsAuthorizing);
                Assert.Throws<ObjectDisposedException>(()=>probe.Returned!.CopyValue());
            }
            finally{probe.Finish.TrySetResult(new(new(RobloxHandoffProbeOutcome.Cancelled)));}
        }
        Assert.True(probe.Disposed);
    });
    [Fact]
    public Task InspectionAndCheckRecordTheirOwnDiagnosticKinds()=>OnUi(async()=>
    {
        // A support reader must be able to tell a new sign-in, a saved-sign-in check and a cancelled
        // handoff inspection apart on the Diagnostics page, in copied text, the report and manager.jsonl.
        var probe=new HandoffBrowser();
        await using var shell=new ShellViewModel(_root,true,Dispatcher.CurrentDispatcher,new Monitor(),signIn:new HandoffSignIn(),sessionClient:new HandoffIdentity(),handoffProbe:probe);
        await shell.InitializeAsync();shell.SelectedAccount=shell.Accounts[0];
        shell.LinkAccount.Execute(null);await shell.LinkAccount.Execution;
        shell.RefreshDiagnostics.Execute(null);
        int linked=shell.DiagnosticRows.Count(row=>row.Event.Kind==ManagerEventKind.AuthorizationLinked);
        Assert.Equal(1,linked);Assert.Equal(ManagerEventKind.AuthorizationLinked,shell.DiagnosticRows[^1].Event.Kind);

        shell.ServerBrowser.UsePreferredPlace(6564161046);
        shell.InspectNativeHandoff.Execute(null);var pinned=await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
        probe.Complete(pinned);await shell.InspectNativeHandoff.Execution;
        Assert.Contains("captured and cancelled",shell.HandoffInspectionStatus);
        shell.RefreshDiagnostics.Execute(null);
        Assert.Equal(linked,shell.DiagnosticRows.Count(row=>row.Event.Kind==ManagerEventKind.AuthorizationLinked));
        Assert.Equal(ManagerEventKind.HandoffInspected,shell.DiagnosticRows[^1].Event.Kind);
        Assert.Equal("Native handoff inspected and cancelled; no client started.",shell.DiagnosticRows[^1].Description);

        shell.RefreshAuthorization.Execute(null);await shell.RefreshAuthorization.Execution;
        Assert.Contains("Saved sign-in verified",shell.Notice);
        shell.RefreshDiagnostics.Execute(null);
        Assert.Equal(linked,shell.DiagnosticRows.Count(row=>row.Event.Kind==ManagerEventKind.AuthorizationLinked));
        Assert.Equal(ManagerEventKind.AuthorizationChecked,shell.DiagnosticRows[^1].Event.Kind);
        Assert.Equal("Saved sign-in re-verified and saved again.",shell.DiagnosticRows[^1].Description);
        Assert.Contains("Native handoff inspected and cancelled",shell.BuildVisibleDiagnosticText());
        Assert.Contains("\"AuthorizationChecked\"",shell.BuildDiagnosticReport());
    });
    [Theory]
    [InlineData(RobloxHandoffProbeOutcome.Cancelled,"HandoffNotCompleted")]
    [InlineData(RobloxHandoffProbeOutcome.Busy,"HandoffNotCompleted")]
    [InlineData(RobloxHandoffProbeOutcome.TimedOut,"HandoffNotCompleted")]
    [InlineData(RobloxHandoffProbeOutcome.OriginRejected,"OperationFailed")]
    public Task InspectionThatEndsWithoutAResultIsNotRecordedAsAnError(RobloxHandoffProbeOutcome outcome,string expectedKind)=>OnUi(async()=>
    {
        // Closing the private page, another owner holding the lane, or no handoff before the deadline is
        // inconclusive, not a failure (AGENTS.md: cancellation is not logged as an error). Real rejections stay errors.
        var probe=new HandoffBrowser();
        await using var shell=new ShellViewModel(_root,true,Dispatcher.CurrentDispatcher,new Monitor(),signIn:new HandoffSignIn(),sessionClient:new HandoffIdentity(),handoffProbe:probe);
        await shell.InitializeAsync();shell.SelectedAccount=shell.Accounts[0];
        shell.LinkAccount.Execute(null);await shell.LinkAccount.Execution;
        shell.ServerBrowser.UsePreferredPlace(6564161046);
        shell.RefreshDiagnostics.Execute(null);
        int errors=shell.DiagnosticRows.Count(row=>row.Level=="Error");long newest=shell.DiagnosticRows[^1].Sequence;

        shell.InspectNativeHandoff.Execute(null);await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
        probe.Finish.TrySetResult(new(new(outcome)));await shell.InspectNativeHandoff.Execution;
        Assert.False(shell.IsLocked);
        Assert.Equal(ShellViewModel.DescribeHandoffResult(new(outcome),6564161046),shell.HandoffInspectionStatus);

        shell.RefreshDiagnostics.Execute(null);
        var added=shell.DiagnosticRows.Where(row=>row.Sequence>newest).ToArray();
        Assert.NotEmpty(added);
        Assert.Equal(expectedKind,added[^1].Event.Kind.ToString());
        Assert.Equal(expectedKind=="OperationFailed"?errors+1:errors,shell.DiagnosticRows.Count(row=>row.Level=="Error"));
        Assert.DoesNotContain(added,row=>row.Event.Kind is ManagerEventKind.AuthorizationLinked or ManagerEventKind.HandoffInspected);
        if(expectedKind!="OperationFailed")
        {
            Assert.Equal("Info",added[^1].Level);
            Assert.DoesNotContain("fail",added[^1].Description,StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"HandoffNotCompleted\"",shell.BuildDiagnosticReport());
        }
    });
    [Fact]
    public Task LockFencesLateBrowserInspectionAndKeepsItsStopMessage()=>OnUi(async()=>
    {
        var probe=new HandoffBrowser();
        await using var shell=new ShellViewModel(_root,true,Dispatcher.CurrentDispatcher,new Monitor(),signIn:new HandoffSignIn(),sessionClient:new HandoffIdentity(),handoffProbe:probe);
        await shell.InitializeAsync();shell.SelectedAccount=shell.Accounts[0];shell.LinkAccount.Execute(null);await shell.LinkAccount.Execution;
        shell.ServerBrowser.UsePreferredPlace(6564161046);shell.InspectNativeHandoff.Execute(null);
        var pinned=await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
        try
        {
            shell.LockDashboard();Assert.False(shell.CanInspectNativeHandoff);Assert.True(shell.IsAuthorizing);
            Assert.False(shell.InspectNativeHandoff.Execution.IsCompleted);
            probe.Complete(pinned);await shell.InspectNativeHandoff.Execution;
            Assert.True(shell.IsLocked);Assert.Contains("stopped by Panic or Lock",shell.HandoffInspectionStatus);
            Assert.DoesNotContain("captured and cancelled",shell.Notice);Assert.Empty(shell.Launches);
            Assert.Throws<ObjectDisposedException>(()=>probe.Returned!.CopyValue());
        }
        finally{probe.Finish.TrySetResult(new(new(RobloxHandoffProbeOutcome.Cancelled)));}
    });
    [Fact]
    public Task CancelledOrInvalidBrowserProbeNeverCreatesAWebViewOrPrivateDirectory()=>OnUi(async()=>
    {
        await using var probe=new OwnedRobloxHandoffProbe(_root,Dispatcher.CurrentDispatcher);
        using var credential=new RobloxSessionCredential("synthetic-only");using var stop=new CancellationTokenSource();stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>probe.InspectAsync(new(Guid.NewGuid(),456,1,42),credential,stop.Token));
        using var invalid=await probe.InspectAsync(new(Guid.Empty,456,1,42),credential,default);
        Assert.Equal(RobloxHandoffProbeOutcome.ContextRejected,invalid.Result.Outcome);
        Assert.False(Directory.Exists(Path.Combine(_root,"signin-browser-data")));
    });
    [Fact]
    public Task RemovingTheInspectedProfileWaitsForItsActualBrowserOwner()=>OnUi(async()=>
    {
        var probe=new HandoffBrowser();
        await using var shell=new ShellViewModel(_root,true,Dispatcher.CurrentDispatcher,new Monitor(),signIn:new HandoffSignIn(),sessionClient:new HandoffIdentity(),handoffProbe:probe);
        await shell.InitializeAsync();shell.SelectedAccount=shell.Accounts[0];shell.LinkAccount.Execute(null);await shell.LinkAccount.Execution;
        var selected=shell.SelectedAccount!;shell.ServerBrowser.UsePreferredPlace(6564161046);
        shell.InspectNativeHandoff.Execute(null);var pinned=await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
        var removing=shell.DeleteAccountsAsync([selected]);
        try
        {
            Assert.False(removing.IsCompleted);Assert.Contains(shell.Accounts,row=>row.Profile.Id==selected.Profile.Id);
            probe.Complete(pinned);await removing;
            Assert.DoesNotContain(shell.Accounts,row=>row.Profile.Id==selected.Profile.Id);Assert.Empty(shell.Launches);
            Assert.Throws<ObjectDisposedException>(()=>probe.Returned!.CopyValue());
        }
        finally{probe.Finish.TrySetResult(new(new(RobloxHandoffProbeOutcome.Cancelled)));}
    });
}
