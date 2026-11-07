using System.Globalization;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class NativeHandoffParserTests
{
    [Theory]
    [InlineData(false, false, "InApp")]
    [InlineData(false, true, "PreferInApp")]
    [InlineData(true, false, "PreferInApp")]
    [InlineData(true, true, "InApp")]
    public void SourceDescribedGameAndExactJobFormsRemainOpaqueAndRedacted(bool job, bool channel, string experience)
    {
        var clock = new HandoffClock(); var context = SyntheticHandoff.Context(job);
        string original = SyntheticHandoff.Uri(context, clock, channel: channel, experience: experience);
        var result = Capture(context, original, clock);
        Assert.Equal(NativeHandoffCaptureStatus.AcceptedSourceGrammar, result.Status);
        using var candidate = result.Candidate!;
        Assert.False(candidate.IsConsumed); Assert.Same(context, candidate.Context);
        string diagnostic = candidate + JsonSerializer.Serialize(result);
        Assert.DoesNotContain(SyntheticHandoff.Ticket, diagnostic); Assert.DoesNotContain("gameinfo", diagnostic);
        Assert.DoesNotContain(original, diagnostic); Assert.Contains("unverified", candidate.Evidence);
        candidate.Dispose(); Assert.True(candidate.IsConsumed);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("case")]
    [InlineData("mode")]
    [InlineData("version")]
    [InlineData("slashes")]
    [InlineData("malformed-escape")]
    [InlineData("utf8")]
    [InlineData("unicode-ticket")]
    [InlineData("cookie-marker")]
    [InlineData("empty-ticket")]
    [InlineData("raw-value-delimiter")]
    [InlineData("tracker-overflow")]
    [InlineData("tracker-js-rounding")]
    [InlineData("tracker-leading-zero")]
    [InlineData("timestamp-range")]
    [InlineData("locale")]
    [InlineData("channel")]
    [InlineData("trailing-separator")]
    public void UnknownAmbiguousOrUnsafeOuterShapesNeverProduceACandidate(string mutation)
    {
        var clock = new HandoffClock(); var context = SyntheticHandoff.Context(); string uri = SyntheticHandoff.Uri(context, clock);
        uri = mutation switch
        {
            "duplicate" => uri + "+gameinfo:synthetic_duplicate",
            "unknown" => uri.Replace("LaunchExp:", "UnknownField:", StringComparison.Ordinal),
            "case" => uri.Replace("launchmode:", "LaunchMode:", StringComparison.Ordinal),
            "mode" => uri.Replace("launchmode:play", "launchmode:edit", StringComparison.Ordinal),
            "version" => uri.Replace("roblox-player:1+", "roblox-player:2+", StringComparison.Ordinal),
            "slashes" => uri.Replace("roblox-player:1+", "roblox-player://1+", StringComparison.Ordinal),
            "malformed-escape" => ReplaceTicket(uri, "%GG"),
            "utf8" => ReplaceTicket(uri, "%C0%AF"),
            "unicode-ticket" => ReplaceTicket(uri, "%F0%9F%8E%AE"),
            "cookie-marker" => ReplaceTicket(uri, "synthetic-DO-NOT-SHARE-cookie"),
            "empty-ticket" => ReplaceTicket(uri, ""),
            "raw-value-delimiter" => ReplaceTicket(uri, "synthetic/ticket"),
            "tracker-overflow" => uri.Replace("browsertrackerid:" + SyntheticHandoff.Tracker, "browsertrackerid:9223372036854775808", StringComparison.Ordinal),
            "tracker-js-rounding" => uri.Replace("browsertrackerid:" + SyntheticHandoff.Tracker, "browsertrackerid:9007199254740992", StringComparison.Ordinal),
            "tracker-leading-zero" => uri.Replace("browsertrackerid:" + SyntheticHandoff.Tracker, "browsertrackerid:0" + SyntheticHandoff.Tracker, StringComparison.Ordinal),
            "timestamp-range" => uri.Replace("launchtime:" + clock.GetUtcNow().ToUnixTimeMilliseconds(), "launchtime:9223372036854775807", StringComparison.Ordinal),
            "locale" => uri.Replace("robloxLocale:en_us", "robloxLocale:en%2Fus", StringComparison.Ordinal),
            "channel" => uri + "+channel:unknown-test-channel",
            _ => uri + "+"
        };
        var result = Capture(context, uri, clock);
        Assert.NotEqual(NativeHandoffCaptureStatus.AcceptedSourceGrammar, result.Status); Assert.Null(result.Candidate);
        Assert.DoesNotContain("synthetic", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("wrong-place")]
    [InlineData("duplicate")]
    [InlineData("encoded-key")]
    [InlineData("case-key")]
    [InlineData("alternate-host")]
    [InlineData("alternate-port")]
    [InlineData("userinfo")]
    [InlineData("fragment")]
    [InlineData("double-place")]
    [InlineData("tracker")]
    [InlineData("origin")]
    [InlineData("private")]
    [InlineData("launch-data")]
    [InlineData("follow")]
    [InlineData("play-together")]
    public void NestedDestinationIsDecodedByLayerAndBoundToTheFrozenTarget(string mutation)
    {
        var clock = new HandoffClock(); var context = SyntheticHandoff.Context();
        string original = SyntheticHandoff.Uri(context, clock, changeDestination: value => mutation switch
        {
            "wrong-place" => value.Replace("placeId=1818", "placeId=1819", StringComparison.Ordinal),
            "duplicate" => value + "&placeId=1818",
            "encoded-key" => value.Replace("placeId=", "%70laceId=", StringComparison.Ordinal),
            "case-key" => value.Replace("placeId=", "PlaceId=", StringComparison.Ordinal),
            "alternate-host" => value.Replace("www.roblox.com/", "www.roblox.com.evil/", StringComparison.Ordinal),
            "alternate-port" => value.Replace("www.roblox.com/", "www.roblox.com:443/", StringComparison.Ordinal),
            "userinfo" => value.Replace("www.roblox.com/", "www.roblox.com@evil/", StringComparison.Ordinal),
            "fragment" => value + "#ignored",
            "double-place" => value.Replace("placeId=1818", "placeId=%2531%2538%2531%2538", StringComparison.Ordinal),
            "tracker" => value.Replace("browserTrackerId=" + SyntheticHandoff.Tracker, "browserTrackerId=1", StringComparison.Ordinal),
            "origin" => value.Replace("joinAttemptOrigin=PlayButton", "joinAttemptOrigin=JoinUser", StringComparison.Ordinal),
            "private" => value.Replace("request=RequestGame", "request=RequestPrivateGame", StringComparison.Ordinal),
            "launch-data" => value + "&launchData=synthetic",
            "follow" => value.Replace("request=RequestGame", "request=RequestFollowUser", StringComparison.Ordinal),
            _ => value.Replace("isPlayTogetherGame=false", "isPlayTogetherGame=true", StringComparison.Ordinal)
        });
        var result = Capture(context, original, clock);
        Assert.NotEqual(NativeHandoffCaptureStatus.AcceptedSourceGrammar, result.Status); Assert.Null(result.Candidate);
    }

    [Fact]
    public void ASourceDescribedPercentAtEachLayerIsNotBlanketRejected()
    {
        var clock = new HandoffClock(); var context = SyntheticHandoff.Context();
        string uri = SyntheticHandoff.Uri(context, clock, changeDestination: value => value.Replace("joinAttemptOrigin=PlayButton", "joinAttemptOrigin=Play%42utton", StringComparison.Ordinal));
        Assert.Contains("%2542", uri);
        var result = Capture(context, uri, clock); Assert.Equal(NativeHandoffCaptureStatus.AcceptedSourceGrammar, result.Status); result.Candidate!.Dispose();
    }

    [Fact]
    public void WrongJobAndGameOnlyFallbackNeverSatisfyAnExactServerRequest()
    {
        var clock = new HandoffClock(); var context = SyntheticHandoff.Context(true);
        string wrong = SyntheticHandoff.Uri(context, clock, changeDestination: value => value.Replace(SyntheticHandoff.Job, "bbbbbbbb-cccc-4ddd-8eee-ffffffffffff", StringComparison.Ordinal));
        Assert.Equal(NativeHandoffCaptureStatus.DestinationMismatch, Capture(context, wrong, clock).Status);
        Assert.Equal(NativeHandoffCaptureStatus.DestinationMismatch, Capture(context, SyntheticHandoff.Uri(SyntheticHandoff.Context(), clock), clock).Status);
    }

    [Fact]
    public void OriginPageTimestampAndSizeRemainIndependentGuards()
    {
        var clock = new HandoffClock(); var context = SyntheticHandoff.Context(); string uri = SyntheticHandoff.Uri(context, clock);
        Assert.Equal(NativeHandoffCaptureStatus.OriginRejected, NativeRobloxHandoff.Capture(context, "https://www.roblox.com/games/1818", "https://www.roblox.com.evil", uri, clock).Status);
        Assert.Equal(NativeHandoffCaptureStatus.ContextRejected, NativeRobloxHandoff.Capture(context, "https://www.roblox.com/games/1819", RobloxHandoffProbePolicy.OfficialOrigin, uri, clock).Status);
        Assert.Equal(NativeHandoffCaptureStatus.ContextRejected, NativeRobloxHandoff.Capture(null, "https://www.roblox.com/games/1818", RobloxHandoffProbePolicy.OfficialOrigin, uri, clock).Status);
        Assert.Equal(NativeHandoffCaptureStatus.InvalidShape, Capture(context, new string('x', NativeRobloxHandoff.MaximumCharacters + 1), clock).Status);
        clock.Advance(TimeSpan.FromSeconds(31)); Assert.Equal(NativeHandoffCaptureStatus.StaleTimestamp, Capture(context, uri, clock).Status);
    }

    [Fact]
    public void BoundedMalformedInputNeverEscapesIntoAnExceptionOrCandidate()
    {
        var random = new Random(8123); var context = SyntheticHandoff.Context(); var clock = new HandoffClock();
        for (int index = 0; index < 256; index++)
        {
            string value = "roblox-player:1+" + new string(Enumerable.Range(0, random.Next(1, 4096)).Select(_ => (char)random.Next(0, 256)).ToArray());
            var result = Capture(context, value, clock); Assert.Null(result.Candidate); Assert.NotEqual(NativeHandoffCaptureStatus.AcceptedSourceGrammar, result.Status);
        }
    }

    [Fact]
    public void OneOwnedPreparationCannotCaptureASecondCandidateOrReconstructItsGrantOwner()
    {
        var clock = new HandoffClock(); using var context = SyntheticHandoff.Context(); string uri = SyntheticHandoff.Uri(context, clock);
        var first = Capture(context, uri, clock); using var candidate = first.Candidate!;
        Assert.Equal(NativeHandoffCaptureStatus.AcceptedSourceGrammar, first.Status);
        var second = Capture(context, uri, clock);
        Assert.Equal(NativeHandoffCaptureStatus.OperationAlreadyCaptured, second.Status); Assert.Null(second.Candidate);
        using var copy = new NativeHandoffContext(context.LaunchOperationId, context.ProfileId, context.ExpectedUserId, context.PendingAuthorizationGeneration, context.Destination);
        Assert.NotEqual(context.OperationId, copy.OperationId);
        candidate.Dispose(); Assert.Equal(NativeHandoffCaptureStatus.OperationAlreadyCaptured, Capture(context, uri, clock).Status);
    }

    [Fact]
    public void InvalidTrafficDoesNotConsumeTheOneValidCaptureButClosedPreparationDoes()
    {
        var clock = new HandoffClock(); using var context = SyntheticHandoff.Context();
        Assert.Null(Capture(context, "invalid", clock).Candidate);
        using var candidate = SyntheticHandoff.Capture(context, clock);
        using var closed = SyntheticHandoff.Context(); closed.Dispose();
        Assert.Equal(NativeHandoffCaptureStatus.OperationAlreadyCaptured, Capture(closed, SyntheticHandoff.Uri(closed, clock), clock).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactDocumentedWebStartPagePinsItsPlaceAndJobWithoutAcceptingAnotherTarget(bool job)
    {
        var clock = new HandoffClock(); using var context = SyntheticHandoff.Context(job);
        string page = context.Destination.ToWebStartUri().AbsoluteUri, original = SyntheticHandoff.Uri(context, clock);
        Assert.Equal(NativeHandoffCaptureStatus.ContextRejected, NativeRobloxHandoff.Capture(context, page + "&placeId=1819", RobloxHandoffProbePolicy.OfficialOrigin, original, clock).Status);
        Assert.Equal(NativeHandoffCaptureStatus.ContextRejected, NativeRobloxHandoff.Capture(context, page.Replace("1818", "1819", StringComparison.Ordinal), RobloxHandoffProbePolicy.OfficialOrigin, original, clock).Status);
        var result = NativeRobloxHandoff.Capture(context, page, RobloxHandoffProbePolicy.OfficialOrigin, original, clock);
        Assert.Equal(NativeHandoffCaptureStatus.AcceptedSourceGrammar, result.Status); result.Candidate!.Dispose();
    }

    private static NativeHandoffCaptureResult Capture(NativeHandoffContext context, string uri, TimeProvider clock)
        => NativeRobloxHandoff.Capture(context, "https://www.roblox.com/games/" + context.Destination.PlaceId, RobloxHandoffProbePolicy.OfficialOrigin, uri, clock);
    private static string ReplaceTicket(string uri, string replacement)
    { int first = uri.IndexOf("gameinfo:", StringComparison.Ordinal) + 9, last = uri.IndexOf('+', first); return uri[..first] + replacement + uri[last..]; }
}

internal static class SyntheticHandoff
{
    public const string Ticket = "synthetic-short-lived-ticket+/%=never-real", Tracker = "1234567890", Job = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
    public static NativeHandoffContext Context(bool job = false) => new(Guid.NewGuid(), Guid.NewGuid(), 123, 1, new(1818, job ? Job : null, job ? RobloxServerKind.Public : RobloxServerKind.Unknown));
    public static string Uri(NativeHandoffContext context, TimeProvider time, Func<string, string>? changeDestination = null, bool channel = false, string experience = "InApp")
    {
        bool job = context.Destination.JobId is not null;
        string destination = "https://www.roblox.com/Game/PlaceLauncher.ashx?request=" + (job ? "RequestGameJob" : "RequestGame")
            + "&browserTrackerId=" + Tracker + "&placeId=" + context.Destination.PlaceId.ToString(CultureInfo.InvariantCulture)
            + (job ? "&gameId=" + context.Destination.JobId : "") + "&isPlayTogetherGame=false&joinAttemptId=11111111-2222-4333-8444-555555555555&joinAttemptOrigin=" + (job ? "JoinInstance" : "PlayButton");
        if (changeDestination is not null) destination = changeDestination(destination);
        return "roblox-player:1+launchmode:play+gameinfo:" + System.Uri.EscapeDataString(Ticket)
            + "+launchtime:" + time.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
            + "+placelauncherurl:" + System.Uri.EscapeDataString(destination) + "+browsertrackerid:" + Tracker
            + "+robloxLocale:en_us+gameLocale:en_us" + (channel ? "+channel:" : "") + "+LaunchExp:" + experience;
    }
    public static NativeHandoffCandidate Capture(NativeHandoffContext context, TimeProvider clock)
    {
        var result = NativeRobloxHandoff.Capture(context, "https://www.roblox.com/games/" + context.Destination.PlaceId, RobloxHandoffProbePolicy.OfficialOrigin, Uri(context, clock), clock);
        Assert.Equal(NativeHandoffCaptureStatus.AcceptedSourceGrammar, result.Status); return result.Candidate!;
    }
}

internal sealed class HandoffClock : TimeProvider
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 3, 50, 0, TimeSpan.Zero);
    private long _monotonic, _wall;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _monotonic);
    public override DateTimeOffset GetUtcNow() => Start.AddTicks(Interlocked.Read(ref _wall));
    public void Advance(TimeSpan elapsed) { Interlocked.Add(ref _monotonic, elapsed.Ticks); Interlocked.Add(ref _wall, elapsed.Ticks); }
    public void JumpWall(TimeSpan elapsed) => Interlocked.Add(ref _wall, elapsed.Ticks);
}
