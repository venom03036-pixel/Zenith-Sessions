using System.Collections;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class OverviewWorkspaceProjectionTests
{
    private static readonly DateTimeOffset Now=new(2026,11,6,18,0,0,TimeSpan.Zero);
    [Fact]
    public void LibraryHeadlineUsesFullDatedTotalAndMasksUnopenedLibrary()
    {
        var input=Input() with {SavedProfileCount=10_000,LibraryLoadedUtc=Now.AddMinutes(-5)};
        var view=OverviewWorkspaceProjection.Create(input,Now);
        Assert.Equal(10_000,view.SavedProfiles);Assert.Equal(input.LibraryLoadedUtc,view.LibraryLoadedUtc);
        Assert.Contains("entire saved library",view.LibraryCaption);Assert.Contains("Last loaded",view.LibraryCaption);
        Assert.Null(OverviewWorkspaceProjection.Create(input with {LibraryReady=false},Now).SavedProfiles);
        Assert.Null(OverviewWorkspaceProjection.Create(input with {LibraryLoadedUtc=Now.AddSeconds(2)},Now).SavedProfiles);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void EmptyProcessScanIsZeroOnlyWhenComplete(bool complete)
    {
        var input=Input() with {Desktop=Desktop([]) with {ProcessInventoryComplete=complete}};
        var view=OverviewWorkspaceProjection.Create(input,Now);
        if(complete) Assert.Equal(0,view.ObservedProcesses);else {Assert.Null(view.ObservedProcesses);Assert.Contains("0 rows reported",view.ProcessCaption);}
        Assert.DoesNotContain("joined accounts",view.ProcessCaption);
    }
    [Theory]
    [InlineData("no-time")] [InlineData("default-time")] [InlineData("future-scan")] [InlineData("stale-scan")]
    [InlineData("legacy-revision")] [InlineData("partial")] [InlineData("problem")] [InlineData("row-default")]
    [InlineData("row-future")] [InlineData("row-after-scan")] [InlineData("row-stale")] [InlineData("start-after-row")]
    [InlineData("huge-start")] [InlineData("row-problem")] [InlineData("duplicate-pid")] [InlineData("duplicate-pid-reuse")]
    [InlineData("duplicate-window")]
    public void InvalidIncompleteOrAmbiguousProcessEvidenceNeverBecomesACurrentTotal(string bad)
    {
        var row=Row(1);var desktop=Desktop([row]);
        desktop=bad switch
        {
            "no-time"=>desktop with {ProcessObservedUtc=null},"default-time"=>desktop with {ProcessObservedUtc=default(DateTimeOffset)},
            "future-scan"=>desktop with {ProcessObservedUtc=Now.AddSeconds(2)},"stale-scan"=>desktop with {ProcessObservedUtc=Now.AddSeconds(-11)},
            "legacy-revision"=>desktop with {ProcessRevision=0},"partial"=>desktop with {ProcessInventoryComplete=false},"problem"=>desktop with {Problem="Synthetic scan failure."},
            "row-default"=>desktop with {Sessions=new[]{row with {ObservedUtc=default}}},"row-future"=>desktop with {Sessions=new[]{row with {ObservedUtc=Now.AddSeconds(2)}}},
            "row-after-scan"=>desktop with {ProcessObservedUtc=Now.AddSeconds(-1)},"row-stale"=>desktop with {Sessions=new[]{row with {ObservedUtc=Now.AddSeconds(-11)}}},
            "start-after-row"=>desktop with {Sessions=new[]{row with {ProcessStartUtcTicks=Now.AddSeconds(1).UtcTicks}}},
            "huge-start"=>desktop with {Sessions=new[]{row with {ProcessStartUtcTicks=long.MaxValue}}},"row-problem"=>desktop with {Sessions=new[]{row with {Problem="Unavailable."}}},
            "duplicate-pid"=>desktop with {Sessions=new[]{row,row}},"duplicate-pid-reuse"=>desktop with {Sessions=new[]{row,row with {ProcessStartUtcTicks=row.ProcessStartUtcTicks+1,Window=102}}},
            _=>desktop with {Sessions=new[]{row,Row(2) with {Window=row.Window}}}
        };
        var view=OverviewWorkspaceProjection.Create(Input() with {Desktop=desktop},Now);
        Assert.Null(view.ObservedProcesses);Assert.Contains("total unknown",view.ProcessCaption);Assert.Contains("do not prove absence",view.ProcessCaption);
        Assert.Equal(desktop.ProcessObservedUtc,view.ProcessObservedUtc);
    }
    [Theory]
    [InlineData("windowless")] [InlineData("minimized")] [InlineData("hidden")]
    public void ObservedProcessCountsDoNotRequireCaptureEligibility(string kind)
    {
        var row=Row(1);row=kind switch {"windowless"=>row with {Window=0},"minimized"=>row with {Minimized=true},_=>row with {Visible=false}};
        var view=OverviewWorkspaceProjection.Create(Input() with {Desktop=Desktop([row])},Now);
        Assert.Equal(1,view.ObservedProcesses);Assert.Contains("Account and arrival unverified",view.ProcessCaption);
    }
    [Fact]
    public void ResourceOnlyRefreshNeverRenewsTheOriginalCachedProcessEvidence()
    {
        var at=Now.AddSeconds(-9);var desktop=Desktop([Row(1) with {ObservedUtc=at}]) with {ProcessObservedUtc=at};
        var first=OverviewWorkspaceProjection.Create(Input() with {Desktop=desktop},Now);Assert.Equal(1,first.ObservedProcesses);
        var later=desktop with {Resources=Memory(Now.AddSeconds(2))};
        var expired=OverviewWorkspaceProjection.Create(Input() with {Desktop=later},Now.AddSeconds(2));
        Assert.Null(expired.ObservedProcesses);Assert.Equal(at,expired.ProcessObservedUtc);Assert.Equal(Now.AddSeconds(2),expired.ResourceObservedUtc);
        Assert.NotEqual("Unavailable",expired.Physical);
    }
    [Theory]
    [InlineData(null)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)]
    [InlineData(-1d)] [InlineData(101d)]
    public void UnknownOrInvalidCpuNeverBecomesZeroOrAnOverallHealthyBadge(double? cpu)
    {
        var desktop=Desktop([Row(1)]) with {Resources=Memory(Now) with {CpuPercent=cpu}};
        var view=OverviewWorkspaceProjection.Create(Input() with {Desktop=desktop},Now);
        Assert.Equal("Unavailable",view.Cpu);Assert.NotEqual("Unavailable",view.Physical);Assert.Contains("incomplete or unavailable",view.ResourceSummary);
    }
    [Theory]
    [InlineData("stale")] [InlineData("future")] [InlineData("problem")]
    public void FailedOrUndatedResourcesCannotLeaveFreshLookingNumbersOrForecasts(string kind)
    {
        var resource=Memory(Now);resource=kind switch {"stale"=>resource with {ObservedUtc=Now.AddSeconds(-4)},"future"=>resource with {ObservedUtc=Now.AddSeconds(2)},_=>resource with {Problem="Synthetic failure."}};
        var view=OverviewWorkspaceProjection.Create(Input() with {Desktop=Desktop([Row(1)]) with {Resources=resource},MemoryOnlySlots=42},Now);
        Assert.Equal("Unavailable",view.Physical);Assert.Equal("Unavailable",view.Commit);Assert.Equal("Unavailable",view.Cpu);
        Assert.StartsWith("Last-known forecast",view.ForecastCaption);Assert.DoesNotContain("additional client budgets",view.ForecastCaption);
    }
    [Fact]
    public void HeldPressureAndStoppedMonitoringRemainExplicitEvenWithPositiveMemoryAndFreshManualData()
    {
        var held=OverviewWorkspaceProjection.Create(Input() with {PressureHeld=true,MemoryOnlySlots=42},Now);
        Assert.Contains("holds new starts",held.ResourceSummary);Assert.Contains("not launch permission",held.ForecastCaption);
        var stopped=OverviewWorkspaceProjection.Create(Input() with {MonitorStopped=true},Now);
        Assert.Contains("monitoring stopped",stopped.ResourceCaption);Assert.Contains("does not restart",stopped.ResourceSummary);Assert.StartsWith("Last-known forecast",stopped.ForecastCaption);
    }
    [Fact]
    public void NewQueueFactsDoNotRedateThePreviousResourceForecast()
    {
        var later=Now.AddSeconds(2);var input=Input() with {Queue=new(false,[Operation(LaunchState.Queued)]),QueueObservedUtc=later};
        var view=OverviewWorkspaceProjection.Create(input,later);
        Assert.Equal(1,view.QueuedRequests);Assert.Equal(Now,view.ResourceObservedUtc);Assert.Equal(Now,view.ForecastObservedUtc);Assert.Equal(later,view.QueueObservedUtc);
    }
    [Fact]
    public void QueueSummaryUsesAllStatesAndTheSharedReviewPredicate()
    {
        var operations=new[]{Operation(LaunchState.Queued),Operation(LaunchState.Preparing),Operation(LaunchState.Validating),Operation(LaunchState.Launching),Operation(LaunchState.Stabilizing),Operation(LaunchState.Blocked),Operation(LaunchState.Failed),Operation(LaunchState.Arrived,true),Operation(LaunchState.Cancelled,true),Operation(LaunchState.Cancelled,true,true)};
        LaunchCoordinator.ValidateSnapshot(new(true,operations));
        var view=OverviewWorkspaceProjection.Create(Input() with {Queue=new(true,operations)},Now);
        Assert.Equal(1,view.QueuedRequests);Assert.Contains("4 preparing/dispatching",view.QueueSummary);Assert.Contains("1 blocked",view.QueueSummary);Assert.Contains("1 failed",view.QueueSummary);Assert.Contains("2 reviewable retained requests",view.QueueSummary);
        Assert.Null(OverviewWorkspaceProjection.Create(Input() with {QueueReady=false},Now).QueuedRequests);
    }
    [Theory]
    [InlineData(LaunchState.Queued,false,false,false)] [InlineData(LaunchState.Blocked,false,false,false)]
    [InlineData(LaunchState.Preparing,false,false,false)] [InlineData(LaunchState.Validating,false,false,false)]
    [InlineData(LaunchState.Launching,false,false,false)] [InlineData(LaunchState.Stabilizing,false,false,false)]
    [InlineData(LaunchState.Failed,false,false,false)] [InlineData(LaunchState.Cancelled,false,false,false)]
    [InlineData(LaunchState.Uncertain,false,false,true)] [InlineData(LaunchState.ReviewRequired,false,false,true)]
    [InlineData(LaunchState.Arrived,true,false,true)] [InlineData(LaunchState.ClientStartedUnverified,true,false,true)]
    [InlineData(LaunchState.Cancelled,true,false,true)] [InlineData(LaunchState.Cancelled,true,true,false)]
    public void DashboardAndExistingQueueRowsUseExactlyTheSameReviewDecision(LaunchState state,bool receipt,bool acknowledged,bool expected)
    {
        var operation=Operation(state,receipt,acknowledged);LaunchCoordinator.ValidateSnapshot(new(true,[operation]));
        Assert.Equal(expected,new LaunchRow(operation).CanReview);Assert.Equal(expected,LaunchRow.IsReviewable(operation));
        var view=OverviewWorkspaceProjection.Create(Input() with {Queue=new(true,[operation])},Now);
        Assert.Contains($"{(expected?1:0)} reviewable retained requests",view.QueueSummary);
    }
    [Fact]
    public void OversizedSourcesAreRejectedBeforeEnumerationAndMalformedQueueNeverLooksEmpty()
    {
        var input=Input() with {Desktop=Desktop([]) with {Sessions=new Oversized<SessionObservation>(129)},Queue=new(true,new Oversized<LaunchOperation>(1102))};
        var view=OverviewWorkspaceProjection.Create(input,Now);Assert.Null(view.ObservedProcesses);Assert.Null(view.QueuedRequests);
        var first=Operation(LaunchState.Queued);Assert.Null(OverviewWorkspaceProjection.Create(Input() with {Queue=new(true,[first,first])},Now).QueuedRequests);
        Assert.Null(OverviewWorkspaceProjection.Create(Input() with {Queue=new(true,[first with {State=(LaunchState)999}])},Now).QueuedRequests);
    }
    internal static OverviewWorkspaceInput Input()=>new(true,10_000,Now,Desktop([Row(1)]),false,true,new(true,[]),Now,true,false,false,true,3,Now);
    internal static SessionObservation Row(int pid)=>new(pid,new DateTime(2026,1,1).Ticks,"Synthetic",pid+100,"Synthetic",false,true,1,1,0,Now);
    internal static ResourceSnapshot Memory(DateTimeOffset at)=>new(at,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,64*CapacityPolicy.GiB,20*CapacityPolicy.GiB,10);
    internal static DesktopSnapshot Desktop(IReadOnlyList<SessionObservation> rows)=>new(Memory(Now),rows,ProcessObservedUtc:Now,ProcessRevision:7,ProcessInventoryComplete:true);
    internal static LaunchOperation Operation(LaunchState state,bool receipt=false,bool acknowledged=false)=>new(new(Guid.NewGuid(),Guid.NewGuid(),1,123,42,0,Now,1),state,acknowledged?LaunchProblem.ClientClosureAcknowledged:LaunchProblem.None,Now,receipt?new(100,new DateTime(2026,1,1).Ticks,Now):null,new(1,1));
    private sealed class Oversized<T>(int count):IReadOnlyList<T>
    {public int Count=>count;public T this[int index]=>throw new InvalidOperationException("Must not index oversized sources.");public IEnumerator<T> GetEnumerator()=>throw new InvalidOperationException("Must not enumerate sources.");IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();}
}
