using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class NativeLaunchDiagnosticPresentationTests
{
    internal static readonly DateTimeOffset Recorded=new(2026,11,6,18,0,0,TimeSpan.Zero);
    internal static NativeLaunchDiagnosticSnapshot Snapshot(Guid operationId)=>new(operationId,7,Recorded.AddSeconds(-10),Recorded,
        NativeLaunchDiagnosticStage.Finished,NativeLaunchCleanupStatus.Complete,
        TrustStatus:ClientExecutableTrustStatus.VerifiedRobloxPublisher,TrustReason:ClientExecutableTrustReason.Verified,
        PreparationStatus:NativeHandoffPreparationStatus.Ready,BrowserOutcome:RobloxHandoffProbeOutcome.CapturedAndPrevented,
        ShapeStatus:NativeHandoffCaptureStatus.AcceptedSourceGrammar,DispatchStatus:NativeHandoffAttemptStatus.StartedUnverified,
        ExecutableObservedUtc:Recorded.AddSeconds(-9),PreparationObservedUtc:Recorded.AddSeconds(-3),BrowserObservedUtc:Recorded.AddSeconds(-4),
        DispatchObservedUtc:Recorded.AddSeconds(-2),CleanupObservedUtc:Recorded);

    [Fact]
    public void IndependentBrowserPreventionAndNativeStartKeepTheirOriginalTimesAndNeverGrantAuthority()
    {
        var id=Guid.NewGuid();var source=Snapshot(id);var view=NativeLaunchDiagnosticPresentation.Project(id,source);
        Assert.True(view.Available);Assert.Same(source,view.Snapshot);Assert.Contains("cancelled",view.Browser);Assert.Contains("separate stage",view.Browser);
        Assert.Contains("start was reported",view.Dispatch);Assert.Contains("unverified",view.Dispatch);Assert.Contains("no launch authority",view.Trust);
        Assert.Contains("18:00:00 UTC",view.Times);Assert.Contains("17:59:51 UTC",view.StageTimes);Assert.Contains("17:59:56 UTC",view.StageTimes);
        Assert.False(view.AccountVerified);Assert.False(view.ArrivalVerified);Assert.False(view.InputAuthorized);
        Assert.Equal(source,NativeLaunchDiagnosticPresentation.Project(id,source).Snapshot);
    }
    [Theory]
    [InlineData(ClientExecutableTrustReason.Unsigned,"signature was not established")]
    [InlineData(ClientExecutableTrustReason.RevocationUnavailable,"revocation evidence was unavailable")]
    [InlineData(ClientExecutableTrustReason.PublisherMismatch,"Roblox Corporation")]
    [InlineData(ClientExecutableTrustReason.FileChanged,"changed during verification")]
    public void FailedPublisherReasonsAreActionableRatherThanAFalseGenericSuccess(ClientExecutableTrustReason reason,string expected)
    {
        var id=Guid.NewGuid();var view=NativeLaunchDiagnosticPresentation.Project(id,Snapshot(id) with
        {TrustStatus=ClientExecutableTrustStatus.Rejected,TrustReason=reason,PreparationStatus=null,BrowserOutcome=null,ShapeStatus=null,DispatchStatus=null});
        Assert.True(view.Available);Assert.Contains(expected,view.Trust);Assert.Contains("not reached or not recorded",view.Preparation);
        Assert.Contains("not reached or not recorded",view.Dispatch);Assert.DoesNotContain("start was reported",view.Dispatch);
    }
    [Theory]
    [InlineData("foreign")]
    [InlineData("enum")]
    [InlineData("trust")]
    [InlineData("timestamp")]
    public void ForeignOrInvalidSnapshotsCannotBecomeSelectedEvidence(string invalid)
    {
        var id=Guid.NewGuid();var valid=Snapshot(id);var bad=invalid switch
        {"foreign"=>valid with {OperationId=Guid.NewGuid()},"enum"=>valid with {Stage=(NativeLaunchDiagnosticStage)999},
            "trust"=>valid with {TrustReason=ClientExecutableTrustReason.Unsigned},_=>valid with {UpdatedUtc=default}};
        var view=NativeLaunchDiagnosticPresentation.Project(id,bad);Assert.False(view.Available);Assert.Null(view.Snapshot);
        Assert.Contains("unavailable",view.Summary);Assert.Equal(id,view.OperationId);Assert.DoesNotContain("finished",view.Summary);
    }
    [Fact]
    public void AbsentHistoryAfterRestartOrEvictionStaysUnavailableAndNullDoesNotMeanSuccess()
    {
        var id=Guid.NewGuid();var view=NativeLaunchDiagnosticPresentation.Project(id,null);
        Assert.False(view.Available);Assert.Contains("predate startup",view.Summary);Assert.Contains("evicted",view.Summary);Assert.Contains("never recorded",view.Summary);
        Assert.Contains("not reached or not recorded",view.Dispatch);Assert.Contains("does not prove no client",view.Dispatch);Assert.Contains("unavailable",view.Cleanup);
        Assert.Contains("Select a launch request",NativeLaunchDiagnosticPresentation.Unavailable().Summary);
    }
    [Fact]
    public void StoppedUncertainAndFailedCleanupRemainVisibleTogetherWithoutUndoingAReceipt()
    {
        var id=Guid.NewGuid();var view=NativeLaunchDiagnosticPresentation.Project(id,Snapshot(id) with
        {Stage=NativeLaunchDiagnosticStage.Stopped,Stopped=true,DispatchStatus=NativeHandoffAttemptStatus.Uncertain,CleanupStatus=NativeLaunchCleanupStatus.Failed});
        Assert.Contains("cannot be recalled",view.Summary);Assert.Contains("uncertain",view.Dispatch);Assert.Contains("possible client",view.Dispatch);Assert.Contains("could not be confirmed",view.Cleanup);
        Assert.False(view.AccountVerified);Assert.False(view.ArrivalVerified);Assert.False(view.InputAuthorized);
    }
    [Fact]
    public void OptionalSourceFailureNeverExposesItsExceptionMessageOrInnerError()
    {
        var source=new Source{Read=_=>throw new InvalidOperationException("synthetic-secret-uri",new Exception("synthetic-cookie"))};
        var view=NativeLaunchDiagnosticPresentation.ReadCurrent(source,Guid.NewGuid(),()=>true)!;
        Assert.False(view.Available);Assert.DoesNotContain("synthetic",view.ToString(),StringComparison.OrdinalIgnoreCase);Assert.Null(view.Snapshot);
    }
    internal sealed class Source:INativeLaunchDiagnosticSource
    {
        public Func<Guid,NativeLaunchDiagnosticSnapshot?> Read=Snapshot;
        public readonly List<Guid> Requested=[];
        public NativeLaunchDiagnosticSnapshot? GetDiagnostic(Guid id){Requested.Add(id);return Read(id);}
    }
}

