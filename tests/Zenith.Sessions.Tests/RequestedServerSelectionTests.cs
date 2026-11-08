using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class RequestedServerSelectionTests
{
    private const long Place = 6564161046;
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 20, 0, 0, TimeSpan.Zero);
    private static string Job(int id) => $"11111111-2222-4333-8444-{id:D12}";
    private static RobloxPublicServer Row(int id, int playing, int maximum = 20, DateTimeOffset? when = null, long place = Place)
        => new(place, Job(id), playing, maximum, null, null, when ?? Now);
    private static RequestedServerChoice Choose(int recipients, params RobloxPublicServer[] page)
        => RequestedServerSelection.ChooseForBatch(Place, page, recipients, Now);

    [Fact]
    public void TheFullestServerWithASpareSeatWinsOverAFullerExactFit()
    {
        // Two recipients: 18/20 fits exactly, 17/20 leaves one spare seat, 12/20 and 5/20 leave more.
        var choice = Choose(2, Row(1, 5), Row(2, 18), Row(3, 17), Row(4, 12));
        Assert.Equal(RequestedServerChoiceKind.SpareSeat, choice.Kind); Assert.Equal(Job(3), choice.Server!.JobId);
        Assert.Equal(2, choice.RecipientCount); Assert.Equal(4, choice.UsableCount);
        Assert.Contains("room for 2 accounts and a spare seat", choice.Explanation); Assert.Contains("17 of 20 players", choice.Explanation);
        Assert.Contains("not reserved", choice.Explanation);
    }

    [Fact]
    public void WithoutASpareSeatAnywhereTheFullestExactFitIsChosen()
    {
        // 17/19 and 18/20 both fit two recipients exactly; 18 players is fuller. 19/20 and 20/20 cannot fit two.
        var choice = Choose(2, Row(1, 19), Row(2, 17, 19), Row(3, 18), Row(4, 20));
        Assert.Equal(RequestedServerChoiceKind.ExactFit, choice.Kind); Assert.Equal(Job(3), choice.Server!.JobId);
        Assert.Contains("exactly enough room for 2 accounts", choice.Explanation); Assert.Contains("No seat is spare", choice.Explanation);
    }

    [Fact]
    public void EqualPlayerCountsKeepThePageOrderRobloxReturned()
    {
        var spare = Choose(1, Row(1, 4), Row(2, 10, 30), Row(3, 10, 12), Row(4, 10, 40));
        Assert.Equal(RequestedServerChoiceKind.SpareSeat, spare.Kind); Assert.Equal(Job(2), spare.Server!.JobId);
        var exact = Choose(1, Row(5, 19), Row(6, 19));
        Assert.Equal(RequestedServerChoiceKind.ExactFit, exact.Kind); Assert.Equal(Job(5), exact.Server!.JobId);
    }

    [Fact]
    public void FullAndOverfullServersHaveNoRoomAndLargeCountsNeverWrap()
    {
        var choice = Choose(1, Row(1, 25, 20), Row(2, 20, 20), Row(3, 3));
        Assert.Equal(Job(3), choice.Server!.JobId);
        var none = Choose(1, Row(1, 25, 20), Row(2, 20, 20));
        Assert.Equal(RequestedServerChoiceKind.NoRoom, none.Kind); Assert.Null(none.Server); Assert.Equal(2, none.UsableCount);
        Assert.Contains("None of the 2 listed servers had room for 1 account", none.Explanation);
        // Int32 arithmetic would wrap int.MaxValue - 1 + int.MaxValue + 1 to a negative "fit".
        var huge = Choose(int.MaxValue, Row(1, int.MaxValue - 1, int.MaxValue), Row(2, 0, int.MaxValue));
        Assert.Equal(RequestedServerChoiceKind.ExactFit, huge.Kind); Assert.Equal(Job(2), huge.Server!.JobId);
    }

    [Fact]
    public void StaleFutureAndUntimedRowsAreSkippedUsingTheCapacityFreshnessRules()
    {
        var choice = Choose(2,
            Row(1, 17, when: Now - RequestedServerCapacity.MaximumAge - TimeSpan.FromSeconds(1)),
            Row(2, 16, when: Now + RequestedServerCapacity.MaximumFutureSkew + TimeSpan.FromSeconds(1)),
            Row(3, 15, when: DateTimeOffset.MinValue),
            Row(4, 3, when: Now - RequestedServerCapacity.MaximumAge),
            Row(5, 2, when: Now + RequestedServerCapacity.MaximumFutureSkew));
        Assert.Equal(RequestedServerChoiceKind.SpareSeat, choice.Kind); Assert.Equal(Job(4), choice.Server!.JobId); Assert.Equal(2, choice.UsableCount);
    }

    [Fact]
    public void MalformedForeignAndMissingRowsAreSkipped()
    {
        RobloxPublicServer?[] page = [null, Row(1, 15, place: 42), new(Place, "not-a-server", 14, 20, null, null, Now),
            new(Place, Guid.Empty.ToString("D"), 13, 20, null, null, Now), Row(2, -1), Row(3, 0, 0), Row(4, 1)];
        var choice = RequestedServerSelection.ChooseForBatch(Place, page!, 1, Now);
        Assert.Equal(Job(4), choice.Server!.JobId); Assert.Equal(1, choice.UsableCount);
        var none = RequestedServerSelection.ChooseForBatch(Place, page[..^1]!, 1, Now);
        Assert.Equal(RequestedServerChoiceKind.NoRoom, none.Kind); Assert.Null(none.Server); Assert.Equal(0, none.UsableCount);
        Assert.Contains("No fresh public server was listed", none.Explanation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AServerListedTwiceIsJudgedByItsNewestCopy(bool newestFirst)
    {
        var older = Row(1, 10, when: Now - TimeSpan.FromMinutes(2));
        var newer = Row(1, 19, when: Now - TimeSpan.FromMinutes(1)) with { JobId = Job(1).ToUpperInvariant() };
        var full = newestFirst ? Choose(2, newer, Row(2, 8), older) : Choose(2, older, Row(2, 8), newer);
        Assert.Equal(Job(2), full.Server!.JobId); Assert.Equal(2, full.UsableCount);
        // A newer copy that reports room is used with its own numbers, not the older full copy's.
        var room = newer with { Playing = 12 }; var stale = older with { Playing = 20 };
        var chosen = newestFirst ? Choose(2, room, Row(2, 8), stale) : Choose(2, stale, Row(2, 8), room);
        Assert.Same(room, chosen.Server);
        // Equally new copies that disagree: the fuller one decides, so the batch is not sent on the emptier guess.
        var tie = Choose(2, Row(1, 10), Row(2, 8), Row(1, 19) with { JobId = Job(1).ToUpperInvariant() });
        Assert.Equal(Job(2), tie.Server!.JobId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NonpositiveRecipientCountsChooseNothing(int recipients)
    {
        var choice = Choose(recipients, Row(1, 1));
        Assert.Equal(RequestedServerChoiceKind.InvalidRecipientCount, choice.Kind); Assert.Null(choice.Server); Assert.Equal(recipients, choice.RecipientCount);
        Assert.Contains("Check the accounts to send", choice.Explanation);
    }

    [Fact]
    public void ArgumentsAreValidated()
    {
        Assert.Throws<ArgumentNullException>(() => RequestedServerSelection.ChooseForBatch(Place, null!, 1, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => RequestedServerSelection.ChooseForBatch(0, [Row(1, 1)], 1, Now));
    }

    private static ServerSpread Spread(int count, params RobloxPublicServer[] page)
        => RequestedServerSelection.SpreadServers(Place, page, count, Now, shuffle: false, random: null);

    [Fact]
    public void SpreadingHandsEachServerWithRoomForOneMoreAndASpareSeatOutOnceThenFallsBackToThePlace()
    {
        // 18/20 and 0/20 leave two or more open seats. 19/20 leaves one, 20/20 none and 21/20 is overfull; a stale row, another
        // place's row and a malformed row are never used, and a second copy of server 1 is the same server.
        var page = new[]
        {
            Row(1, 18), Row(2, 19), Row(3, 20), Row(4, 21), Row(5, 0), Row(6, 0, when: Now.AddMinutes(-6)), Row(7, 0, place: Place + 1),
            Row(8, 0) with { JobId = "not-a-server" }, Row(1, 18) with { JobId = Job(1).ToUpperInvariant() },
        };
        var spread = Spread(4, page);
        Assert.Equal(new[] { Job(1), Job(5), null, null }, spread.Destinations.Select(destination => destination.JobId));
        Assert.All(spread.Destinations, destination => Assert.Equal(Place, destination.PlaceId));
        Assert.Equal(new[] { RobloxServerKind.Public, RobloxServerKind.Public, RobloxServerKind.Unknown, RobloxServerKind.Unknown },
            spread.Destinations.Select(destination => destination.ServerKind));
        Assert.Equal(2, spread.SpreadCount); Assert.Equal(2, spread.PlaceOnlyCount); Assert.Equal(5, spread.UsableCount);
        Assert.Contains("2 of 4 accounts got their own listed server", spread.Explanation);
        Assert.Contains("the other 2 will join the place without a chosen server", spread.Explanation);
        Assert.Contains("not reserved", spread.Explanation);

        var everyone = Spread(2, page);
        Assert.Equal(new[] { Job(1), Job(5) }, everyone.Destinations.Select(destination => destination.JobId));
        Assert.Equal(0, everyone.PlaceOnlyCount); Assert.Contains("Each of the 2 accounts got its own listed server", everyone.Explanation);
        Assert.Contains("The account got its own listed server", Spread(1, page).Explanation);
    }

    [Fact]
    public void TheNewestCopyOfARepeatedServerDecidesWhetherItHasRoom()
    {
        var filledSince = Spread(2, Row(1, 5, when: Now.AddSeconds(-30)), Row(1, 19));
        Assert.Equal(new string?[] { null, null }, filledSince.Destinations.Select(destination => destination.JobId));
        Assert.Contains("None of the loaded servers had room for one more player and a spare seat, so 2 accounts will join the place without a chosen server", filledSince.Explanation);
        var emptiedSince = Spread(2, Row(1, 19, when: Now.AddSeconds(-30)), Row(1, 5));
        Assert.Equal(new[] { Job(1), null }, emptiedSince.Destinations.Select(destination => destination.JobId));
        var none = Spread(3);
        Assert.Equal(3, none.PlaceOnlyCount); Assert.Equal(0, none.UsableCount);
        Assert.Contains("No fresh public server is loaded, so 3 accounts will join the place without a chosen server", none.Explanation);
    }

    [Fact]
    public void ShufflingIsASeededPermutationOfTheServersWithRoom()
    {
        var page = Enumerable.Range(1, 8).Select(id => Row(id, id)).Append(Row(9, 19)).ToArray();
        var first = RequestedServerSelection.SpreadServers(Place, page, 8, Now, shuffle: true, new Random(20261107));
        var again = RequestedServerSelection.SpreadServers(Place, page, 8, Now, shuffle: true, new Random(20261107));
        var listed = Enumerable.Range(1, 8).Select(Job).ToArray();
        var shuffled = first.Destinations.Select(destination => destination.JobId!).ToArray();
        Assert.Equal(again.Destinations.Select(destination => destination.JobId), shuffled);
        Assert.Equal(listed.Order(), shuffled.Order());
        Assert.NotEqual(listed, shuffled);
        // Fewer accounts than servers: each still gets a different server.
        var three = RequestedServerSelection.SpreadServers(Place, page, 3, Now, shuffle: true, new Random(5));
        Assert.Equal(3, three.Destinations.Select(destination => destination.JobId).Distinct().Count());
        Assert.All(three.Destinations, destination => Assert.Contains(destination.JobId, listed));
    }

    [Fact]
    public void TheRecordedResultCountsRequestsThatGotAServerNotReviewedAccounts()
    {
        var loaded = Spread(4, Row(1, 0), Row(2, 0), Row(3, 0));
        Assert.Equal("No request was recorded, so no listed server was handed out.", loaded.ExplainRecorded(0, 0));
        Assert.StartsWith("The request got its own listed server with room for one more player and a spare seat.", loaded.ExplainRecorded(1, 1));
        Assert.StartsWith("Each of the 2 requests got its own listed server", loaded.ExplainRecorded(2, 2));
        string some = Spread(4, Row(1, 0), Row(2, 0)).ExplainRecorded(3, 2);
        Assert.Contains("2 of 3 requests got their own listed server", some);
        Assert.Contains("Only 2 loaded servers had that room, so the other 1 asks for the place without a chosen server.", some);
        Assert.All(new[] { loaded.ExplainRecorded(1, 1), some }, text => Assert.EndsWith("arrival still needs verification.", text));
        Assert.Equal("None of the loaded servers had room for one more player and a spare seat, so the request asks for the place without a chosen server.",
            Spread(1, Row(1, 19)).ExplainRecorded(1, 0));
        Assert.Equal("No fresh public server is loaded, so the 2 requests ask for the place without a chosen server. Browse public servers first to spread them.",
            Spread(2).ExplainRecorded(2, 0));
        Assert.DoesNotContain("account", some);
        Assert.Throws<ArgumentOutOfRangeException>(() => loaded.ExplainRecorded(1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => loaded.ExplainRecorded(1, -1));
        // More requests with a server than the spread held servers is a caller bug.
        Assert.Throws<ArgumentOutOfRangeException>(() => loaded.ExplainRecorded(4, 4));
    }

    [Fact]
    public void SpreadingValidatesItsArguments()
    {
        Assert.Throws<ArgumentNullException>(() => RequestedServerSelection.SpreadServers(Place, null!, 1, Now, false, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => RequestedServerSelection.SpreadServers(0, [], 1, Now, false, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => RequestedServerSelection.SpreadServers(Place, [], -1, Now, false, null));
        Assert.Throws<ArgumentNullException>(() => RequestedServerSelection.SpreadServers(Place, [], 1, Now, true, null));
        Assert.Empty(RequestedServerSelection.SpreadServers(Place, [Row(1, 1)], 0, Now, false, null).Destinations);
    }
}
