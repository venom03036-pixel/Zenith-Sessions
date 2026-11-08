using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class ServerBrowserFindRoomTests
{
    private const long Place = 6564161046;
    private static string Job(int id) => $"11111111-2222-4333-8444-{id:D12}";
    private static RobloxPublicServer Row(Clock clock, int id, int playing, int maximum = 20, long place = Place)
        => new(place, Job(id), playing, maximum, null, null, clock.GetUtcNow());
    private static RobloxPublicServerResult Page(params RobloxPublicServer[] rows)
        => new(RobloxPublicServerStatus.PageLimit, rows, "synthetic-next", null, "Synthetic server page");

    [Fact]
    public async Task OneRequestForAHundredNonFullServersMostFullFirstPinsTheChosenServerLikeUseSelectedServer()
    {
        var clock = new Clock();
        var directory = new FakeDirectory { Result = Page(Row(clock, 1, 19), Row(clock, 2, 17) with { JobId = Job(2).ToUpperInvariant() }, Row(clock, 3, 4)) };
        await using var vm = new ServerBrowserViewModel(directory, clock: clock); vm.UsePreferredPlace(Place);
        vm.FindRoom.Execute(2); await vm.FindRoom.Execution;
        var query = Assert.Single(directory.Queries);
        Assert.Equal(Place, query.PlaceId); Assert.Equal(100, query.PageSize); Assert.Equal(1, query.MaxPages); Assert.Null(query.Cursor);
        Assert.Equal(RobloxPublicServerSortOrder.Desc, query.SortOrder); Assert.True(query.ExcludeFullGames);
        // Stored as Use selected server stores a row: the normalized job ID and the observation it came from.
        Assert.Equal(Job(2), vm.Destination!.JobId); Assert.Equal(RobloxServerKind.Public, vm.Destination.ServerKind);
        Assert.Equal(Job(2), vm.RequestedServerObservation!.JobId); Assert.Equal(17, vm.RequestedServerObservation.Playing);
        Assert.Equal(RequestedServerCapacityState.SpaceReported, vm.AssessRequestedCapacity(2).State);
        Assert.Equal(3, vm.AssessRequestedCapacity(2).OpenSlots);
        Assert.Contains("17 of 20 players", vm.Status); Assert.Contains("not reserved", vm.Status);
        Assert.False(vm.IsBusy); Assert.Empty(vm.Servers);
    }

    [Fact]
    public async Task FindingIgnoresTheListSettingsAndLeavesTheLoadedRowsAndTheirCursorUsable()
    {
        var clock = new Clock(); var directory = new FakeDirectory { Result = Page(Row(clock, 1, 5)) };
        await using var vm = new ServerBrowserViewModel(directory, clock: clock); vm.UsePreferredPlace(Place);
        vm.SelectedServerOrder = ServerOrderChoice.BestPing; vm.HideFull = false;
        vm.Browse.Execute(null); await vm.Browse.Execution;
        var listed = Assert.Single(vm.Servers); vm.SelectedServer = listed;
        // The find page carries no cursor: if it replaced the list's cursor, Load next 25 would stop working.
        directory.Result = new(RobloxPublicServerStatus.Complete, [Row(clock, 2, 11, 12)], null, null, "Synthetic find page");
        vm.FindRoom.Execute(1); await vm.FindRoom.Execution;
        Assert.Equal(RobloxPublicServerSortOrder.Desc, directory.Queries[1].SortOrder); Assert.True(directory.Queries[1].ExcludeFullGames);
        Assert.Equal(100, directory.Queries[1].PageSize); Assert.Equal(Job(2), vm.Destination!.JobId);
        Assert.Contains("exactly enough room for 1 account", vm.Status);
        Assert.Same(listed, Assert.Single(vm.Servers)); Assert.Same(listed, vm.SelectedServer); Assert.True(vm.CanUseServer); Assert.True(vm.CanNext);
        vm.Next.Execute(null); await vm.Next.Execution;
        Assert.Equal("synthetic-next", directory.Queries[2].Cursor); Assert.Equal(25, directory.Queries[2].PageSize);
        Assert.Equal(RobloxPublicServerSortOrder.Asc, directory.Queries[2].SortOrder);
    }

    [Fact]
    public async Task NoServerWithRoomLeavesTheRequestedDestinationUnchanged()
    {
        var clock = new Clock(); var directory = new FakeDirectory { Result = Page(Row(clock, 1, 19), Row(clock, 2, 18)) };
        await using var vm = new ServerBrowserViewModel(directory, clock: clock); vm.UsePreferredPlace(Place); var requested = vm.Destination;
        vm.FindRoom.Execute(3); await vm.FindRoom.Execution;
        Assert.Single(directory.Queries); Assert.Same(requested, vm.Destination); Assert.Null(vm.Destination!.JobId); Assert.Null(vm.RequestedServerObservation);
        Assert.Contains("None of the 2 listed servers had room for 3 accounts", vm.Status); Assert.Contains("destination is unchanged", vm.Status);
    }

    [Fact]
    public async Task NoRecipientsALockOrAProviderPauseSendNoRequest()
    {
        var clock = new Clock(); bool allowed = true;
        var directory = new FakeDirectory { Result = new(RobloxPublicServerStatus.RateLimited, [], null, null, "Synthetic rate limit") };
        await using var vm = new ServerBrowserViewModel(directory, () => allowed, clock); vm.UsePreferredPlace(Place);
        vm.FindRoom.Execute(0); await vm.FindRoom.Execution;
        Assert.Empty(directory.Queries); Assert.Contains("Check the accounts to send", vm.Status); Assert.Contains("No request was sent", vm.Status);
        allowed = false; vm.FindRoom.Execute(2); await vm.FindRoom.Execution; Assert.Empty(directory.Queries); allowed = true;
        vm.Browse.Execute(null); await vm.Browse.Execution; Assert.Single(directory.Queries);
        // A rate limit without a retry header still pauses every server request for 30 seconds, and a limited find pauses browsing too.
        clock.Advance(TimeSpan.FromSeconds(29)); vm.FindRoom.Execute(2); await vm.FindRoom.Execution;
        Assert.Single(directory.Queries); Assert.Contains("No request was sent", vm.Status);
        clock.Advance(TimeSpan.FromSeconds(2)); vm.FindRoom.Execute(2); await vm.FindRoom.Execution;
        Assert.Equal(2, directory.Queries.Count); Assert.Equal("Synthetic rate limit", vm.Status); Assert.Null(vm.Destination!.JobId);
        vm.Browse.Execute(null); await vm.Browse.Execution; Assert.Equal(2, directory.Queries.Count);
    }

    [Fact]
    public async Task AReplacedDestinationCancelsAndFencesALateChoice()
    {
        var clock = new Clock(); var directory = new FakeDirectory { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var vm = new ServerBrowserViewModel(directory, clock: clock); vm.UsePreferredPlace(Place);
        vm.FindRoom.Execute(2); Assert.True(vm.IsBusy); Assert.False(vm.CanUseServer);
        vm.Browse.Execute(null); await vm.Browse.Execution; Assert.Single(directory.Queries);
        vm.UsePreferredPlace(42); Assert.True(directory.Tokens[0].IsCancellationRequested);
        directory.Pending.SetResult(Page(Row(clock, 1, 3))); await vm.FindRoom.Execution;
        Assert.Equal(42, vm.Destination!.PlaceId); Assert.Null(vm.Destination.JobId); Assert.Null(vm.RequestedServerObservation);
        Assert.DoesNotContain("players", vm.Status); Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task AnInconsistentPageChoosesNoServer()
    {
        var clock = new Clock(); var directory = new FakeDirectory();
        await using var vm = new ServerBrowserViewModel(directory, clock: clock); vm.UsePreferredPlace(Place);
        RobloxPublicServerResult[] bad =
        [
            Page(Row(clock, 1, 3), Row(clock, 2, 4, place: 42)),
            Page(Row(clock, 1, 3), Row(clock, 2, 4) with { ObservedUtc = clock.GetUtcNow() - TimeSpan.FromMinutes(6) }),
            Page(Row(clock, 1, 3), Row(clock, 1, 4) with { JobId = Job(1).ToUpperInvariant() }),
            Page(Enumerable.Range(1, 101).Select(id => Row(clock, id, 3)).ToArray()),
        ];
        foreach (var result in bad)
        {
            directory.Result = result; vm.FindRoom.Execute(1); await vm.FindRoom.Execution;
            Assert.Null(vm.Destination!.JobId); Assert.Null(vm.RequestedServerObservation);
            Assert.Contains("inconsistent or stale", vm.Status); Assert.Contains("No server was chosen", vm.Status);
        }
        Assert.Equal(bad.Length, directory.Queries.Count);
    }

    private sealed class FakeDirectory : IRobloxPublicServerDirectory
    {
        public List<RobloxPublicServerQuery> Queries { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public RobloxPublicServerResult Result = new(RobloxPublicServerStatus.Complete, [], null, null, "Synthetic empty page");
        public TaskCompletionSource<RobloxPublicServerResult>? Pending;
        public Task<RobloxPublicServerResult> BrowseAsync(RobloxPublicServerQuery query, CancellationToken ct)
        { Queries.Add(query); Tokens.Add(ct); return Pending?.Task ?? Task.FromResult(Result); }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
