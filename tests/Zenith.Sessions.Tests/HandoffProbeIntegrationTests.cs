using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
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
        public bool Disposed;public RobloxSessionCredential? Returned;public RobloxHandoffProbeOutcome? Refusal{get;set;}
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
            shell.InspectNativeHandoff.Execute(null);var pinned=await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
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
        shell.InspectNativeHandoff.Execute(null);var pinned=await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
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

        shell.InspectNativeHandoff.Execute(null);await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
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
        var pinned=await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
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
    public Task InspectionBlockedByAnotherOwnersHeldFolderIsNotReportedAsMissingWebView2()=>OnUi(async()=>
    {
        // Stands in for a sign-in whose browser is still closing: its folder lease is held by another owner.
        var held=SignInProfileDirectory.Create(_root,Guid.NewGuid(),default);
        try
        {
            await using var probe=new OwnedRobloxHandoffProbe(_root,Dispatcher.CurrentDispatcher);
            using var credential=new RobloxSessionCredential("synthetic-only");
            using var result=await probe.InspectAsync(new(Guid.NewGuid(),456,1,42),credential,default);
            // Busy, not CleanupFailed: this inspection created nothing, so it must not poison a native launch owner either.
            Assert.Equal(RobloxHandoffProbeOutcome.Busy,result.Result.Outcome);
            Assert.True(Directory.Exists(held.Path),"Another owner's held sign-in folder was removed.");
        }
        finally{held.Remove(default);}
    });
    [Fact]
    public Task AnotherOwnersHeldFolderIsReportedAsARefusalBeforeAnyInspectionStarts()=>OnUi(async()=>
    {
        await using IRobloxHandoffProbe probe=new OwnedRobloxHandoffProbe(_root,Dispatcher.CurrentDispatcher);
        await using IRobloxNativeHandoffCapture native=new OwnedRobloxNativeHandoffCapture(_root,Dispatcher.CurrentDispatcher);
        await using IRobloxHandoffProbe elsewhere=new OwnedRobloxHandoffProbe(_root+"-other",Dispatcher.CurrentDispatcher);
        Assert.Null(probe.Refusal);Assert.Null(native.Refusal);
        // Stands in for a sign-in or latched native capture that still holds its folder lease.
        var held=SignInProfileDirectory.Create(_root,Guid.NewGuid(),default);
        try
        {
            Assert.Equal(RobloxHandoffProbeOutcome.Busy,probe.Refusal);Assert.Equal(RobloxHandoffProbeOutcome.Busy,native.Refusal);
            Assert.Null(elsewhere.Refusal);
        }
        finally{held.Remove(default);}
        Assert.Null(probe.Refusal);Assert.Null(native.Refusal);
    });
    [Fact]
    public Task FolderNeedingReviewLatchesTheProbeAsCleanupFailedInsteadOfBlamingWebView2()=>OnUi(async()=>
    {
        string foreign=Directory.CreateDirectory(Path.Combine(_root,"signin-browser-data","not-a-zenith-folder")).FullName;
        await using IRobloxHandoffProbe probe=new OwnedRobloxHandoffProbe(_root,Dispatcher.CurrentDispatcher);
        using var credential=new RobloxSessionCredential("synthetic-only");
        using(var result=await probe.InspectAsync(new(Guid.NewGuid(),456,1,42),credential,default))
        {
            Assert.Equal(RobloxHandoffProbeOutcome.CleanupFailed,result.Result.Outcome);
            // Unrecognized data survives a restart, so the status must name the folder to review, not only "close Zenith".
            string status=ShellViewModel.DescribeHandoffResult(result.Result,42);
            Assert.Contains("signin-browser-data",status);Assert.DoesNotContain("WebView2",status);
        }
        Assert.True(Directory.Exists(foreign),"Unrecognized data was not preserved for review.");
        // Later checks are refused before the caller reserves another account's grant.
        Assert.Equal(RobloxHandoffProbeOutcome.CleanupFailed,probe.Refusal);
        // The folder blocks every owner's admission, so the separate native capture refuses too instead of reserving first.
        await using IRobloxNativeHandoffCapture native=new OwnedRobloxNativeHandoffCapture(_root,Dispatcher.CurrentDispatcher);
        Assert.Equal(RobloxHandoffProbeOutcome.CleanupFailed,native.Refusal);
    });
    [Fact]
    public Task StorageFailureOutsideLeftoverDataIsRefusedAsStorageNotLatchedAsACleanupFailure()=>OnUi(async()=>
    {
        // Remote or unwritable storage says nothing about leftover data. It latched CleanupFailed: the status said cleanup did
        // not finish and to restart, which cannot fix the data folder, and native launch stopped until restart. Latching
        // nothing instead let every later check reserve its account's grant and fail the same way.
        var denied=Directory.CreateDirectory(Path.Combine(_root,"denied"));
        var deny=new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.CreateDirectories,AccessControlType.Deny);
        var acl=denied.GetAccessControl();acl.AddAccessRule(deny);denied.SetAccessControl(acl);
        const string remote=@"\\zenith-test.invalid\share\data";
        try
        {
            foreach(string dataRoot in new[]{remote,denied.FullName})
            {
                await using IRobloxHandoffProbe probe=new OwnedRobloxHandoffProbe(dataRoot,Dispatcher.CurrentDispatcher);
                // Admission refuses remote storage outright, so that is known before any grant is reserved.
                Assert.Equal(dataRoot==remote?(RobloxHandoffProbeOutcome?)RobloxHandoffProbeOutcome.StorageUnavailable:null,probe.Refusal);
                using var credential=new RobloxSessionCredential("synthetic-only");
                using(var result=await probe.InspectAsync(new(Guid.NewGuid(),456,1,42),credential,default))
                {
                    Assert.Equal(RobloxHandoffProbeOutcome.BrowserUnavailable,result.Result.Outcome);
                    string status=ShellViewModel.DescribeHandoffResult(result.Result,42);
                    Assert.Contains("data folder",status);Assert.DoesNotContain("cleanup",status,StringComparison.OrdinalIgnoreCase);
                    // A failure after the grant was reserved cost the account its saved sign-in, and the status must say so.
                    Assert.Contains("sign in again",status,StringComparison.OrdinalIgnoreCase);
                }
                // Refused as storage, not latched as cleanup: native launch is not poisoned, and no later check reserves a grant.
                Assert.Equal(RobloxHandoffProbeOutcome.StorageUnavailable,probe.Refusal);
                await using IRobloxNativeHandoffCapture native=new OwnedRobloxNativeHandoffCapture(dataRoot,Dispatcher.CurrentDispatcher);
                Assert.Equal(RobloxHandoffProbeOutcome.StorageUnavailable,native.Refusal);
                Assert.False(SignInProfileDirectory.LeftoverNeedsReview(dataRoot),"A storage failure was remembered as leftover data to review.");
                // A sweep that finds no sign-in folder tested no storage, so it does not lift the refusal.
                Assert.Equal(default,SignInProfileDirectory.SweepOwned(dataRoot,default));
                Assert.Equal(RobloxHandoffProbeOutcome.StorageUnavailable,probe.Refusal);
            }
            Assert.False(Directory.Exists(Path.Combine(denied.FullName,"signin-browser-data")));
        }
        finally{acl.RemoveAccessRuleSpecific(deny);denied.SetAccessControl(acl);}
    });
    [Fact]
    public Task StorageFailureRefusesTheNextChecksBeforeTheirGrantsAreReservedWithoutStoppingNativeLaunch()=>OnUi(async()=>
    {
        // A file in a leftover folder that another program still has open is a storage failure, not data to review. Nothing
        // remembered it, so each later check (or each account of a queued launch) reserved its grant, failed the same way and
        // lost its saved sign-in, and no account could be signed in again until storage was fixed.
        string leftover=Directory.CreateDirectory(Path.Combine(_root,"signin-browser-data",Guid.NewGuid().ToString("N")+"-"+Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(leftover,".zenith-private-signin"),"Zenith Sessions private sign-in profile v1");
        string data=Path.Combine(Directory.CreateDirectory(Path.Combine(leftover,"EBWebView")).FullName,"synthetic");File.WriteAllText(data,"test only");
        using var repository=new AccountRepository(_root);await repository.InitializeAsync(default);
        using var vault=new ProtectedCredentialVault(_root);
        var first=await repository.SaveAsync(AccountProfile.Create("First alias"),default);
        var inspected=await repository.SaveAsync(AccountProfile.Create("Inspected alias"),default);
        var prepared=await repository.SaveAsync(AccountProfile.Create("Prepared alias"),default);
        foreach(var (profile,user) in new[]{(first,455L),(inspected,456L),(prepared,457L)})
            await vault.SaveAsync(new(profile.Id,RobloxSessionIdentity.Provider,user,1,"","",DateTimeOffset.MaxValue,"old_player",AuthorizationHealth.Linked,
                RobloxSessionIdentity.Issuer,user.ToString(CultureInfo.InvariantCulture),"synthetic-probe-original"),default);
        await using var service=new RobloxSessionLinkService(repository,vault,new HandoffIdentity(),new HandoffSignIn());
        await using IRobloxHandoffProbe probe=new OwnedRobloxHandoffProbe(_root,Dispatcher.CurrentDispatcher);
        await using IRobloxNativeHandoffCapture native=new OwnedRobloxNativeHandoffCapture(_root,Dispatcher.CurrentDispatcher);
        using(new FileStream(data,FileMode.Open,FileAccess.Read,FileShare.None))
        {
            Assert.Null(probe.Refusal);
            // Nothing could tell the first check in advance: it reserved its grant, so that account must sign in again.
            var failed=await service.InspectHandoffAsync(first.Id,42,probe,default);
            Assert.Equal(RobloxHandoffProbeOutcome.BrowserUnavailable,failed.Outcome);
            Assert.Equal(AuthorizationHealth.SignInRequired,(await vault.ReadAsync(first.Id,default))!.Health);
            Assert.False(SignInProfileDirectory.LeftoverNeedsReview(_root),"A file still open was remembered as data to review.");
            // The next check is refused before its grant is reserved, and says the saved sign-in was kept.
            var inspection=await service.InspectHandoffAsync(inspected.Id,42,probe,default);
            Assert.Equal(RobloxHandoffProbeOutcome.StorageUnavailable,inspection.Outcome);await AssertStillLinked(inspected.Id);
            string status=ShellViewModel.DescribeHandoffResult(inspection,42);
            Assert.Contains("data folder",status);Assert.Contains("still saved",status);Assert.Contains("signin-browser-data",status);
            Assert.DoesNotContain("cleanup",status,StringComparison.OrdinalIgnoreCase);
            // Native preparation is refused the same way. It is not CleanupFailed, so the configured provider does not stop.
            var preparation=await service.PrepareNativeHandoffAsync(
                new(Guid.NewGuid(),prepared.Id,prepared.Revision,457,1818,0,DateTimeOffset.UtcNow,1,HasExplicitDestination:true),native,default);
            Assert.Equal(NativeHandoffPreparationStatus.StorageUnavailable,preparation.Status);Assert.Null(preparation.BrowserFacts);
            await AssertStillLinked(prepared.Id);
            Assert.True(File.Exists(data),"The file another program has open was removed.");
        }
        // Once the other program lets go, an admission that prepares its folder (such as the sign-in the first account now
        // needs, which reserves no saved grant) lifts the refusal.
        SignInProfileDirectory.Create(_root,Guid.NewGuid(),default).Remove(default);
        Assert.False(Directory.Exists(leftover),"The admission did not recover the released leftover.");
        Assert.Null(probe.Refusal);Assert.Null(native.Refusal);
        async Task AssertStillLinked(Guid id)
        {
            var saved=(await vault.ReadAsync(id,default))!;
            Assert.Equal(AuthorizationHealth.Linked,saved.Health);Assert.Equal(1,saved.Generation);
        }
    });
    [Fact]
    public Task LeftoverKeptForReviewAtStartupRefusesTheFirstChecksBeforeAnyGrantIsReserved()=>OnUi(async()=>
    {
        // An owned name holding browser data but no marker: older builds left exactly this, and it stays blocked for review.
        string leftover=Path.Combine(_root,"signin-browser-data",Guid.NewGuid().ToString("N")+"-"+Guid.NewGuid().ToString("N"));
        string data=Path.Combine(Directory.CreateDirectory(Path.Combine(leftover,"EBWebView")).FullName,"synthetic");File.WriteAllText(data,"test only");
        // Every start sweeps before the library is ready; it keeps this folder.
        Assert.Equal(new SignInSweepResult(0,1,KeptForReview:1),SignInProfileDirectory.SweepOwned(_root,default));
        using var repository=new AccountRepository(_root);await repository.InitializeAsync(default);
        using var vault=new ProtectedCredentialVault(_root);
        var inspected=await repository.SaveAsync(AccountProfile.Create("Inspected alias"),default);
        var prepared=await repository.SaveAsync(AccountProfile.Create("Prepared alias"),default);
        foreach(var (profile,user) in new[]{(inspected,456L),(prepared,457L)})
            await vault.SaveAsync(new(profile.Id,RobloxSessionIdentity.Provider,user,1,"","",DateTimeOffset.MaxValue,"old_player",AuthorizationHealth.Linked,
                RobloxSessionIdentity.Issuer,user.ToString(CultureInfo.InvariantCulture),"synthetic-probe-original"),default);
        await using var service=new RobloxSessionLinkService(repository,vault,new HandoffIdentity(),new HandoffSignIn());
        // Fresh owners, as after every restart: neither has latched anything in memory yet.
        await using IRobloxHandoffProbe probe=new OwnedRobloxHandoffProbe(_root,Dispatcher.CurrentDispatcher);
        await using IRobloxNativeHandoffCapture native=new OwnedRobloxNativeHandoffCapture(_root,Dispatcher.CurrentDispatcher);
        var inspection=await service.InspectHandoffAsync(inspected.Id,42,probe,default);
        await AssertStillLinked(inspected.Id);Assert.Equal(RobloxHandoffProbeOutcome.CleanupFailed,inspection.Outcome);
        var preparation=await service.PrepareNativeHandoffAsync(
            new(Guid.NewGuid(),prepared.Id,prepared.Revision,457,1818,0,DateTimeOffset.UtcNow,1,HasExplicitDestination:true),native,default);
        await AssertStillLinked(prepared.Id);
        // CleanupFailed, so the configured native provider still stops until restart; no capture was attempted.
        Assert.Equal(NativeHandoffPreparationStatus.CleanupFailed,preparation.Status);Assert.Null(preparation.BrowserFacts);
        Assert.True(File.Exists(data),"Leftover data kept for review was removed.");
        async Task AssertStillLinked(Guid id)
        {
            var saved=(await vault.ReadAsync(id,default))!;
            Assert.Equal(AuthorizationHealth.Linked,saved.Health);Assert.Equal(1,saved.Generation);
        }
    });
    [Fact]
    public Task RefusedInspectionKeepsTheSavedSignInAndALatchedProbeDisablesTheButton()=>OnUi(async()=>
    {
        var probe=new HandoffBrowser();
        await using(var shell=new ShellViewModel(_root,true,Dispatcher.CurrentDispatcher,new Monitor(),signIn:new HandoffSignIn(),sessionClient:new HandoffIdentity(),handoffProbe:probe))
        {
            await shell.InitializeAsync();shell.SelectedAccount=shell.Accounts[0];
            shell.LinkAccount.Execute(null);await shell.LinkAccount.Execution;
            var id=shell.SelectedAccount!.Profile.Id;long? linked=shell.SelectedAccount.AuthorizationGeneration;Assert.NotNull(linked);
            shell.ServerBrowser.LinkInput="6564161046";shell.ServerBrowser.ResolveInput();Assert.True(shell.CanInspectNativeHandoff);
            // A transient refusal (another browser still closing) is answered on click, not by disabling the button,
            // because nothing would re-enable it when that browser finishes closing.
            probe.Refusal=RobloxHandoffProbeOutcome.Busy;Assert.True(shell.CanInspectNativeHandoff);
            probe.Finish.TrySetResult(new(new(RobloxHandoffProbeOutcome.Busy)));
            shell.InspectNativeHandoff.Execute(null);await shell.InspectNativeHandoff.Execution.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(linked,shell.Accounts.Single(row=>row.Profile.Id==id).AuthorizationGeneration);
            Assert.False(probe.Entered.Task.IsCompleted);Assert.Contains("no page was opened",shell.HandoffInspectionStatus);
            probe.Refusal=RobloxHandoffProbeOutcome.CleanupFailed;Assert.False(shell.CanInspectNativeHandoff);
        }
    });
    [Fact]
    public Task WindowCancelledInspectionIsNotRecordedAsADiagnosticError()=>OnUi(async()=>
    {
        var probe=new HandoffBrowser();
        await using var shell=new ShellViewModel(_root,true,Dispatcher.CurrentDispatcher,new Monitor(),signIn:new HandoffSignIn(),sessionClient:new HandoffIdentity(),handoffProbe:probe);
        await shell.InitializeAsync();shell.SelectedAccount=shell.Accounts[0];shell.LinkAccount.Execute(null);await shell.LinkAccount.Execution;
        shell.ServerBrowser.UsePreferredPlace(6564161046);
        shell.RefreshDiagnostics.Execute(null);string before=shell.DiagnosticErrors;
        shell.InspectNativeHandoff.Execute(null);await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // The window's Cancel button or close box: the probe reports Cancelled while the shell's own stop token is untouched.
        probe.Finish.TrySetResult(new(new(RobloxHandoffProbeOutcome.Cancelled)));
        await shell.InspectNativeHandoff.Execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("Inspection cancelled",shell.HandoffInspectionStatus);
        // AGENTS.md: cancellation is expected, not an error. The shell's own Cancel already records nothing.
        shell.RefreshDiagnostics.Execute(null);Assert.Equal(before,shell.DiagnosticErrors);
    });
    // Owner-level tests: the real probe admits its folder, then waits behind a gated UI thread. Every case ends
    // before any window or WebView2 environment exists, so no browser, network, account or runtime is needed.
    [Fact]
    public async Task ProbeWaitingForItsWindowIsBusyThenCancelsAndRemovesItsFolder()
    {
        await using var ui=await GatedUiThread.StartAsync();
        await using var probe=new OwnedRobloxHandoffProbe(_root,ui.Dispatcher);
        using var credential=new RobloxSessionCredential("synthetic-only");using var stop=new CancellationTokenSource();
        var running=probe.InspectAsync(new(Guid.NewGuid(),456,1,42),credential,stop.Token);
        string folder=await GatedUiThread.WaitForOwnedFolderAsync(_root);
        using(var busy=await probe.InspectAsync(new(Guid.NewGuid(),456,1,42),credential,default))
            Assert.Equal(RobloxHandoffProbeOutcome.Busy,busy.Result.Outcome);
        stop.Cancel();ui.Release();
        using var result=await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RobloxHandoffProbeOutcome.Cancelled,result.Result.Outcome);
        Assert.False(Directory.Exists(folder),"A cancelled inspection left its private folder behind.");
    }
    [Fact]
    public async Task ProbeWhoseFolderCannotBeRemovedStaysBlockedAndKeepsTheFolderForRecovery()
    {
        await using var ui=await GatedUiThread.StartAsync();
        var probe=new OwnedRobloxHandoffProbe(_root,ui.Dispatcher);
        using var credential=new RobloxSessionCredential("synthetic-only");using var stop=new CancellationTokenSource();
        var running=probe.InspectAsync(new(Guid.NewGuid(),456,1,42),credential,stop.Token);
        string folder=await GatedUiThread.WaitForOwnedFolderAsync(_root);
        string cache=Directory.CreateDirectory(Path.Combine(folder,"cache")).FullName;
        using(new FileStream(Path.Combine(cache,"held"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
        {
            stop.Cancel();ui.Release();
            using var result=await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(RobloxHandoffProbeOutcome.CleanupFailed,result.Result.Outcome);
            using var latched=await probe.InspectAsync(new(Guid.NewGuid(),456,1,42),credential,default);
            Assert.Equal(RobloxHandoffProbeOutcome.CleanupFailed,latched.Result.Outcome);
            Assert.Equal(RobloxHandoffProbeOutcome.CleanupFailed,((IRobloxHandoffProbe)probe).Refusal);
            await probe.DisposeAsync();
        }
        Assert.True(File.Exists(Path.Combine(folder,".zenith-private-signin")),"The retained folder lost its ownership marker.");
        var next=SignInProfileDirectory.Create(_root,Guid.NewGuid(),default);
        Assert.False(Directory.Exists(folder),"The next admission did not recover the retained folder.");
        next.Remove(default);
    }
    // A probe whose cleanup failed keeps its folder and lease until it is disposed, when Zenith closes, so every other
    // owner's admission fails until then. Counted as a live owner, the native capture answered Busy ("still open or
    // closing ... retry") and a sign-in said to finish or cancel a window that no longer existed, for the whole session.
    [Fact]
    public async Task ProbeFolderKeptAfterAFailedCleanupIsReportedToOtherOwnersAsCleanupFailedNotBusy()
    {
        await using var ui=await GatedUiThread.StartAsync();
        var probe=new OwnedRobloxHandoffProbe(_root,ui.Dispatcher);
        try
        {
            using var credential=new RobloxSessionCredential("synthetic-only");using var stop=new CancellationTokenSource();
            var running=probe.InspectAsync(new(Guid.NewGuid(),456,1,42),credential,stop.Token);
            string folder=await GatedUiThread.WaitForOwnedFolderAsync(_root);
            string cache=Directory.CreateDirectory(Path.Combine(folder,"cache")).FullName;
            using(new FileStream(Path.Combine(cache,"held"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
            {
                stop.Cancel();ui.Release();
                using var result=await running.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(RobloxHandoffProbeOutcome.CleanupFailed,result.Result.Outcome);
            }
            // The other program has let go, but the probe keeps the folder until it is disposed.
            await using IRobloxNativeHandoffCapture native=new OwnedRobloxNativeHandoffCapture(_root,ui.Dispatcher);
            Assert.Equal(RobloxHandoffProbeOutcome.CleanupFailed,native.Refusal);
            await using var signIn=new OwnedRobloxSignIn(_root,ui.Dispatcher);
            var refused=await Assert.ThrowsAsync<AccountLinkException>(()=>signIn.SignInAsync(Guid.NewGuid(),default));
            Assert.Contains("Close Zenith Sessions",refused.Message);Assert.Contains("signin-browser-data",refused.Message);
            Assert.DoesNotContain("Finish or cancel",refused.Message);Assert.DoesNotContain(_root,refused.Message);
            Assert.False(signIn.CleanupPending,"A refusal that created nothing latched the sign-in owner.");
            Assert.True(Directory.Exists(folder),"Another owner's kept folder was opened or removed.");
            // Released, as when Zenith closes: nothing was latched elsewhere, and the next admission recovers the folder.
            await probe.DisposeAsync();
            Assert.Null(native.Refusal);
            SignInProfileDirectory.Create(_root,Guid.NewGuid(),default).Remove(default);
            Assert.False(Directory.Exists(folder),"The next admission did not recover the released folder.");
        }
        finally{await probe.DisposeAsync();}
    }
    [Fact]
    public async Task ProbeDeadlineIsReportedAsTimedOutAndRemovesItsFolder()
    {
        await using var ui=await GatedUiThread.StartAsync();
        var deadline=new ManualDeadline();
        await using var probe=new OwnedRobloxHandoffProbe(_root,ui.Dispatcher,deadline);
        using var credential=new RobloxSessionCredential("synthetic-only");
        var running=probe.InspectAsync(new(Guid.NewGuid(),456,1,42),credential,default);
        string folder=await GatedUiThread.WaitForOwnedFolderAsync(_root);
        Assert.Equal(TimeSpan.FromMinutes(3),deadline.Requested);
        await deadline.FireAsync();ui.Release();
        using var result=await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RobloxHandoffProbeOutcome.TimedOut,result.Result.Outcome);
        Assert.False(Directory.Exists(folder),"An expired inspection left its private folder behind.");
    }
    [Fact]
    public Task RemovingTheInspectedProfileWaitsForItsActualBrowserOwner()=>OnUi(async()=>
    {
        var probe=new HandoffBrowser();
        await using var shell=new ShellViewModel(_root,true,Dispatcher.CurrentDispatcher,new Monitor(),signIn:new HandoffSignIn(),sessionClient:new HandoffIdentity(),handoffProbe:probe);
        await shell.InitializeAsync();shell.SelectedAccount=shell.Accounts[0];shell.LinkAccount.Execute(null);await shell.LinkAccount.Execution;
        var selected=shell.SelectedAccount!;shell.ServerBrowser.UsePreferredPlace(6564161046);
        shell.InspectNativeHandoff.Execute(null);var pinned=await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
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
