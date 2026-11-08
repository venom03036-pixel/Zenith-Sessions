using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// Recently played: places observed in client logs, named through one batch request plus single lookups. Never verified.
public sealed class RecentlyPlayedViewModelTests
{
    private static readonly DateTimeOffset T0=new(2026,11,7,12,0,0,TimeSpan.Zero);
    private static RecentlyPlayedSource Source(long place,long? universe,int minutesAgo)=>new(place,universe,T0.AddMinutes(-minutesAgo));

    [Fact]
    public Task DedupesByPlaceNewestFirstAndKeepsSix()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var names=new NamesDirectory();await using var vm=new RecentlyPlayedViewModel(names,names,f.Browser,()=>f.Allowed);
        await vm.UpdateAsync([Source(1,101,50),Source(2,102,40),Source(3,103,30),Source(3,103,5),Source(4,104,20),Source(5,105,10),Source(6,106,60),Source(7,107,70),Source(5,105,90)]);
        Assert.Equal(new long[]{3,5,4,2,1,6},vm.Chips.Select(chip=>chip.PlaceId).ToArray()); // Newest observation per place wins; the seventh drops.
        Assert.True(vm.HasChips);
        await vm.UpdateAsync([]);Assert.Empty(vm.Chips);Assert.False(vm.HasChips);
    });

    [Fact]
    public Task NamesComeFromOneBatchPlusSingleLookupsForMissingUniverse()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var names=new NamesDirectory();await using var vm=new RecentlyPlayedViewModel(names,names,f.Browser,()=>f.Allowed);
        await vm.UpdateAsync([Source(1,101,1),Source(2,102,2),Source(3,103,3),Source(4,104,4),Source(5,null,5),Source(6,null,6)]);await vm.ActiveOperation;
        Assert.Equal(new long[]{101,102,103,104},Assert.Single(names.Batches));Assert.Equal(new long[]{5,6},names.Singles.ToArray());
        Assert.Equal(new[]{"Game 101","Game 102","Game 103","Game 104","Named place 5","Named place 6"},vm.Chips.Select(chip=>chip.Name).ToArray());
        Assert.All(vm.Chips,chip=>Assert.True(chip.HasName));Assert.Equal(1,names.MaxConcurrent);
    });

    [Fact]
    public Task AlreadyNamedPlacesAreNotLookedUpAgainThisVisit()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var names=new NamesDirectory();await using var vm=new RecentlyPlayedViewModel(names,names,f.Browser,()=>f.Allowed);
        RecentlyPlayedSource[] sources=[Source(1,101,1),Source(2,null,2)];
        await vm.UpdateAsync(sources);await vm.ActiveOperation;await vm.UpdateAsync(sources);await vm.ActiveOperation;
        Assert.Single(names.Batches);Assert.Single(names.Singles);Assert.Equal("Game 101",vm.Chips[0].Name);
        await vm.UpdateAsync([Source(1,101,1),Source(2,null,2),Source(3,103,3)]);await vm.ActiveOperation;
        Assert.Equal(2,names.Batches.Count);Assert.Equal(new long[]{103},names.Batches[1]);Assert.Single(names.Singles); // Only the new place is asked.
        vm.Cancel();Assert.Empty(vm.Chips);
        await vm.UpdateAsync(sources);await vm.ActiveOperation;Assert.Equal(3,names.Batches.Count); // A new visit asks again.
    });

    [Fact]
    public Task ChipUsesThePlaceOnlyNeverTheOldServer()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var names=new NamesDirectory();await using var vm=new RecentlyPlayedViewModel(names,names,f.Browser,()=>f.Allowed);
        await vm.UpdateAsync([Source(42,142,1)]);await vm.ActiveOperation;var chip=vm.Chips[0];
        vm.Use.Execute(chip);Assert.Equal(42,f.Browser.Destination!.PlaceId);Assert.Null(f.Browser.Destination.JobId);Assert.Equal(0,f.Servers.Calls);
        vm.Use.Execute(new RecentlyPlayedChip(99));Assert.Equal(42,f.Browser.Destination!.PlaceId); // Only listed chips are used.
    });

    [Fact]
    public Task UnnamedPlaceShowsPlaceIdWithoutRetry()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var names=new NamesDirectory{Unknown=true};await using var vm=new RecentlyPlayedViewModel(names,names,f.Browser,()=>f.Allowed);
        await vm.UpdateAsync([Source(123,1123,1),Source(124,null,2)]);await vm.ActiveOperation;
        Assert.Equal(new[]{"Place 123","Place 124"},vm.Chips.Select(chip=>chip.Name).ToArray());Assert.All(vm.Chips,chip=>Assert.False(chip.HasName));
        await vm.UpdateAsync([Source(123,1123,1),Source(124,null,2)]);await vm.ActiveOperation;
        Assert.Single(names.Batches);Assert.Single(names.Singles); // Attempted places are not asked again this visit.
    });

    [Fact]
    public Task NewSourcesCancelTheOldNamingRunAndDisposalJoinsIt()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var names=new NamesDirectory{Hold=true};var vm=new RecentlyPlayedViewModel(names,names,f.Browser,()=>f.Allowed);
        Task first=vm.UpdateAsync([Source(1,101,1)]);await names.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));var firstToken=names.LastToken;
        Task second=vm.UpdateAsync([Source(2,102,1)]);Assert.True(firstToken.IsCancellationRequested);Assert.False(names.LastToken.IsCancellationRequested);
        Task disposal=vm.DisposeAsync().AsTask();Assert.False(disposal.IsCompleted);
        names.Hold=false;names.Release();await disposal.WaitAsync(TimeSpan.FromSeconds(5));await first;await second;
        Assert.Empty(vm.Chips);
    });

    [Fact]
    public Task IdenticalUpdatesWhileNamingKeepTheOneRequestInFlight()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var names=new NamesDirectory{Hold=true};await using var vm=new RecentlyPlayedViewModel(names,names,f.Browser,()=>f.Allowed);
        RecentlyPlayedSource[] sources=[Source(1,101,1),Source(2,null,2)];
        Task first=vm.UpdateAsync(sources);await names.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));var token=names.LastToken;
        try
        {
            for(int sample=0;sample<5;sample++) _=vm.UpdateAsync(sources); // Every desktop sample re-sends the same observations.
            Assert.False(token.IsCancellationRequested);Assert.Single(names.Batches);Assert.Empty(names.Singles);
        }
        finally {names.Hold=false;names.Release();}
        await first;await vm.ActiveOperation;
        Assert.Single(names.Batches);Assert.Single(names.Singles);Assert.Equal(new[]{"Game 101","Named place 2"},vm.Chips.Select(chip=>chip.Name).ToArray());
    });

    // A Deferred without RetryAfter is the shared two-slot gate or a 15 s timeout, not a Roblox cooldown: the run stops so no
    // refusals pile up, and its places wait for an explicit trigger. The shell sends one update per desktop presentation
    // (about every 0.5-1 s), so those updates must never re-ask them, or a slow Roblox would be asked again after every timeout.
    [Fact]
    public Task LocalRefusalIsNotAskedAgainByRepeatedUpdatesUntilAnExplicitTrigger()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var names=new NamesDirectory();names.Deferrals.Enqueue(null);
        await using var vm=new RecentlyPlayedViewModel(names,names,f.Browser,()=>f.Allowed);
        RecentlyPlayedSource[] sources=[Source(1,101,1),Source(2,null,2)];
        await vm.UpdateAsync(sources);await vm.ActiveOperation;
        Assert.Single(names.Batches);Assert.Empty(names.Singles);Assert.All(vm.Chips,chip=>Assert.False(chip.HasName));
        for(int presentation=0;presentation<5;presentation++) {await vm.UpdateAsync(sources);await vm.ActiveOperation;}
        Assert.Single(names.Batches);Assert.Empty(names.Singles); // One lookup; the run's unasked place waits with it.
        Assert.Equal(new[]{"Place 1","Place 2"},vm.Chips.Select(chip=>chip.Name).ToArray());
        vm.ForgetLocalRefusals(); // Join a game opened again, or another account chosen there.
        await vm.UpdateAsync(sources);await vm.ActiveOperation;
        Assert.Equal(2,names.Batches.Count);Assert.Single(names.Singles);Assert.Equal(new[]{"Game 101","Named place 2"},vm.Chips.Select(chip=>chip.Name).ToArray());
    });

    [Fact]
    public Task AfterALocalRefusalOnlyANewObservationIsAskedAndANewVisitAsksAgain()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var names=new NamesDirectory();names.Deferrals.Enqueue(null);
        await using var vm=new RecentlyPlayedViewModel(names,names,f.Browser,()=>f.Allowed);
        RecentlyPlayedSource[] first=[Source(5,null,2)];
        await vm.UpdateAsync(first);await vm.ActiveOperation;
        for(int presentation=0;presentation<3;presentation++) {await vm.UpdateAsync(first);await vm.ActiveOperation;}
        Assert.Equal(new long[]{5},names.Singles.ToArray());
        RecentlyPlayedSource[] second=[Source(6,null,1),Source(5,null,2)];
        await vm.UpdateAsync(second);await vm.ActiveOperation; // A new place in the logs is a new observation: only it is asked.
        Assert.Equal(new long[]{5,6},names.Singles.ToArray());Assert.Equal(new[]{"Named place 6","Place 5"},vm.Chips.Select(chip=>chip.Name).ToArray());
        vm.Cancel();await vm.UpdateAsync(second);await vm.ActiveOperation; // Leaving and opening the page again is a new visit.
        Assert.Equal(new long[]{5,6,6,5},names.Singles.ToArray());Assert.All(vm.Chips,chip=>Assert.True(chip.HasName));
    });

    [Fact]
    public Task ProviderPauseIsNotAskedAgainThisVisit()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var names=new NamesDirectory();names.Deferrals.Enqueue(TimeSpan.FromSeconds(30));
        await using var vm=new RecentlyPlayedViewModel(names,names,f.Browser,()=>f.Allowed);
        RecentlyPlayedSource[] sources=[Source(1,101,1)];
        await vm.UpdateAsync(sources);await vm.ActiveOperation;await vm.UpdateAsync(sources);await vm.ActiveOperation;
        Assert.Single(names.Batches);Assert.Equal("Place 1",Assert.Single(vm.Chips).Name); // A Roblox cooldown is never retried automatically.
    });

    [Fact]
    public Task MissingDirectoriesLeavePlaceIds()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();await using var vm=new RecentlyPlayedViewModel(null,null,f.Browser,()=>f.Allowed);
        await vm.UpdateAsync([Source(5,105,1)]);await vm.ActiveOperation;Assert.Equal("Place 5",Assert.Single(vm.Chips).Name);
    });

    private sealed class NamesDirectory:IPublicRobloxGameBatchDirectory,IPublicRobloxGameDirectory
    {
        public readonly List<long[]> Batches=[];public readonly List<long> Singles=[];
        public bool Hold,Unknown;public int Active,MaxConcurrent;public CancellationToken LastToken;
        public readonly Queue<TimeSpan?> Deferrals=new(); // One-shot Deferred answers: null is a local refusal, a value a provider pause.
        public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release()=>_release.TrySetResult();
        private async Task<T> Track<T>(Func<T> result,CancellationToken ct)
        {
            LastToken=ct;int active=++Active;if(active>MaxConcurrent)MaxConcurrent=active;Entered.TrySetResult();
            try {if(Hold)await _release.Task;return result();} finally {Active--;}
        }
        public Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxGame>>> LookupGamesAsync(IReadOnlyList<long> universeIds,CancellationToken ct)
        {
            Batches.Add(universeIds.ToArray());
            if(Deferrals.TryDequeue(out var pause)) return Task.FromResult(new PublicDirectoryResult<IReadOnlyList<PublicRobloxGame>>(PublicDirectoryState.Deferred,null,"synthetic",pause));
            return Track(()=>Unknown?new PublicDirectoryResult<IReadOnlyList<PublicRobloxGame>>(PublicDirectoryState.Unknown,null,"synthetic")
                :SearchTest.Ok<IReadOnlyList<PublicRobloxGame>>(universeIds.Select(id=>new PublicRobloxGame(id-100,id,id-100,"Game "+id,7,"Creator",PublicRobloxCreatorKind.User,1,10,DateTimeOffset.UtcNow)).ToArray()),ct);
        }
        public Task<PublicDirectoryResult<PublicRobloxGame>> LookupGameAsync(long requestedPlaceId,CancellationToken ct)
        {
            Singles.Add(requestedPlaceId);
            if(Deferrals.TryDequeue(out var pause)) return Task.FromResult(new PublicDirectoryResult<PublicRobloxGame>(PublicDirectoryState.Deferred,null,"synthetic",pause));
            return Track(()=>Unknown?new PublicDirectoryResult<PublicRobloxGame>(PublicDirectoryState.Unknown,null,"synthetic")
                :SearchTest.Ok(new PublicRobloxGame(requestedPlaceId,requestedPlaceId+1000,requestedPlaceId,"Named place "+requestedPlaceId,7,"Creator",PublicRobloxCreatorKind.User,1,10,DateTimeOffset.UtcNow)),ct);
        }
        public Task<PublicDirectoryResult<PublicRobloxGameIcon>> GetGameIconAsync(long universeId,CancellationToken ct)=>throw new InvalidOperationException("Unexpected icon request");
        public Task<PublicDirectoryResult<EncodedPublicGameIcon>> FetchGameIconAsync(PublicRobloxGameIcon icon,CancellationToken ct)=>throw new InvalidOperationException("Unexpected icon download");
        public void ClearMemoryCache() { }
    }
}
