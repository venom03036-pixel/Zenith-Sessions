using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class OwnedExperienceBridgeTests
{
    private const long Universe = 8_000_000_001, User = 8_000_000_002;
    private static readonly DateTimeOffset Start = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly OwnedExperienceServer Source = new(9_000_000_001, Guid.Parse("12345678-1234-1234-1234-123456789abc"));
    private static readonly OwnedExperienceServer Destination = new(9_000_000_002, Guid.Parse("23456789-2345-2345-2345-23456789abcd"));

    [Fact]
    public async Task PublishesPinnedMetadataAndReadsExactArrivalWithSeparateKeys()
    {
        var command = Command();
        var clock = new Clock();
        int writes = 0, reads = 0;
        using var bridge = new RobloxOwnedExperienceBridge(Universe,
            _ => { reads++; return ValueTask.FromResult<string?>("fixture-read"); },
            _ => { writes++; return ValueTask.FromResult<string?>("fixture-publish"); },
            new Handler(async (request, ct) =>
            {
                Assert.Equal("apis.roblox.com", request.RequestUri!.Host);
                Assert.Null(request.Headers.Authorization);
                Assert.False(request.Headers.Contains("Cookie"));
                if (request.Method == HttpMethod.Post)
                {
                    Assert.Equal($"/cloud/v2/universes/{Universe}:publishMessage", request.RequestUri.AbsolutePath);
                    Assert.Equal("fixture-publish", request.Headers.GetValues("x-api-key").Single());
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    Assert.Equal("ZenithSessionsV1", body.RootElement.GetProperty("topic").GetString());
                    var written = JsonNode.Parse(body.RootElement.GetProperty("message").GetString()!);
                    Assert.True(JsonNode.DeepEquals(CommandFields(command), written));
                    return new(HttpStatusCode.OK);
                }
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal($"/cloud/v2/universes/{Universe}/data-stores/ZenithSessionsReceiptsV1/scopes/global/entries/u{User}",
                    request.RequestUri.AbsolutePath);
                Assert.Equal("fixture-read", request.Headers.GetValues("x-api-key").Single());
                Assert.Null(request.Content);
                return Json(Receipt(command, "arrived"));
            }), clock);
        var sent = await bridge.PublishAsync(command, default);
        Assert.Equal(OwnedExperienceBridgeState.Published, sent.State);
        Assert.Null(sent.Acknowledgement);
        Assert.Equal(0, reads);
        var observed = await bridge.ReadAsync(command, default);
        Assert.Equal(OwnedExperienceBridgeState.Acknowledged, observed.State);
        Assert.Equal(new(command.OperationId, Universe, User, OwnedExperienceStage.Arrived, Destination, Start), observed.Acknowledgement);
        Assert.Equal(1, reads);
        Assert.Equal(1, writes);
    }

    // The Luau server fixture feeds these exact texts through the shipped script's size cap and JSON decode
    // path, so a change to the published payload must change both suites together.
    [Fact]
    public async Task PublishedCommandJsonMatchesTheLuauFixtureGoldens()
    {
        string fixture;
        using (var stream = typeof(OwnedExperienceBridgeTests).Assembly.GetManifestResourceStream("OwnedExperienceBridgeTests.luau"))
        {
            Assert.True(stream is not null, "The Luau server fixture is not embedded in the test assembly.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            fixture = await reader.ReadToEndAsync();
        }
        static string Golden(string fixture, string name)
        {
            var match = System.Text.RegularExpressions.Regex.Match(fixture, $@"local {name} = \[\[(.*?)\]\]",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            Assert.True(match.Success, $"The Luau server fixture has no {name} payload.");
            return match.Groups[1].Value;
        }

        var operation = Guid.Parse("34567890-3456-3456-3456-34567890abcd");
        var move = new OwnedExperienceCommand(operation, Universe, User, Source, Destination, Start);
        var status = new OwnedExperienceCommand(operation, Universe, User, Source, null, Start);
        Assert.Equal(Golden(fixture, "goldenMove"), await PublishedMessage(move, Start));
        Assert.Equal(Golden(fixture, "goldenStatus"), await PublishedMessage(status, Start));

        // The script ignores any message over 1024 bytes. The widest command it can accept must fit.
        const long widest = 9_007_199_254_740_991;
        var latest = DateTimeOffset.MaxValue - OwnedExperienceCommand.Lifetime;
        var largest = new OwnedExperienceCommand(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), widest, widest,
            new(widest, Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffffe")), new(widest, Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffffd")), latest);
        var widestMessage = await PublishedMessage(largest, latest);
        Assert.True(Encoding.UTF8.GetByteCount(widestMessage) <= 1024,
            $"The widest command is {Encoding.UTF8.GetByteCount(widestMessage)} bytes, over the script's 1024-byte limit.");
    }

    [Theory]
    [InlineData("atSource", false, OwnedExperienceStage.AtSource)]
    [InlineData("received", true, OwnedExperienceStage.Received)]
    [InlineData("teleportInitiated", true, OwnedExperienceStage.TeleportInitiated)]
    [InlineData("rejected", true, OwnedExperienceStage.Rejected)]
    public async Task SourceStatesRemainDistinct(string wireStage, bool join, OwnedExperienceStage expected)
    {
        var command = Command(join);
        using var bridge = Fixture((_, _) => Task.FromResult(Json(Receipt(command, wireStage))));
        var result = await bridge.ReadAsync(command, default);
        Assert.Equal(OwnedExperienceBridgeState.Acknowledged, result.State);
        Assert.Equal(expected, result.Acknowledgement?.Stage);
        Assert.Equal(Source, result.Acknowledgement?.Server);
    }

    // The server script accepts a command when issued <= serverNow + 2, so a Roblox clock up to two
    // seconds behind the PC stamps a valid receipt earlier than the PC's issued second.
    [Theory]
    [InlineData(-2, "atSource")]
    [InlineData(-1, "atSource")]
    [InlineData(-1, "received")]
    [InlineData(-2, "rejected")]
    [InlineData(2, "atSource")]
    public async Task ReceiptStampedWithinTheScriptClockAllowanceIsAcknowledged(int offsetSeconds, string wireStage)
    {
        var command = Command(join: wireStage != "atSource");
        var receipt = Receipt(command, wireStage);
        receipt["value"]!["observedUtc"] = Start.AddSeconds(offsetSeconds).ToUnixTimeSeconds();
        using var bridge = Fixture((_, _) => Task.FromResult(Json(receipt)));
        var result = await bridge.ReadAsync(command, default);
        Assert.Equal(OwnedExperienceBridgeState.Acknowledged, result.State);
        Assert.Equal(Start.AddSeconds(offsetSeconds), result.Acknowledgement?.ObservedUtc);
    }

    [Theory]
    [InlineData("universeId", 5_000_000_001)]
    [InlineData("userId", 5_000_000_002)]
    [InlineData("sourcePlaceId", 5_000_000_003)]
    [InlineData("destinationPlaceId", 5_000_000_004)]
    [InlineData("version", 2)]
    [InlineData("issuedUtc", 1)]
    [InlineData("expiresUtc", 1)]
    public async Task RejectsMismatchedPinnedCommandFields(string field, long value)
    {
        var command = Command();
        var receipt = Receipt(command, "arrived");
        receipt["value"]!["command"]![field] = value;
        using var bridge = Fixture((_, _) => Task.FromResult(Json(receipt)));
        Assert.Equal(OwnedExperienceBridgeState.InvalidResponse, (await bridge.ReadAsync(command, default)).State);
    }

    [Theory]
    [InlineData("wrong server")]
    [InlineData("wrong place")]
    [InlineData("wrong stage")]
    [InlineData("future")]
    [InlineData("expired observation")]
    [InlineData("duplicate")]
    [InlineData("oversize")]
    public async Task InvalidReceiptNeverBecomesAnArrival(string defect)
    {
        var command = Command();
        var receipt = Receipt(command, "arrived");
        var value = receipt["value"]!;
        switch (defect)
        {
            case "wrong server": value["observedJobId"] = Source.JobId; break;
            case "wrong place": value["observedPlaceId"] = Source.PlaceId; break;
            case "wrong stage": value["stage"] = "atSource"; break;
            case "future": value["observedUtc"] = Start.AddSeconds(3).ToUnixTimeSeconds(); break;
            case "expired observation": value["observedUtc"] = Start.AddSeconds(-3).ToUnixTimeSeconds(); break;
        }
        string json = receipt.ToJsonString();
        if (defect == "duplicate") json = json.Insert(1, "\"value\":null,");
        if (defect == "oversize") json += new string(' ', 4096);
        using var bridge = Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(json)))
        }));
        var result = await bridge.ReadAsync(command, default);
        Assert.Equal(OwnedExperienceBridgeState.InvalidResponse, result.State);
        Assert.Null(result.Acknowledgement);
    }

    [Fact]
    public async Task MissingOrEarlierOperationIsPendingUntilExpiry()
    {
        var command = Command();
        var earlier = Command();
        var clock = new Clock();
        int calls = 0;
        using var bridge = Fixture((_, _) => Task.FromResult(++calls == 1 ? new(HttpStatusCode.NotFound)
            : Json(Receipt(earlier, "arrived"))), clock);
        Assert.Equal(OwnedExperienceBridgeState.Pending, (await bridge.ReadAsync(command, default)).State);
        var paced = await bridge.ReadAsync(command, default);
        Assert.Equal(OwnedExperienceBridgeState.RateLimited, paced.State);
        Assert.Equal(Start.AddMilliseconds(500), paced.RetryUtc);
        clock.Now = Start.AddMilliseconds(500);
        Assert.Equal(OwnedExperienceBridgeState.Pending, (await bridge.ReadAsync(command, default)).State);
        clock.Now = command.ExpiresUtc;
        Assert.Equal(OwnedExperienceBridgeState.Expired, (await bridge.ReadAsync(command, default)).State);
        Assert.Equal(OwnedExperienceBridgeState.Expired, (await bridge.PublishAsync(command, default)).State);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RateLimitAppliesOnlyToItsRouteWithoutRetryingPublication()
    {
        var command = Command();
        var clock = new Clock();
        int calls = 0;
        using var bridge = Fixture((request, _) =>
        {
            calls++;
            if (request.Method == HttpMethod.Get) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (calls > 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            var blocked = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            blocked.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(10));
            return Task.FromResult(blocked);
        }, clock);
        var first = await bridge.PublishAsync(command, default);
        Assert.Equal(OwnedExperienceBridgeState.RateLimited, first.State);
        Assert.Equal(Start.AddSeconds(10), first.RetryUtc);
        Assert.Equal(first, await bridge.PublishAsync(command, default));
        Assert.Equal(OwnedExperienceBridgeState.Pending, (await bridge.ReadAsync(command, default)).State);
        Assert.Equal(2, calls);
        clock.Now = Start.AddSeconds(10);
        Assert.Equal(OwnedExperienceBridgeState.Published, (await bridge.PublishAsync(command, default)).State);
        Assert.Equal(Start.AddSeconds(11), (await bridge.PublishAsync(command, default)).RetryUtc);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task CancelledRequestsHoldTheirLaneUntilTheTransportStops()
    {
        var command = Command();
        var clock = new Clock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var bridge = Fixture(async (_, _) =>
        {
            if (++calls == 1)
            {
                entered.SetResult();
                await release.Task;
            }
            return Json(Receipt(command, "arrived"));
        }, clock);
        using var stop = new CancellationTokenSource();
        var first = bridge.ReadAsync(command, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();
        try
        {
            Assert.Equal(OwnedExperienceBridgeState.Busy, (await bridge.PublishAsync(command, default)).State);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);
        clock.Now = Start.AddSeconds(1);
        Assert.Equal(OwnedExperienceBridgeState.Acknowledged, (await bridge.ReadAsync(command, default)).State);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RejectsMissingCredentialsAndOtherExperiencesBeforeNetworkAccess()
    {
        using var bridge = new RobloxOwnedExperienceBridge(Universe, _ => ValueTask.FromResult<string?>(null),
            _ => ValueTask.FromResult<string?>("fixture\r\nunsafe"),
            new Handler((_, _) => throw new InvalidOperationException("A request was not expected.")), new Clock());
        Assert.Equal(OwnedExperienceBridgeState.AuthorizationRequired, (await bridge.ReadAsync(Command(), default)).State);
        Assert.Equal(OwnedExperienceBridgeState.AuthorizationRequired, (await bridge.PublishAsync(Command(), default)).State);
        var other = new OwnedExperienceCommand(Guid.NewGuid(), Universe + 1, User, Source, Destination, Start);
        await Assert.ThrowsAsync<ArgumentException>(() => bridge.PublishAsync(other, default));
        Assert.Throws<ArgumentException>(() => new OwnedExperienceCommand(Guid.NewGuid(), Universe, long.MaxValue, Source, null, Start));
        Assert.Throws<ArgumentException>(() => new OwnedExperienceCommand(Guid.NewGuid(), Universe, User, Source, Source, Start));
    }

    private static OwnedExperienceCommand Command(bool join = true)
        => new(Guid.NewGuid(), Universe, User, Source, join ? Destination : null, Start);

    private static async Task<string> PublishedMessage(OwnedExperienceCommand command, DateTimeOffset now)
    {
        string? message = null;
        using var bridge = new RobloxOwnedExperienceBridge(command.UniverseId, _ => ValueTask.FromResult<string?>("fixture-read"),
            _ => ValueTask.FromResult<string?>("fixture-publish"), new Handler(async (request, ct) =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                message = body.RootElement.GetProperty("message").GetString();
                return new(HttpStatusCode.OK);
            }), new Clock { Now = now });
        Assert.Equal(OwnedExperienceBridgeState.Published, (await bridge.PublishAsync(command, default)).State);
        Assert.NotNull(message);
        return message;
    }

    private static JsonObject CommandFields(OwnedExperienceCommand command) => new()
    {
        ["version"] = 1, ["operationId"] = command.OperationId, ["universeId"] = command.UniverseId, ["userId"] = command.UserId,
        ["sourcePlaceId"] = command.Source.PlaceId, ["sourceJobId"] = command.Source.JobId,
        ["destinationPlaceId"] = command.Destination?.PlaceId, ["destinationJobId"] = command.Destination?.JobId,
        ["issuedUtc"] = command.IssuedUtc.ToUnixTimeSeconds(), ["expiresUtc"] = command.ExpiresUtc.ToUnixTimeSeconds()
    };

    private static JsonObject Receipt(OwnedExperienceCommand command, string stage)
    {
        var server = stage == "arrived" ? Destination : Source;
        return new()
        {
            ["value"] = new JsonObject
            {
                ["command"] = CommandFields(command), ["stage"] = stage, ["observedUtc"] = Start.ToUnixTimeSeconds(),
                ["observedJobId"] = server.JobId, ["observedPlaceId"] = server.PlaceId
            }
        };
    }

    private static HttpResponseMessage Json(JsonObject body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static RobloxOwnedExperienceBridge Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, Clock? clock = null)
        => new(Universe, _ => ValueTask.FromResult<string?>("fixture-read"), _ => ValueTask.FromResult<string?>("fixture-publish"),
            new Handler(send), clock ?? new Clock());

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