public sealed partial class ClientWindowIntegrationTests
{
    private ShellViewModel DiagnosticShell()=>new(_root,true,Dispatcher.CurrentDispatcher,new Monitor(),new SearchDirectory(new()),new SearchServers());
    private static LaunchOperation DiagnosticOperation()=>new(new(Guid.NewGuid(),Guid.NewGuid(),1,123,42,0,DateTimeOffset.UtcNow,1,
        HasExplicitDestination:true),LaunchState.Blocked,LaunchProblem.PreparationRejected,DateTimeOffset.UtcNow,ResourceBudget:new(1,1));
    private static void PublishDiagnosticQueue(ShellViewModel shell,params LaunchOperation[] operations)
    {
        typeof(ShellViewModel).GetField("_latestQueue",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(shell,new LaunchQueueSnapshot(true,operations));
        typeof(ShellViewModel).GetMethod("RefreshLaunchEvidence",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(shell,null);
    }
    [Fact]
    public Task DiagnosticRefreshReadsOnlyThePinnedSelectedOperationAndPreservesOriginalTimes()=>OnUi(async()=>
    {
        await using var shell=DiagnosticShell();PublishDiagnosticQueue(shell,Enumerable.Range(0,32).Select(_=>DiagnosticOperation()).ToArray());
        var selected=shell.LaunchEvidenceRows[^1];shell.SelectedLaunchEvidence=selected;var source=new NativeLaunchDiagnosticPresentationTests.Source();
        shell.RefreshSelectedNativeDiagnostics(source);Assert.Equal(new[]{selected.Id},source.Requested);Assert.Equal(selected.Id,shell.SelectedNativeDiagnostics.OperationId);
        Assert.Equal(NativeLaunchDiagnosticPresentationTests.Recorded,shell.SelectedNativeDiagnostics.Snapshot!.UpdatedUtc);
        var first=shell.SelectedNativeDiagnostics;shell.RefreshSelectedNativeDiagnostics(source);Assert.Equal(first,shell.SelectedNativeDiagnostics);
        Assert.Equal(32,shell.LaunchEvidenceRows.Count);Assert.Empty(shell.Launches);Assert.False(shell.SelectedNativeDiagnostics.ArrivalVerified);
    });
    [Theory]
    [InlineData("selection")]
    [InlineData("lock")]
    [InlineData("panic")]
    public Task ReentrantSelectionLockOrPanicDuringTheSourceReadCannotPublishTheOldSnapshot(string change)=>OnUi(async()=>
    {
        await using var shell=DiagnosticShell();PublishDiagnosticQueue(shell,DiagnosticOperation(),DiagnosticOperation());
        var first=shell.LaunchEvidenceRows[0];var other=shell.LaunchEvidenceRows[1];shell.SelectedLaunchEvidence=first;
        var source=new NativeLaunchDiagnosticPresentationTests.Source{Read=id=>
        {
            if(change=="selection")shell.SelectedLaunchEvidence=other;else if(change=="lock")shell.LockDashboard();else shell.Panic.Execute(null);
            return NativeLaunchDiagnosticPresentationTests.Snapshot(id);
        }};
        shell.RefreshSelectedNativeDiagnostics(source);Assert.False(shell.SelectedNativeDiagnostics.Available);Assert.Null(shell.SelectedNativeDiagnostics.Snapshot);
        if(change=="selection"){Assert.Same(other,shell.SelectedLaunchEvidence);Assert.Equal(other.Id,shell.SelectedNativeDiagnostics.OperationId);}
        else if(change=="lock"){Assert.True(shell.IsLocked);Assert.Null(shell.SelectedNativeDiagnostics.OperationId);Assert.Empty(shell.LaunchEvidenceRows);}
        else{Assert.Same(first,shell.SelectedLaunchEvidence);Assert.Contains("Panic",shell.Notice);}
        Assert.Single(source.Requested);Assert.Empty(shell.Launches);
    });
    [Fact]
    public Task ReportFormatTwoAllowsOnlyFixedStagesAndAuthorityFlagsAndDoesNotSerializeThePresentationObject()=>OnUi(async()=>
    {
        await using var shell=DiagnosticShell();var operation=DiagnosticOperation();PublishDiagnosticQueue(shell,operation);
        string report=shell.BuildNativeTrialEvidenceReport();using var document=JsonDocument.Parse(report);var root=document.RootElement;
        Assert.Equal(2,root.GetProperty("FormatVersion").GetInt32());var stage=root.GetProperty("NativeDiagnostics");Assert.False(stage.GetProperty("Available").GetBoolean());
        Assert.Equal(operation.Intent.OperationId,stage.GetProperty("OperationId").GetGuid());Assert.Equal(JsonValueKind.Null,stage.GetProperty("TrustReason").ValueKind);
        Assert.Equal(JsonValueKind.Null,stage.GetProperty("DispatchStatus").ValueKind);Assert.Equal(JsonValueKind.Null,stage.GetProperty("UpdatedUtc").ValueKind);
        foreach(var item in new[]{root,stage})foreach(string flag in new[]{"AccountVerified","ArrivalVerified","InputAuthorized"})Assert.False(item.GetProperty(flag).GetBoolean());
        foreach(string forbidden in new[]{"Snapshot","ExecutablePath","FileId","Signer","SessionCookie","RefreshToken","roblox-player:","gameinfo=","ProfileId",_root})Assert.DoesNotContain(forbidden,report,StringComparison.OrdinalIgnoreCase);
        Assert.InRange(report.Length,1,65536);
    });

    [Fact]
    public Task ActualConfiguredProviderStageHistoryReachesOnlyItsOriginalSelectedReportAsAnAllowlistedSnapshot()=>OnUi(async()=>
    {
        var child=new NativeEvidenceChild{DiagnosticFactory=NativeLaunchDiagnosticPresentationTests.Snapshot};
        await using var shell=await NativeEvidenceShellAsync(child,new NativeEvidenceMonitor());shell.StartNativeTrial.Execute(Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CaptureNativeTrialReview()));await shell.StartNativeTrial.Execution;
        var selected=await NativeEvidenceStartedAsync(shell);shell.Refresh.Execute(null);await shell.Refresh.Execution;
        var diagnostic=shell.SelectedNativeDiagnostics;Assert.True(diagnostic.Available);Assert.Equal(selected.Id,diagnostic.OperationId);
        Assert.Equal(NativeLaunchDiagnosticPresentationTests.Recorded,diagnostic.Snapshot!.DispatchObservedUtc!.Value.AddSeconds(2));
        string report=shell.BuildNativeTrialEvidenceReport();AssertNativeEvidenceReport(report,selected.Id);using var json=JsonDocument.Parse(report);
        var stage=json.RootElement.GetProperty("NativeDiagnostics");Assert.True(stage.GetProperty("Available").GetBoolean());Assert.Equal(selected.Id,stage.GetProperty("OperationId").GetGuid());
        Assert.Equal("StartedUnverified",stage.GetProperty("DispatchStatus").GetString());Assert.Equal("CapturedAndPrevented",stage.GetProperty("BrowserOutcome").GetString());
        Assert.Equal(NativeLaunchDiagnosticPresentationTests.Recorded.AddSeconds(-2),stage.GetProperty("DispatchObservedUtc").GetDateTimeOffset());
        Assert.False(stage.GetProperty("AccountVerified").GetBoolean());Assert.False(stage.GetProperty("ArrivalVerified").GetBoolean());Assert.False(stage.GetProperty("InputAuthorized").GetBoolean());
    });
    [Theory]
    [InlineData("selection")]
    [InlineData("lock")]
    [InlineData("panic")]
    public Task ReportRefreshCannotExportAfterReentrantSelectionLockOrPanic(string change)=>OnUi(async()=>
    {
        await using var shell=DiagnosticShell();PublishDiagnosticQueue(shell,DiagnosticOperation(),DiagnosticOperation());var other=shell.LaunchEvidenceRows[1];
        shell.RefreshSelectedNativeDiagnostics(new NativeLaunchDiagnosticPresentationTests.Source());Assert.True(shell.SelectedNativeDiagnostics.Available);
        bool armed=true;shell.PropertyChanged+=(_,args)=>
        {
            if(!armed||args.PropertyName!=nameof(shell.SelectedNativeDiagnostics))return;armed=false;
            if(change=="selection")shell.SelectedLaunchEvidence=other;else if(change=="lock")shell.LockDashboard();else shell.Panic.Execute(null);
        };
        var error=Assert.Throws<InvalidOperationException>(()=>shell.BuildNativeTrialEvidenceReport());Assert.False(armed);Assert.Contains("changed",error.Message);
        Assert.False(shell.SelectedNativeDiagnostics.Available);Assert.Empty(shell.Launches);
        if(change=="lock")Assert.True(shell.IsLocked);else if(change=="selection")Assert.Same(other,shell.SelectedLaunchEvidence);else Assert.Contains("Panic",shell.Notice);
    });
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ExportStopsBeforeFileCreationAndPreservesTheReentrantStopNotice(bool panic)=>OnUi(async()=>
    {
        await using var shell=DiagnosticShell();PublishDiagnosticQueue(shell,DiagnosticOperation());shell.RefreshSelectedNativeDiagnostics(new NativeLaunchDiagnosticPresentationTests.Source());
        using var output=new NativeEvidenceExportFolder();string path=System.IO.Path.Combine(output.Path,"never-written.json");bool armed=true;string? notice=null;
        shell.PropertyChanged+=(_,args)=>
        {
            if(!armed||args.PropertyName!=nameof(shell.SelectedNativeDiagnostics))return;armed=false;
            if(panic)shell.Panic.Execute(null);else shell.LockDashboard();notice=shell.Notice;
        };
        shell.ExportLaunchEvidence.Execute(path);await shell.ExportLaunchEvidence.Execution;Assert.False(armed);Assert.Equal(notice,shell.Notice);
        Assert.False(System.IO.File.Exists(path));Assert.Empty(System.IO.Directory.GetFiles(output.Path,"*.tmp"));Assert.False(shell.SelectedNativeDiagnostics.Available);
    });
}
