using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class ServerBrowserCapacityTests
{
    private const long Place = 6564161046;
    private const string Job = "11111111-2222-4333-8444-000000000001";
    private const string OtherJob = "11111111-2222-4333-8444-000000000002";
    private static RobloxPublicServer Observation(Clock clock, string job = Job, int playing = 18, int maximum = 20)
        => new(Place, job, playing, maximum, null, null, clock.GetUtcNow());
    private static RobloxPublicServerResult Page(params RobloxPublicServer[] rows)
        => new(RobloxPublicServerStatus.PageLimit, rows, "synthetic-next", null, "Synthetic server page");

    [Fact]
    public async Task OnlyExplicitUsePinsCapacityAndLaterSelectionCannotRedirectIt()
    {
        var clock = new Clock(); var directory = new Directory { Result = Page(Observation(clock), Observation(clock, OtherJob, 1, 40)) };
        await using var vm = new ServerBrowserViewModel(directory, clock: clock);
        vm.UsePreferredPlace(Place); vm.Browse.Execute(null); await vm.Browse.Execution;
        vm.SelectedServer = vm.Servers[0];
        Assert.Null(vm.RequestedServerObservation); Assert.Null(vm.Destination!.JobId);
        Assert.Equal(RequestedServerCapacityState.Unknown, vm.AssessRequestedCapacity(3).State);
        vm.SelectedServer = vm.Servers[1]; vm.UseServer.Execute(null);
        Assert.Equal(OtherJob, vm.RequestedServerObservation!.JobId); Assert.Equal(OtherJob, vm.Destination.JobId);
        Assert.Equal(39, vm.AssessRequestedCapacity(3).OpenSlots);
        vm.SelectedServer = vm.Servers[0];
        Assert.Equal(OtherJob, vm.RequestedServerObservation.JobId); Assert.Equal(39, vm.AssessRequestedCapacity(3).OpenSlots);
        vm.UseServer.Execute(null);
        Assert.Equal(Job, vm.RequestedServerObservation.JobId);
        Assert.Equal(RequestedServerCapacityState.SpaceReported, vm.AssessRequestedCapacity(2).State);
        Assert.Equal(RequestedServerCapacityState.InsufficientSlots, vm.AssessRequestedCapacity(3).State);
        Assert.Equal(2, vm.AssessRequestedCapacity(int.MaxValue).OpenSlots);
        Assert.Equal(RequestedServerCapacityState.InvalidRecipientCount, vm.AssessRequestedCapacity(-1).State);
        Assert.Single(directory.Queries);
    }

    [Fact]
    public async Task FrozenBundleTargetCannotBorrowCapacityFromANewBrowserDestination()
    {
        var clock=new Clock();var directory=new Directory {Result=Page(Observation(clock),Observation(clock,OtherJob,1,40))};
        await using var vm=new ServerBrowserViewModel(directory,clock:clock);
        vm.UsePreferredPlace(Place);vm.Browse.Execute(null);await vm.Browse.Execution;
        vm.SelectedServer=vm.Servers[0];vm.UseServer.Execute(null);
        var frozen=vm.Destination!;
        Assert.Equal(2,vm.AssessRequestedCapacity(frozen,3).OpenSlots);
        vm.SelectedServer=vm.Servers[1];vm.UseServer.Execute(null);
        Assert.Equal(39,vm.AssessRequestedCapacity(3).OpenSlots);
        var original=vm.AssessRequestedCapacity(frozen,3);
        Assert.Equal(RequestedServerCapacityState.Unknown,original.State);Assert.Null(original.OpenSlots);
        Assert.Equal(Job,frozen.JobId);Assert.Equal(OtherJob,vm.Destination!.JobId);
    }

    [Theory]
    [InlineData("preferred", true)]
    [InlineData("favorite", true)]
    [InlineData("manual", true)]
    [InlineData("invalid-manual", false)]
    [InlineData("invalid-preferred", false)]
    [InlineData("sort", true)]
    [InlineData("filter", true)]
    [InlineData("cancel", true)]
    [InlineData("clear", false)]
    public async Task ManualFavoriteQueryAndCancelChangesDiscardObservationWithoutInventingNewEvidence(string action, bool hasDestination)
    {
        var clock = new Clock(); var directory = new Directory { Result = Page(Observation(clock)) };
        await using var vm = new ServerBrowserViewModel(directory, clock: clock);
        vm.UsePreferredPlace(Place); vm.Browse.Execute(null); await vm.Browse.Execution;
        vm.SelectedServer = vm.Servers[0]; vm.UseServer.Execute(null);
        Assert.True(vm.AssessRequestedCapacity(1).HasFreshObservation);
        var priorDestination = vm.Destination;
        switch (action)
        {
            case "preferred": vm.UsePreferredPlace(Place); break;
            case "favorite": vm.UseFavoriteDestination(new(Guid.NewGuid(), "Synthetic favorite", Place, Job, 1, Guid.NewGuid())); break;
            case "manual": vm.LinkInput = $"https://www.roblox.com/games/start?placeId={Place}&gameInstanceId={Job}"; vm.Resolve.Execute(null); break;
            case "invalid-manual": vm.LinkInput = "not a destination"; vm.Resolve.Execute(null); break;
            case "invalid-preferred": vm.UsePreferredPlace(0); break;
            case "sort": vm.SelectedServerOrder = ServerOrderChoice.MostFull; break;
            case "filter": vm.HideFull = true; break;
            case "cancel": vm.CancelWork(false); break;
            case "clear": vm.CancelWork(true); break;
        }
        Assert.Null(vm.RequestedServerObservation); Assert.Equal(hasDestination, vm.HasDestination);
        Assert.Equal(RequestedServerCapacityState.Unknown, vm.AssessRequestedCapacity(1).State);
        Assert.False(vm.CanUseServer); Assert.Single(directory.Queries);
        if (action is "sort" or "filter" or "cancel") Assert.Same(priorDestination, vm.Destination);
    }

    [Fact]
    public async Task AForeignRowInsertedIntoThePublicCollectionCannotSupplyCapacityOrChangeTheRequest()
    {
        var clock = new Clock(); var directory = new Directory { Result = Page(Observation(clock)) };
        await using var vm = new ServerBrowserViewModel(directory, clock: clock);
        vm.UsePreferredPlace(Place); vm.Browse.Execute(null); await vm.Browse.Execution;
        foreach (var row in new[] { new PublicServerRow(Observation(clock, OtherJob, 0, int.MaxValue)), new PublicServerRow(null!) })
        {
            vm.Servers.Add(row); vm.SelectedServer = row;
            Assert.False(vm.CanUseServer); vm.UseServer.Execute(null);
            Assert.Null(vm.RequestedServerObservation); Assert.Null(vm.Destination!.JobId);
            Assert.Equal(RequestedServerCapacityState.Unknown, vm.AssessRequestedCapacity(1).State);
        }
    }

    [Fact]
    public async Task ReusingAnObservationDoesNotRenewItsFiveMinuteTimestamp()
    {
        var clock = new Clock(); var directory = new Directory { Result = Page(Observation(clock)) };
        await using var vm = new ServerBrowserViewModel(directory, clock: clock);
        vm.UsePreferredPlace(Place); vm.Browse.Execute(null); await vm.Browse.Execution;
        vm.SelectedServer = vm.Servers[0]; vm.UseServer.Execute(null);
        var observed = vm.RequestedServerObservation!.ObservedUtc;
        clock.Advance(TimeSpan.FromMinutes(4)); vm.UseServer.Execute(null);
        Assert.Equal(observed, vm.RequestedServerObservation!.ObservedUtc);
        clock.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromTicks(1)));
        Assert.Equal(RequestedServerCapacityState.Stale, vm.AssessRequestedCapacity(1).State);
        Assert.False(vm.CanUseServer); vm.UseServer.Execute(null); Assert.Null(vm.RequestedServerObservation);
        Assert.Equal(RequestedServerCapacityState.Unknown, vm.AssessRequestedCapacity(1).State);
        Assert.Single(directory.Queries);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("missing-time")]
    [InlineData("place")]
    [InlineData("job")]
    [InlineData("duplicate")]
    [InlineData("negative")]
    [InlineData("maximum")]
    [InlineData("cursor")]
    [InlineData("null-row")]
    [InlineData("null-page")]
    [InlineData("null-result")]
    [InlineData("status")]
    public async Task InvalidPagesCannotLeavePriorRowsCursorOrCapacityUsable(string invalid)
    {
        var clock = new Clock(); var good = Observation(clock); var directory = new Directory { Result = Page(good) };
        await using var vm = new ServerBrowserViewModel(directory, clock: clock);
        vm.UsePreferredPlace(Place); vm.Browse.Execute(null); await vm.Browse.Execution;
        var oldRow = vm.Servers[0]; vm.SelectedServer = oldRow; vm.UseServer.Execute(null);
        directory.Result = invalid switch
        {
            "stale" => Page(good with { ObservedUtc = clock.GetUtcNow().AddMinutes(-6) }),
            "future" => Page(good with { ObservedUtc = clock.GetUtcNow().AddSeconds(6) }),
            "missing-time" => Page(good with { ObservedUtc = default }),
            "place" => Page(good with { PlaceId = Place + 1 }),
            "job" => Page(good with { JobId = "secret-marker" }),
            "duplicate" => Page(good, good with { Playing = 0 }),
            "negative" => Page(good with { Playing = -1 }),
            "maximum" => Page(good with { MaxPlayers = 0 }),
            "cursor" => Page(good) with { NextPageCursor = new string('x', 2049) },
            "null-row" => Page(new RobloxPublicServer[] { null! }),
            "null-page" => Page(good) with { Servers = null! },
            "null-result" => null!,
            _ => Page(good) with { Status = (RobloxPublicServerStatus)999 }
        };
        vm.Next.Execute(null); await vm.Next.Execution;
        Assert.Empty(vm.Servers); Assert.False(vm.CanNext); Assert.Null(vm.RequestedServerObservation);
        vm.SelectedServer = oldRow; Assert.False(vm.CanUseServer); vm.UseServer.Execute(null);
        Assert.Equal(RequestedServerCapacityState.Unknown, vm.AssessRequestedCapacity(1).State);
        Assert.DoesNotContain("secret-marker", vm.Status); Assert.Contains("No rows were accepted", vm.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PanicAndLockExpireAdviceAndFenceAnIgnoringLateProviderWithoutStartingAnotherOwner(bool lateFailure)
    {
        bool allowed = true; var clock = new Clock(); var directory = new Directory { Result = Page(Observation(clock)) };
        await using var vm = new ServerBrowserViewModel(directory, () => allowed, clock);
        vm.UsePreferredPlace(Place); vm.Browse.Execute(null); await vm.Browse.Execution;
        var oldRow = vm.Servers[0]; vm.SelectedServer = oldRow; vm.UseServer.Execute(null);
        directory.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); vm.Next.Execute(null);
        vm.CancelWork(false); allowed = false;
        Assert.True(directory.Token.IsCancellationRequested); Assert.Equal(RequestedServerCapacityState.Unknown, vm.AssessRequestedCapacity(1).State);
        vm.Browse.Execute(null); await vm.Browse.Execution; Assert.Equal(2, directory.Queries.Count);
        allowed = true; vm.SelectedServer = oldRow; vm.UseServer.Execute(null); Assert.Null(vm.RequestedServerObservation);
        if(lateFailure) directory.Pending.SetException(new FormatException("secret-marker"));
        else directory.Pending.SetResult(Page(Observation(clock, OtherJob)));
        await vm.Next.Execution;
        Assert.DoesNotContain(vm.Servers, row => row.JobId == OtherJob); Assert.Null(vm.RequestedServerObservation);
        Assert.False(vm.CanUseServer); Assert.False(vm.IsBusy); Assert.Equal(2, directory.Queries.Count);
        Assert.DoesNotContain("secret-marker", vm.Status); Assert.DoesNotContain("failed", vm.Status);
    }

    [Fact]
    public async Task ShutdownKeepsOwnershipPastTheFormerFiveSecondAbandonmentAndSuppressesLatePublication()
    {
        var clock = new Clock(); var directory = new Directory { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var vm = new ServerBrowserViewModel(directory, clock: clock);
        vm.UsePreferredPlace(Place); vm.Browse.Execute(null);
        var stopping = vm.DisposeAsync().AsTask();
        try
        {
            Assert.True(directory.Token.IsCancellationRequested); Assert.True(vm.IsBusy);
            // Cross the previous real five-second timeout: a short gated test cannot catch that abandonment.
            await Task.Delay(TimeSpan.FromMilliseconds(5200));
            Assert.False(stopping.IsCompleted); vm.UsePreferredPlace(Place + 1);
            Assert.Null(vm.Destination); Assert.Null(vm.RequestedServerObservation);
        }
        finally { directory.Pending.SetResult(Page(Observation(clock))); }
        await stopping; await vm.Browse.Execution;
        Assert.Empty(vm.Servers); Assert.Null(vm.Destination); Assert.False(vm.IsBusy); Assert.False(vm.CanUseServer);
        Assert.Equal(RequestedServerCapacityState.Unknown, vm.AssessRequestedCapacity(1).State);
        await vm.DisposeAsync(); Assert.Single(directory.Queries);
    }

    private sealed class Directory : IRobloxPublicServerDirectory
    {
        public readonly List<RobloxPublicServerQuery> Queries = [];
        public CancellationToken Token;
        public RobloxPublicServerResult Result = Page();
        public TaskCompletionSource<RobloxPublicServerResult>? Pending;
        public Task<RobloxPublicServerResult> BrowseAsync(RobloxPublicServerQuery query, CancellationToken ct)
        { Queries.Add(query); Token = ct; return Pending?.Task ?? Task.FromResult(Result); }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
