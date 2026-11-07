using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

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
    [Theory]
    [InlineData("throw")]
    [InlineData("invalid")]
    [InlineData("throw-then-stale")]
    public void OptionalSourceFailureIsReportedByTypeOnlyAndStillShowsFixedUnavailable(string failure)
    {
        var id=Guid.NewGuid();var types=new List<Type>();int checks=0;
        var source=new Source{Read=read=>failure=="invalid"?Snapshot(read) with {Stage=(NativeLaunchDiagnosticStage)999}
            :throw new InvalidOperationException("synthetic-secret-uri",new Exception("synthetic-cookie"))};
        var view=NativeLaunchDiagnosticPresentation.ReadCurrent(source,id,()=>failure!="throw-then-stale"||checks++==0,failed:types.Add);
        Assert.Equal(new[]{failure=="invalid"?typeof(ArgumentException):typeof(InvalidOperationException)},types);
        if(failure=="throw-then-stale"){Assert.Null(view);return;}
        Assert.NotNull(view);Assert.False(view.Available);Assert.Null(view.Snapshot);Assert.Equal(id,view.OperationId);
        // Z1357: the fixed text is the read-fault wording, not the absent-history wording.
        Assert.Equal(NativeLaunchDiagnosticPresentation.Faulted(id),view);Assert.True(view.ReadFailed);
        Assert.NotEqual(NativeLaunchDiagnosticPresentation.Unavailable(id).Summary,view.Summary);
        Assert.DoesNotContain("synthetic",view.ToString(),StringComparison.OrdinalIgnoreCase);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ValidOrEvictedReadsReportNoFailure(bool evicted)
    {
        var id=Guid.NewGuid();var types=new List<Type>();var source=new Source();if(evicted)source.Read=_=>null;
        var view=NativeLaunchDiagnosticPresentation.ReadCurrent(source,id,()=>true,failed:types.Add)!;
        Assert.Empty(types);Assert.Equal(!evicted,view.Available);
    }
    // Z1357: a read that throws, or returns a snapshot that is invalid or belongs to another
    // operation, is a provider fault. It must be reported by type and must not be explained to the
    // user as startup, eviction or never-recorded history.
    [Theory]
    [InlineData("throw")]
    [InlineData("invalid")]
    [InlineData("foreign")]
    public void SourceReadFaultIsReportedByTypeAndNotBlamedOnHistory(string failure)
    {
        var id=Guid.NewGuid();var types=new List<Type>();
        var source=new Source{Read=read=>failure switch
        {
            "throw"=>throw new InvalidOperationException("synthetic-secret-uri",new Exception("synthetic-cookie")),
            "invalid"=>Snapshot(read) with {UpdatedUtc=default},
            _=>Snapshot(Guid.NewGuid())
        }};
        var view=NativeLaunchDiagnosticPresentation.ReadCurrent(source,id,()=>true,failed:types.Add)!;
        Assert.Equal(new[]{failure switch{"throw"=>typeof(InvalidOperationException),"invalid"=>typeof(ArgumentException),_=>typeof(System.IO.InvalidDataException)}},types);
        Assert.False(view.Available);Assert.Null(view.Snapshot);Assert.Equal(id,view.OperationId);Assert.True(view.ReadFailed);
        Assert.Contains("unavailable",view.Summary);Assert.Contains("read fault",view.Summary);
        foreach(string history in new[]{"predate startup","evicted","never recorded"})Assert.DoesNotContain(history,view.Summary);
        Assert.DoesNotContain("Queue history",view.Times);
        Assert.DoesNotContain("synthetic",view.ToString(),StringComparison.OrdinalIgnoreCase);
    }
    [Theory]
    [InlineData("foreign")]
    [InlineData("timestamp")]
    public void ProjectShowsAPresentButUnusableSnapshotAsAFaultNotAbsentHistory(string invalid)
    {
        var id=Guid.NewGuid();var types=new List<Type>();
        var view=NativeLaunchDiagnosticPresentation.Project(id,invalid=="foreign"?Snapshot(Guid.NewGuid()):Snapshot(id) with {UpdatedUtc=default},types.Add);
        Assert.False(view.Available);Assert.Equal(id,view.OperationId);Assert.Contains("read fault",view.Summary);Assert.DoesNotContain("evicted",view.Summary);
        Assert.True(view.ReadFailed);Assert.Equal(new[]{invalid=="foreign"?typeof(System.IO.InvalidDataException):typeof(ArgumentException)},types);
        var absent=NativeLaunchDiagnosticPresentation.Project(id,null,types.Add);Assert.False(absent.ReadFailed);Assert.Single(types);
        Assert.NotEqual(absent,view);Assert.Contains("evicted",absent.Summary);
        Assert.False(NativeLaunchDiagnosticPresentation.Project(id,Snapshot(id),types.Add).ReadFailed);Assert.Single(types);
        Assert.False(NativeLaunchDiagnosticPresentation.Faulted(Guid.Empty).ReadFailed);
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
    [Fact]
    public Task ThrowingNativeDiagnosticSourceRecordsOneSecretFreeOperationFailedAcrossRepeatedRefreshes()=>OnUi(async()=>
    {
        await using var shell=DiagnosticShell();PublishDiagnosticQueue(shell,DiagnosticOperation());
        var diagnostics=(ManagerDiagnostics)typeof(ShellViewModel).GetField("_diagnostics",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(shell)!;
        long before=diagnostics.GetSnapshot().LatestSequence;bool fail=true;
        var source=new NativeLaunchDiagnosticPresentationTests.Source{Read=id=>fail
            ?throw new InvalidOperationException("synthetic-secret-uri",new Exception("synthetic-cookie")):NativeLaunchDiagnosticPresentationTests.Snapshot(id)};
        ManagerDiagnosticEvent[] Failures()=>diagnostics.GetSnapshot().Events.Where(e=>e.Sequence>before&&e.Kind==ManagerEventKind.OperationFailed).ToArray();
        for(int tick=0;tick<3;tick++)shell.RefreshSelectedNativeDiagnostics(source);
        var failure=Assert.Single(Failures());Assert.Equal("InvalidOperationException",failure.ErrorType);
        Assert.False(shell.SelectedNativeDiagnostics.Available);Assert.Contains("unavailable",shell.SelectedNativeDiagnostics.Summary);
        fail=false;shell.RefreshSelectedNativeDiagnostics(source);Assert.True(shell.SelectedNativeDiagnostics.Available);Assert.Single(Failures());
        fail=true;shell.RefreshSelectedNativeDiagnostics(source);shell.RefreshSelectedNativeDiagnostics(source);Assert.Equal(2,Failures().Length);
        shell.RefreshDiagnostics.Execute(null);
        Assert.Equal(2,shell.DiagnosticRows.Count(row=>row.Event.Kind==ManagerEventKind.OperationFailed&&row.ErrorType=="InvalidOperationException"));
        foreach(var row in shell.DiagnosticRows)
        {Assert.DoesNotContain("synthetic",row.Description,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("synthetic",row.ErrorType,StringComparison.OrdinalIgnoreCase);}
        Assert.DoesNotContain("synthetic",shell.BuildVisibleDiagnosticText(),StringComparison.OrdinalIgnoreCase);Assert.Empty(shell.Launches);
    });
    [Fact]
    public Task ForeignNativeDiagnosticSnapshotRecordsOneInvalidDataFailureAndIsNotShownAsHistory()=>OnUi(async()=>
    {
        await using var shell=DiagnosticShell();PublishDiagnosticQueue(shell,DiagnosticOperation());
        var diagnostics=(ManagerDiagnostics)typeof(ShellViewModel).GetField("_diagnostics",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(shell)!;
        long before=diagnostics.GetSnapshot().LatestSequence;int raised=0;
        shell.PropertyChanged+=(_,args)=>{if(args.PropertyName==nameof(shell.SelectedNativeDiagnostics))raised++;};
        var source=new NativeLaunchDiagnosticPresentationTests.Source{Read=_=>NativeLaunchDiagnosticPresentationTests.Snapshot(Guid.NewGuid())};
        ManagerDiagnosticEvent[] Failures()=>diagnostics.GetSnapshot().Events.Where(e=>e.Sequence>before&&e.Kind==ManagerEventKind.OperationFailed).ToArray();
        for(int tick=0;tick<3;tick++)shell.RefreshSelectedNativeDiagnostics(source);
        var failure=Assert.Single(Failures());Assert.Equal("InvalidDataException",failure.ErrorType);Assert.Equal(1,raised);
        var view=shell.SelectedNativeDiagnostics;Assert.False(view.Available);Assert.True(view.ReadFailed);Assert.Contains("read fault",view.Summary);Assert.DoesNotContain("evicted",view.Summary);
        source.Read=_=>null;shell.RefreshSelectedNativeDiagnostics(source);
        Assert.Equal(2,raised);Assert.False(shell.SelectedNativeDiagnostics.ReadFailed);Assert.Contains("evicted",shell.SelectedNativeDiagnostics.Summary);
        Assert.Single(Failures());Assert.Empty(shell.Launches);
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
