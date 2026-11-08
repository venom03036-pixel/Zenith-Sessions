using System.Globalization;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// R-api-7 (owner decision 2026-11-08): the Direct ticket native launch mode. A fake ticket client stands in for Roblox;
// the request Zenith builds must pass the same grammar the official-page capture validates, and nothing is rewritten after it.
public sealed class DirectTicketNativeHandoffCaptureTests
{
    private const string Ticket = "synthetic-direct-ticket+/=never-real";
    private const string Secret = "synthetic-direct-session";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnIssuedTicketBecomesOneAcceptedRequestInTheSharedGrammarForTheFrozenDestination(bool job)
    {
        var tickets = new Tickets(); var clock = new HandoffClock(); var context = SyntheticHandoff.Context(job);
        var capture = new DirectTicketNativeHandoffCapture(tickets, clock);
        using var credential = new RobloxSessionCredential(Secret, DateTimeOffset.UtcNow.AddDays(1));
        using var result = await capture.CaptureAsync(context, credential, default);
        Assert.Equal(RobloxHandoffProbeOutcome.TicketIssued, result.Outcome); Assert.Equal(NativeLaunchMode.DirectTicket, result.Mode);
        Assert.Equal(NativeHandoffCaptureStatus.AcceptedSourceGrammar, result.ShapeStatus);
        Assert.Equal(new RobloxShareLinkTokenScope(context.ProfileId, context.ExpectedUserId), tickets.Scope); Assert.Equal(Secret, tickets.SeenCookie);
        Assert.True(tickets.Issued!.IsConsumed); // The ticket moved into the request and was zeroed.
        var candidate = result.Candidate!; Assert.Same(context, candidate.Context); Assert.False(candidate.IsConsumed); Assert.False(candidate.IsExpired);
        Assert.InRange(candidate.BrowserTrackerId, 1, LaunchProcessReceipt.MaximumBrowserTrackerId);
        // The same credential travels on for the existing same-player verification, as the page's current cookie does.
        Assert.Equal(Secret, result.CurrentCredential!.CopyValue()); Assert.Equal(credential.ExpiresUtc, result.CurrentCredential.ExpiresUtc);
        // The built request, read through the one-use start boundary: Roblox's own grammar, the frozen destination, a fresh join attempt.
        using var start = new StartBoundary(); bool entered = false;
        Assert.Equal(NativeHandoffDispatchOutcome.NotStarted, candidate.Invoke(start, default, ref entered).Outcome); Assert.True(entered);
        string original = start.Observed!;
        Assert.StartsWith("roblox-player:1+launchmode:play+gameinfo:", original, StringComparison.Ordinal);
        var fields = original["roblox-player:1+".Length..].Split('+').Select(piece => piece.Split(':', 2)).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
        Assert.Equal(Ticket, Uri.UnescapeDataString(fields["gameinfo"]));
        Assert.Equal(clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), fields["launchtime"]);
        Assert.Equal(candidate.BrowserTrackerId.ToString(CultureInfo.InvariantCulture), fields["browsertrackerid"]);
        Assert.Equal("en_us", fields["robloxLocale"]); Assert.Equal("en_us", fields["gameLocale"]); Assert.Equal("InApp", fields["LaunchExp"]); Assert.Equal("", fields["channel"]);
        string destination = Uri.UnescapeDataString(fields["placelauncherurl"]);
        string expected = "https://www.roblox.com/Game/PlaceLauncher.ashx?request=" + (job ? "RequestGameJob" : "RequestGame") + "&browserTrackerId=" + fields["browsertrackerid"]
            + "&placeId=1818" + (job ? "&gameId=" + SyntheticHandoff.Job : "") + "&isPlayTogetherGame=false&joinAttemptId=";
        Assert.StartsWith(expected, destination, StringComparison.Ordinal);
        string rest = destination[expected.Length..];
        Assert.True(Guid.TryParseExact(rest[..36], "D", out var attempt) && attempt != Guid.Empty); Assert.Equal("&joinAttemptOrigin=" + (job ? "JoinInstance" : "PlayButton"), rest[36..]);
        // Twice through the same grammar the browser path uses: the request Zenith built is accepted as-is, so nothing was rewritten.
        var reparsed = NativeRobloxHandoff.AcceptBuilt(new(context.LaunchOperationId, context.ProfileId, context.ExpectedUserId, context.PendingAuthorizationGeneration, context.Destination), original, clock);
        Assert.Equal(NativeHandoffCaptureStatus.AcceptedSourceGrammar, reparsed.Status); reparsed.Candidate!.Dispose();
        string diagnostic = result + JsonSerializer.Serialize(result) + candidate + JsonSerializer.Serialize(candidate) + capture.ToString();
        Assert.DoesNotContain(Ticket, diagnostic, StringComparison.Ordinal); Assert.DoesNotContain("gameinfo", diagnostic, StringComparison.Ordinal); Assert.DoesNotContain(Secret, diagnostic, StringComparison.Ordinal);
        Assert.Null(capture.Refusal);
    }

    [Fact]
    public async Task ARotatedSessionCookieIsTheCurrentCredentialAndTheInputIsNeverRetained()
    {
        var tickets = new Tickets { Rotated = "synthetic-direct-rotated" }; var context = SyntheticHandoff.Context();
        var capture = new DirectTicketNativeHandoffCapture(tickets, new HandoffClock());
        using var credential = new RobloxSessionCredential(Secret);
        using var result = await capture.CaptureAsync(context, credential, default);
        Assert.Equal(RobloxHandoffProbeOutcome.TicketIssued, result.Outcome);
        Assert.Equal("synthetic-direct-rotated", result.CurrentCredential!.CopyValue());
        Assert.Equal(Secret, credential.CopyValue()); // Borrowed, not disposed, by the capture.
    }

    [Theory]
    [InlineData(RobloxLaunchTicketOutcome.SignInRequired, RobloxHandoffProbeOutcome.SignInRequired)]
    [InlineData(RobloxLaunchTicketOutcome.Challenge, RobloxHandoffProbeOutcome.SignInRequired)]
    [InlineData(RobloxLaunchTicketOutcome.Refused, RobloxHandoffProbeOutcome.Refused)]
    [InlineData(RobloxLaunchTicketOutcome.RateLimited, RobloxHandoffProbeOutcome.RateLimited)]
    [InlineData(RobloxLaunchTicketOutcome.Unavailable, RobloxHandoffProbeOutcome.Failed)]
    public async Task EveryRefusalIsATypedOutcomeWithNoCandidateAndTheContextStaysUnclaimed(RobloxLaunchTicketOutcome refusal, RobloxHandoffProbeOutcome expected)
    {
        var tickets = new Tickets { Outcome = refusal }; var context = SyntheticHandoff.Context();
        var capture = new DirectTicketNativeHandoffCapture(tickets, new HandoffClock());
        using var credential = new RobloxSessionCredential(Secret);
        using var result = await capture.CaptureAsync(context, credential, default);
        Assert.Equal(expected, result.Outcome); Assert.Equal(NativeLaunchMode.DirectTicket, result.Mode); Assert.Null(result.Candidate); Assert.Null(result.ShapeStatus);
        Assert.True(context.CanDispatch == false && context.TryClaimCapture()); // Nothing claimed the operation; a later capture still could.
    }

    [Fact]
    public async Task OneRequestAtATimeAndTheCallersCancellationThrowsWithoutAResult()
    {
        var tickets = new Tickets(); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tickets.BeforeAnswer = async ct => { entered.TrySetResult(); await gate.Task.WaitAsync(ct); };
        var capture = new DirectTicketNativeHandoffCapture(tickets, new HandoffClock());
        using var credential = new RobloxSessionCredential(Secret); using var stop = new CancellationTokenSource();
        var first = capture.CaptureAsync(SyntheticHandoff.Context(), credential, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(RobloxHandoffProbeOutcome.Busy, capture.Refusal);
        using (var busy = await capture.CaptureAsync(SyntheticHandoff.Context(), credential, default)) Assert.Equal(RobloxHandoffProbeOutcome.Busy, busy.Outcome);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Null(capture.Refusal); Assert.Equal(1, tickets.Calls); // The busy answer never reached the ticket client.
        gate.TrySetResult();
    }

    [Fact]
    public void TheBuiltRequestIsRejectedByTheGrammarWhenItNamesAnotherDestinationAndTheBuilderRefusesBadInputs()
    {
        var clock = new HandoffClock(); var context = SyntheticHandoff.Context();
        using (var ticket = new ProtectedLaunchTicket(Ticket))
        {
            string original = NativeRobloxHandoffRequest.Build(context, ticket, 1234567890, Guid.NewGuid(), clock);
            Assert.True(ticket.IsConsumed);
            var other = new NativeHandoffContext(context.LaunchOperationId, context.ProfileId, context.ExpectedUserId, context.PendingAuthorizationGeneration, new(1819));
            Assert.Equal(NativeHandoffCaptureStatus.DestinationMismatch, NativeRobloxHandoff.AcceptBuilt(other, original, clock).Status);
            clock.JumpWall(TimeSpan.FromSeconds(31));
            Assert.Equal(NativeHandoffCaptureStatus.StaleTimestamp, NativeRobloxHandoff.AcceptBuilt(context, original, clock).Status);
            Assert.Equal(NativeHandoffCaptureStatus.ContextRejected, NativeRobloxHandoff.AcceptBuilt(null, original, clock).Status);
            Assert.Equal(NativeHandoffCaptureStatus.InvalidShape, NativeRobloxHandoff.AcceptBuilt(context, "roblox-player:1+launchmode:play", clock).Status);
            Assert.Throws<ObjectDisposedException>(() => NativeRobloxHandoffRequest.Build(context, ticket, 1234567890, Guid.NewGuid(), clock)); // One use.
        }
        using (var ticket = new ProtectedLaunchTicket(Ticket))
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => NativeRobloxHandoffRequest.Build(context, ticket, 0, Guid.NewGuid(), clock));
            Assert.Throws<ArgumentOutOfRangeException>(() => NativeRobloxHandoffRequest.Build(context, ticket, LaunchProcessReceipt.MaximumBrowserTrackerId + 1, Guid.NewGuid(), clock));
            Assert.Throws<ArgumentException>(() => NativeRobloxHandoffRequest.Build(context, ticket, 1, Guid.Empty, clock));
            Assert.False(ticket.IsConsumed); // A refused build spends nothing.
        }
        for (int attempt = 0; attempt < 64; attempt++) Assert.InRange(NativeRobloxHandoffRequest.NewBrowserTrackerId(), 1, LaunchProcessReceipt.MaximumBrowserTrackerId);
    }

    private sealed class Tickets : IRobloxLaunchTicketClient
    {
        public int Calls; public RobloxShareLinkTokenScope Scope; public string? SeenCookie; public string? Rotated;
        public RobloxLaunchTicketOutcome Outcome = RobloxLaunchTicketOutcome.Issued;
        public ProtectedLaunchTicket? Issued;
        public Func<CancellationToken, Task>? BeforeAnswer;
        public async Task<RobloxLaunchTicketResult> RequestAsync(RobloxSessionCredential credential, RobloxShareLinkTokenScope scope, CancellationToken ct)
        {
            Calls++; Scope = scope; SeenCookie = credential.CopyValue();
            if (BeforeAnswer is not null) await BeforeAnswer(ct);
            ct.ThrowIfCancellationRequested();
            if (Outcome != RobloxLaunchTicketOutcome.Issued) return new(Outcome);
            Issued = new ProtectedLaunchTicket(Ticket);
            return new(RobloxLaunchTicketOutcome.Issued, Issued, Rotated is null ? null : new RobloxSessionCredential(Rotated));
        }
        public void ForgetTokens(Guid profileId) { }
        public void ForgetAllTokens() { }
    }
    private sealed class StartBoundary : IPreparedNativeHandoffStart
    {
        public string? Observed;
        public NativeHandoffDispatchResult Start(string officialHandoff) { Observed = officialHandoff; return new(NativeHandoffDispatchOutcome.NotStarted); }
        public void Dispose() { }
    }
}
