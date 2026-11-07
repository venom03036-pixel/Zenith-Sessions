using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class RequestedServerCapacityTests
{
    private const long Place = 6564161046;
    private const string Job = "11111111-2222-4333-8444-000000000001";
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
    private static readonly RobloxRequestedDestination Requested = new(Place, Job, RobloxServerKind.Public);
    private static RobloxPublicServer Observation(int playing = 18, int maximum = 20, DateTimeOffset? when = null)
        => new(Place, Job, playing, maximum, null, null, when ?? Now);

    [Theory]
    [InlineData(1, RequestedServerCapacityState.SpaceReported)]
    [InlineData(2, RequestedServerCapacityState.SpaceReported)]
    [InlineData(3, RequestedServerCapacityState.InsufficientSlots)]
    [InlineData(int.MaxValue, RequestedServerCapacityState.InsufficientSlots)]
    public void EachRecipientCountIsComparedWithOriginalObservedOpenSlots(int count, RequestedServerCapacityState state)
    {
        var result = RequestedServerCapacity.Assess(Requested, Observation(), count, Now.AddMinutes(2));
        Assert.Equal(state, result.State); Assert.Equal(count, result.RecipientCount); Assert.Equal(2, result.OpenSlots);
        Assert.Equal(Now, result.ObservedUtc); Assert.Equal(Now.AddMinutes(5), result.ExpiresUtc);
        Assert.True(result.HasFreshObservation); Assert.Equal(state != RequestedServerCapacityState.SpaceReported, result.Warns);
        Assert.Contains("not reserved", result.Explanation); Assert.Contains("unverified", result.Explanation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NonpositiveRecipientCountsHaveExplicitNonCapacityResults(int count)
    {
        var result = RequestedServerCapacity.Assess(Requested, Observation(), count, Now);
        Assert.Equal(RequestedServerCapacityState.InvalidRecipientCount, result.State);
        Assert.Null(result.OpenSlots); Assert.False(result.HasFreshObservation); Assert.True(result.Warns);
    }

    [Theory]
    [InlineData(20, 20, 0)]
    [InlineData(int.MaxValue, 1, 0)]
    [InlineData(0, int.MaxValue, int.MaxValue)]
    public void FullOverfullAndMaximumCountsCannotWrapCapacity(int playing, int maximum, int expected)
    {
        var result = RequestedServerCapacity.Assess(Requested, Observation(playing, maximum), int.MaxValue, Now);
        Assert.Equal(expected, result.OpenSlots); Assert.True(result.HasFreshObservation);
        Assert.Equal(expected == 0 ? RequestedServerCapacityState.Full : RequestedServerCapacityState.SpaceReported, result.State);
    }

    [Theory]
    [InlineData(-1, 20)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void MalformedOccupancyCannotProduceCapacity(int playing, int maximum)
    {
        var result = RequestedServerCapacity.Assess(Requested, Observation(playing, maximum), 1, Now);
        Assert.Equal(RequestedServerCapacityState.Unknown, result.State); Assert.Null(result.OpenSlots);
    }

    [Fact]
    public void MissingForeignAndWrongKindObservationsCannotDescribeTheRequestedServer()
    {
        var foreign = new[] { Observation() with { PlaceId = Place + 1 }, Observation() with { JobId = "11111111-2222-4333-8444-000000000002" },
            Observation() with { JobId = "untrusted-secret-marker" }, Observation() with { PlaceId = 0 } };
        foreach (var observation in foreign)
        {
            var result = RequestedServerCapacity.Assess(Requested, observation, 1, Now);
            Assert.Equal(RequestedServerCapacityState.Unknown, result.State); Assert.Null(result.OpenSlots); Assert.Null(result.ObservedUtc);
            Assert.DoesNotContain("untrusted-secret-marker", result.Explanation);
        }
        Assert.Equal(RequestedServerCapacityState.Unknown, RequestedServerCapacity.Assess(Requested, null, 1, Now).State);
        Assert.Equal(RequestedServerCapacityState.Unknown, RequestedServerCapacity.Assess(null, Observation(), 1, Now).State);
        Assert.Equal(RequestedServerCapacityState.Unknown, RequestedServerCapacity.Assess(new(Place, Job), Observation(), 1, Now).State);
        Assert.Equal(RequestedServerCapacityState.Unknown, RequestedServerCapacity.Assess(new(Place), Observation(), 1, Now).State);
    }

    [Fact]
    public void FreshnessUsesTheOriginalTimestampAndRejectsForwardAndBackwardClockChanges()
    {
        Assert.True(RequestedServerCapacity.Assess(Requested, Observation(), 1, Now.AddMinutes(5)).HasFreshObservation);
        foreach (var time in new[] { Now.AddMinutes(5).AddTicks(1), Now.AddSeconds(-6) })
        {
            var result = RequestedServerCapacity.Assess(Requested, Observation(), 1, time);
            Assert.Equal(RequestedServerCapacityState.Stale, result.State); Assert.Null(result.OpenSlots);
            Assert.Equal(Now, result.ObservedUtc); Assert.Equal(Now.AddMinutes(5), result.ExpiresUtc);
        }
        Assert.Equal(RequestedServerCapacityState.Stale, RequestedServerCapacity.Assess(Requested, Observation(when: DateTimeOffset.MinValue), 1, Now).State);
        // Normalize offsets before adding freshness, including a local representation near DateTime.MaxValue.
        var extreme = new DateTimeOffset(DateTime.MaxValue, TimeSpan.FromHours(14));
        Assert.True(RequestedServerCapacity.Assess(Requested, Observation(when: extreme), 1, extreme).HasFreshObservation);
        Assert.True(RequestedServerCapacity.Assess(Requested, Observation(when: DateTimeOffset.MaxValue), 1, DateTimeOffset.MaxValue).HasFreshObservation);
    }
}
