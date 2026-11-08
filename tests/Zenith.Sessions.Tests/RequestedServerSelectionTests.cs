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
}
