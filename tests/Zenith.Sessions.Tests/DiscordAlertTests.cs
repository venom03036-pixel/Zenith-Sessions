using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Discord webhook alerts (R-api-12, also R-shell-22): an opt-in, off by default, that posts fixed-template messages to one saved
// Discord webhook when a client crashes, stops responding or is disconnected, and when a launch batch has finished. The webhook URL
// is a secret: DPAPI-protected on disk, masked in Settings, never logged. Sends are coalesced, bounded (50 queued, 10 lines per
// message, one message per 2 s) and never retried in a storm: a 429 waits once for Discord's Retry-After; any other failure is
// recorded by type and shown once. Panic stops the outbox for this run; Lock leaves it (monitoring continues); Exit disposes it.
public sealed class DiscordAlertTests : IDisposable
{
    private const string Token = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789abcdefghijklmnopqrstuvwxyz-_AbCd";
    private const string Url = "https://discord.com/api/webhooks/123456789012345678/" + Token;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-discord-alerts-" + Guid.NewGuid().ToString("N"));
    private string Primary => Path.Combine(_root, "settings.json");
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly TimeSpan Positive = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LockDuringPendingSettingsActivationPreservesDiscordMonitoring(bool startup) => OnDispatcherAsync(async () =>
    {
        if (startup)
            using (var store = new SettingsStore(_root))
                await store.SaveAsync(new SessionsSettings(DiscordAlerts: true), default);
        var transport = new TransportProbe();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new ClientMonitor(),
            discordWebhookStore: new StoreProbe(), discordTransport: transport);
        if (!startup) await vm.InitializeAsync();
        Task pending;
        using (var held = new HeldPreferencesWrite(vm, initialLoad: startup))
        {
            if (startup) pending = vm.InitializeAsync();
            else { vm.DiscordAlerts = true; vm.SaveSettings.Execute(null); pending = vm.SaveSettings.Execution; }
            Assert.False(pending.IsCompleted);
            vm.LockDashboard();
        }
        await pending;
        Assert.True(vm.IsReady, vm.Notice);
        Assert.True(vm.IsLocked);
        Assert.True(vm.IsDiscordAlertsActive);
        Assert.False(vm.IsDiscordAlertsStopped);
        Assert.Empty(transport.Requests);
    });

    [Fact]
    public Task AnEarlierInitializationCannotRestartDiscordAlertsAfterPanic() => OnDispatcherAsync(async () =>
    {
        using (var store = new SettingsStore(_root))
            await store.SaveAsync(new SessionsSettings(DiscordAlerts: true), default);
        var transport = new TransportProbe();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new ClientMonitor(),
            discordWebhookStore: new StoreProbe(), discordTransport: transport);
        Task initialization;
        using (var held = new HeldPreferencesWrite(vm, initialLoad: true))
        {
            initialization = vm.InitializeAsync(); Assert.False(initialization.IsCompleted);
            vm.Panic.Execute(null);
        }
        await initialization;
        Assert.True(vm.IsReady, vm.Notice);
        Assert.False(vm.IsDiscordAlertsActive);
        Assert.True(vm.IsDiscordAlertsStopped);
        Assert.Empty(transport.Requests);
    });

    [Fact]
    public Task AnEarlierPendingSettingsSaveCannotRestartDiscordAlertsAfterPanic() => OnDispatcherAsync(async () =>
    {
        var transport = new TransportProbe();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new ClientMonitor(),
            discordWebhookStore: new StoreProbe(), discordTransport: transport);
        await vm.InitializeAsync();
        using (var held = new HeldPreferencesWrite(vm))
        {
            vm.DiscordAlerts = true; vm.SaveSettings.Execute(null);
            Assert.False(vm.SaveSettings.Execution.IsCompleted);
            vm.Panic.Execute(null);
        }
        await vm.SaveSettings.Execution;
        Assert.False(vm.IsDiscordAlertsActive);
        Assert.Empty(transport.Requests);
    });

    [Theory]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/" + Token, true, "discord.com")]
    [InlineData("https://discordapp.com/api/webhooks/123456789012345678/" + Token, true, "discordapp.com")]
    [InlineData("https://ptb.discord.com/api/v10/webhooks/123456789012345678/" + Token, true, "ptb.discord.com")]
    [InlineData("  https://discord.com/api/webhooks/123456789012345678/" + Token + "  ", true, "discord.com")]
    [InlineData("http://discord.com/api/webhooks/123456789012345678/" + Token, false, null)]
    [InlineData("https://discord.com.example.net/api/webhooks/123456789012345678/" + Token, false, null)]
    [InlineData("https://evil.example/api/webhooks/123456789012345678/" + Token, false, null)]
    [InlineData("https://user:pw@discord.com/api/webhooks/123456789012345678/" + Token, false, null)]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/" + Token + "?wait=true", false, null)]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/" + Token + "#x", false, null)]
    [InlineData("https://discord.com/api/webhooks/abc/" + Token, false, null)]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/short", false, null)]
    [InlineData("https://discord.com/api/channels/123456789012345678/" + Token, false, null)]
    [InlineData("https://discord.com:8443/api/webhooks/123456789012345678/" + Token, false, null)]
    [InlineData("", false, null)]
    [InlineData(null, false, null)]
    public void WebhookAddressesAreAcceptedOnlyForExactDiscordHostsOverHttpsWithTheWebhookPathShape(string? text, bool accepted, string? host)
    {
        bool parsed = DiscordWebhookAddress.TryParse(text, out var address, out string problem);
        Assert.True(parsed == accepted, $"'{text}' was {(parsed ? "accepted" : "refused")}: {problem}");
        if (!accepted) { Assert.Null(address); Assert.False(string.IsNullOrWhiteSpace(problem)); return; }
        Assert.NotNull(address);
        using (address)
        {
            Assert.Equal(host, address.Host);
            Assert.Equal("AbCd", address.Last4);
            // Redacted everywhere a secret could leak by accident (Rule 10).
            Assert.DoesNotContain(Token, address.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Token, problem, StringComparison.Ordinal);
            string revealed = ""; address.Reveal(span => revealed = new string(span));
            Assert.Equal(text!.Trim(), revealed);
        }
        Assert.Throws<ObjectDisposedException>(() => address!.Reveal(_ => { }));
    }

    [Fact]
    public void MessagesUseFixedTemplatesThatCarryOnlyLabelsPlacesAndCountsAndStayBounded()
    {
        var at = new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal("Client crashed: account `Alpha`, place 123456.", new AlertEvent(AlertKind.ClientCrashed, at, "Alpha", 123456).Line);
        Assert.Equal("Client may have crashed (a Roblox crash dump was written as it ended): a client with no matching saved account.",
            new AlertEvent(AlertKind.ClientPossiblyCrashed, at).Line);
        Assert.Equal("Client not responding: account `Beta`.", new AlertEvent(AlertKind.ClientNotResponding, at, "Beta").Line);
        Assert.Equal("Client disconnected (reason 277): account `Beta`, place 77.", new AlertEvent(AlertKind.ClientDisconnected, at, "Beta", 77, 277).Line);
        Assert.Equal("Client disconnected because the account launched in another client: account `Beta`.",
            new AlertEvent(AlertKind.ClientDisconnected, at, "Beta", DisconnectReason: ClientLogObservation.AccountLaunchedElsewhereReason).Line);
        Assert.Equal("Launch batch for place 9001 finished: 4 requests, 2 arrived, 1 started unverified, 0 need review, 1 failed, 0 cancelled.",
            new AlertEvent(AlertKind.BatchCompleted, at, Batch: new(9001, 4, 2, 1, 0, 1, 0, 0)).Line);
        Assert.Contains("Test alert", new AlertEvent(AlertKind.Test, at).Line, StringComparison.Ordinal);

        // A label is display text: control characters, Discord markup and length are bounded before it is sent.
        // A backtick would end the label's code span, so it becomes an apostrophe; control characters are dropped.
        Assert.Equal("a'b", AlertMessages.NormalizeLabel("a`b" + (char)7 + " "));
        Assert.Equal(AlertMessages.MaximumLabelLength + 1, AlertMessages.NormalizeLabel(new string('x', 200))!.Length);
        Assert.Null(AlertMessages.NormalizeLabel("  "));

        var events = Enumerable.Range(0, 3).Select(i => new AlertEvent(AlertKind.ClientNotResponding, at, "Account " + i)).ToArray();
        string content = AlertMessages.Compose(events, dropped: 2);
        Assert.StartsWith("**Zenith Sessions** · 3 alerts", content, StringComparison.Ordinal);
        Assert.All(events, e => Assert.Contains(e.Line, content, StringComparison.Ordinal));
        Assert.Contains("2 earlier alerts were dropped because the queue was full.", content, StringComparison.Ordinal);
        Assert.StartsWith("**Zenith Sessions** · 1 alert\n", AlertMessages.Compose([events[0]], 0), StringComparison.Ordinal);
        Assert.Equal(50, AlertMessages.MaximumQueued); Assert.Equal(10, AlertMessages.MaximumLinesPerMessage);
        Assert.Equal(TimeSpan.FromSeconds(2), AlertMessages.MinimumGap);
        string huge = AlertMessages.Compose(Enumerable.Range(0, 10).Select(_ => new AlertEvent(AlertKind.ClientCrashed, at, new string('y', 40), long.MaxValue)).ToArray(), 0);
        Assert.InRange(huge.Length, 1, AlertMessages.MaximumContentLength);
    }

    [Fact]
    public async Task TheOutboxCoalescesQueuedAlertsSendsAtMostOneMessagePerGapAndDropsTheOldestWhenFull()
    {
        var transport = new TransportProbe();
        var reports = new List<AlertDeliveryReport>();
        var gap = TimeSpan.FromMilliseconds(300);
        await using var outbox = new DiscordAlertOutbox(transport, _ => Task.FromResult<DiscordWebhookAddress?>(Address()), gap);
        outbox.Reported += report => { lock (reports) reports.Add(report); };
        var at = DateTimeOffset.UtcNow;
        // Nothing runs, and nothing is kept, until Start: an alert before that is refused, not queued (Rule 12).
        Assert.False(outbox.IsRunning);
        Assert.False(outbox.Enqueue(new(AlertKind.Test, at)));
        outbox.Start(); Assert.True(outbox.IsRunning);
        transport.Hold = true;
        for (int i = 0; i < 5; i++) Assert.True(outbox.Enqueue(new(AlertKind.ClientNotResponding, at, "Account " + i)));
        transport.Release();
        var first = await transport.Sent(1);
        Assert.Equal(5, first.Content.Split('\n').Count(line => line.StartsWith("Client not responding", StringComparison.Ordinal)));
        Assert.StartsWith("**Zenith Sessions** · 5 alerts", first.Content, StringComparison.Ordinal);

        // A storm: the queue keeps the newest 50, each message carries at most 10 lines, and the first says what was dropped.
        transport.Hold = true;
        for (int i = 0; i < 60; i++) outbox.Enqueue(new(AlertKind.ClientDisconnected, at, "Storm " + i, 1, 277));
        transport.Release();
        var second = await transport.Sent(2);
        Assert.Equal(10, second.Content.Split('\n').Count(line => line.StartsWith("Client disconnected", StringComparison.Ordinal)));
        Assert.Contains("10 earlier alerts were dropped because the queue was full.", second.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Storm 0`", second.Content, StringComparison.Ordinal); // The oldest went first.
        Assert.Contains("Storm 10`", second.Content, StringComparison.Ordinal);
        var third = await transport.Sent(3);
        Assert.True(third.At - second.At >= gap - TimeSpan.FromMilliseconds(50), $"Two messages went {(third.At - second.At).TotalMilliseconds} ms apart; the gap is {gap.TotalMilliseconds} ms.");
        Assert.DoesNotContain("dropped", third.Content, StringComparison.Ordinal);
        await transport.Sent(6);
        await WaitUntil(() => { lock (reports) return reports.Count >= 6; });
        lock (reports)
        {
            Assert.All(reports, r => Assert.Equal(AlertDeliveryStatus.Delivered, r.Result.Status));
            Assert.Equal(55, reports.Sum(r => r.Alerts)); Assert.Equal(10, reports.Sum(r => r.Dropped));
        }
        Assert.All(transport.Requests, r => Assert.Equal("AbCd", r.Last4));
        Assert.False(transport.Disposed);
    }

    [Fact]
    public async Task ARateLimitedSendWaitsForRetryAfterOnceAndEveryOtherFailureIsReportedAndNeverRetried()
    {
        var transport = new TransportProbe();
        var reports = new List<AlertDeliveryReport>();
        transport.Answers.Enqueue(new(AlertDeliveryStatus.RateLimited, 429, TimeSpan.FromMilliseconds(120)));
        transport.Answers.Enqueue(new(AlertDeliveryStatus.Delivered, 204));
        transport.Answers.Enqueue(new(AlertDeliveryStatus.Unavailable, 503));
        transport.Answers.Enqueue(new(AlertDeliveryStatus.Rejected, 404));
        await using var outbox = new DiscordAlertOutbox(transport, _ => Task.FromResult<DiscordWebhookAddress?>(Address()), TimeSpan.Zero);
        outbox.Reported += report => { lock (reports) reports.Add(report); };
        outbox.Start();
        var at = DateTimeOffset.UtcNow;
        outbox.Enqueue(new(AlertKind.Test, at));
        var retried = await transport.Sent(2);
        Assert.Equal(transport.Requests[0].Content, retried.Content); // The same message, once more, after Discord's wait.
        Assert.True(retried.At - transport.Requests[0].At >= TimeSpan.FromMilliseconds(100), "The 429 retry did not wait for Retry-After.");
        await WaitUntil(() => { lock (reports) return reports.Count >= 1; });
        lock (reports) Assert.Equal(AlertDeliveryStatus.Delivered, reports[0].Result.Status);

        outbox.Enqueue(new(AlertKind.ClientCrashed, at, "Gamma", 5));
        await transport.Sent(3);
        outbox.Enqueue(new(AlertKind.ClientCrashed, at, "Delta", 5));
        await transport.Sent(4);
        await WaitUntil(() => { lock (reports) return reports.Count >= 3; });
        await Task.Delay(200);
        Assert.Equal(4, transport.Requests.Count); // No retry for 503 or 404.
        lock (reports)
        {
            Assert.Equal(AlertDeliveryStatus.Unavailable, reports[1].Result.Status); Assert.Equal(503, reports[1].Result.StatusCode);
            Assert.Equal(AlertDeliveryStatus.Rejected, reports[2].Result.Status); Assert.Equal(404, reports[2].Result.StatusCode);
        }
        // No saved webhook: the alerts are dropped and reported as such, nothing is sent.
        await using var silent = new DiscordAlertOutbox(transport, _ => Task.FromResult<DiscordWebhookAddress?>(null), TimeSpan.Zero);
        var none = new List<AlertDeliveryReport>();
        silent.Reported += report => { lock (none) none.Add(report); };
        silent.Start(); silent.Enqueue(new(AlertKind.Test, at));
        await WaitUntil(() => { lock (none) return none.Count >= 1; });
        lock (none) { Assert.Equal(AlertDeliveryStatus.NoWebhook, none[0].Result.Status); Assert.Equal(1, none[0].Alerts); }
        Assert.Equal(4, transport.Requests.Count);
    }

    [Fact]
    public async Task StopDropsQueuedAlertsAndCancelsTheSendInFlightAndDisposeEndsTheWorker()
    {
        var transport = new TransportProbe { Hold = true };
        var outbox = new DiscordAlertOutbox(transport, _ => Task.FromResult<DiscordWebhookAddress?>(Address()), TimeSpan.Zero);
        outbox.Start();
        var at = DateTimeOffset.UtcNow;
        outbox.Enqueue(new(AlertKind.Test, at));
        await WaitUntil(() => transport.InFlight > 0);
        outbox.Enqueue(new(AlertKind.ClientCrashed, at, "Later", 1));
        outbox.Stop();
        await outbox.Work.WaitAsync(Positive);
        Assert.False(outbox.IsRunning);
        Assert.True(transport.Cancelled >= 1, "Stop did not cancel the send in flight.");
        Assert.Equal(0, outbox.Pending);
        Assert.False(outbox.Enqueue(new(AlertKind.Test, at)));
        // Start again resumes with a fresh queue; Dispose ends everything and disposes nothing it does not own.
        transport.Hold = false;
        outbox.Start(); Assert.True(outbox.IsRunning);
        outbox.Enqueue(new(AlertKind.Test, at));
        await transport.Sent(1);
        await outbox.DisposeAsync();
        Assert.False(outbox.IsRunning); Assert.True(outbox.Work.IsCompleted);
        Assert.False(transport.Disposed);
    }

    [Fact]
    public void TheClientTrackerAlertsOnNewHangsDisconnectsAndCrashRecordsButNotOnRestoredHistoryOrAPlayersOwnLeave()
    {
        var tracker = new ClientAlertTracker();
        var started = DateTimeOffset.UtcNow.AddMinutes(-10);
        var a = new SessionProcessKey(null, 101, started.UtcTicks); var b = new SessionProcessKey(null, 102, started.UtcTicks);
        var restored = new RecentClientHistoryEntry(Guid.NewGuid(), new(null, 100, started.UtcTicks), started, started.AddMinutes(1),
            NoLongerObservedUtc: started.AddMinutes(2), NotRespondingUtc: started.AddMinutes(1), FaultCheck: new(started.AddMinutes(3), true, started.AddMinutes(2), 0xC0000005));
        var active = new RecentClientHistoryEntry(Guid.NewGuid(), a, started, started.AddMinutes(1));
        var hungAtStart = new RecentClientHistoryEntry(Guid.NewGuid(), b, started, started.AddMinutes(1), NotRespondingUtc: started.AddMinutes(1));
        // The first look is the baseline: history restored from disk and clients already hung never alert.
        Assert.Empty(tracker.Observe(View([active, hungAtStart], [restored]), started.AddMinutes(1)));

        var hung = active with { LastObservedUtc = started.AddMinutes(2), NotRespondingUtc = started.AddMinutes(2), LastKnownUserId = 4242, LastKnownUserIdentityUtc = started.AddMinutes(1),
            LastKnownDestination = new(123456, Guid.NewGuid(), null, started.AddMinutes(1)) };
        var alert = Assert.Single(tracker.Observe(View([hung, hungAtStart], [restored]), started.AddMinutes(2)));
        Assert.Equal(AlertKind.ClientNotResponding, alert.Kind); Assert.Equal(a, alert.Process); Assert.Equal(4242, alert.UserId); Assert.Equal(123456, alert.PlaceId);
        // Still hung: no repeat. Recovered, then hung again: one more alert.
        Assert.Empty(tracker.Observe(View([hung with { LastObservedUtc = started.AddMinutes(3), NotRespondingUtc = started.AddMinutes(3) }, hungAtStart], [restored]), started.AddMinutes(3)));
        var recovered = hung with { LastObservedUtc = started.AddMinutes(4), NotRespondingUtc = null };
        Assert.Empty(tracker.Observe(View([recovered, hungAtStart], [restored]), started.AddMinutes(4)));
        Assert.Single(tracker.Observe(View([recovered with { LastObservedUtc = started.AddMinutes(5), NotRespondingUtc = started.AddMinutes(5) }, hungAtStart], [restored]), started.AddMinutes(5)));

        // A disconnect Roblox logged alerts once; the player's own leave (285) does not; the account launched elsewhere (273) does.
        var dropped = recovered with { LastObservedUtc = started.AddMinutes(6), DisconnectedUtc = started.AddMinutes(6), DisconnectReason = 277 };
        alert = Assert.Single(tracker.Observe(View([dropped, hungAtStart], [restored]), started.AddMinutes(6)));
        Assert.Equal(AlertKind.ClientDisconnected, alert.Kind); Assert.Equal(277, alert.DisconnectReason);
        Assert.Empty(tracker.Observe(View([dropped with { LastObservedUtc = started.AddMinutes(7) }, hungAtStart], [restored]), started.AddMinutes(7)));
        var rejoined = dropped with { LastObservedUtc = started.AddMinutes(8), DisconnectedUtc = null, DisconnectReason = null };
        Assert.Empty(tracker.Observe(View([rejoined, hungAtStart], [restored]), started.AddMinutes(8)));
        Assert.Empty(tracker.Observe(View([rejoined with { LastObservedUtc = started.AddMinutes(9), DisconnectedUtc = started.AddMinutes(9), DisconnectReason = 285 }, hungAtStart], [restored]), started.AddMinutes(9)));
        // Roblox clears a disconnect only when the client joins again; a later disconnect is a new one.
        Assert.Empty(tracker.Observe(View([rejoined with { LastObservedUtc = started.AddMinutes(9).AddSeconds(30) }, hungAtStart], [restored]), started.AddMinutes(9).AddSeconds(30)));
        var elsewhere =rejoined with { LastObservedUtc = started.AddMinutes(10), DisconnectedUtc = started.AddMinutes(10), DisconnectReason = ClientLogObservation.AccountLaunchedElsewhereReason };
        alert = Assert.Single(tracker.Observe(View([hungAtStart], [elsewhere with { NoLongerObservedUtc = started.AddMinutes(10) }]), started.AddMinutes(10)));
        Assert.Equal(AlertKind.ClientDisconnected, alert.Kind); Assert.Equal(ClientLogObservation.AccountLaunchedElsewhereReason, alert.DisconnectReason);

        // An ended client alerts for a crash only once Windows' fault record (or a crash dump) is in: nothing for an unchecked end.
        var ended = hungAtStart with { NotRespondingUtc = null, LastObservedUtc = started.AddMinutes(11), NoLongerObservedUtc = started.AddMinutes(11) };
        Assert.Empty(tracker.Observe(View([], [ended, elsewhere with { NoLongerObservedUtc = started.AddMinutes(10) }]), started.AddMinutes(11)));
        var crashed = ended with { FaultCheck = new(started.AddMinutes(12), true, started.AddMinutes(11), 0xC0000005) };
        alert = Assert.Single(tracker.Observe(View([], [crashed]), started.AddMinutes(12)));
        Assert.Equal(AlertKind.ClientCrashed, alert.Kind); Assert.Equal(b, alert.Process);
        Assert.Empty(tracker.Observe(View([], [crashed]), started.AddMinutes(13)));
        var dumpOnly = new RecentClientHistoryEntry(Guid.NewGuid(), new(null, 103, started.UtcTicks), started, started.AddMinutes(13), NoLongerObservedUtc: started.AddMinutes(13));
        Assert.Empty(tracker.Observe(View([], [crashed, dumpOnly]), started.AddMinutes(13)));
        alert = Assert.Single(tracker.Observe(View([], [crashed, dumpOnly with { FaultCheck = new(started.AddMinutes(14), true, null, null, 1) }]), started.AddMinutes(14)));
        Assert.Equal(AlertKind.ClientPossiblyCrashed, alert.Kind);
        var clean = new RecentClientHistoryEntry(Guid.NewGuid(), new(null, 104, started.UtcTicks), started, started.AddMinutes(14), NoLongerObservedUtc: started.AddMinutes(14));
        Assert.Empty(tracker.Observe(View([], [clean]), started.AddMinutes(14)));
        Assert.Empty(tracker.Observe(View([], [clean with { FaultCheck = new(started.AddMinutes(15), true) }]), started.AddMinutes(15)));
        // Forgotten clients leave the tracker, so its memory is bounded by the history's own limits.
        Assert.Equal(1, tracker.TrackedClients);
    }

    [Fact]
    public void TheBatchTrackerAlertsOnceWhenEveryRequestOfABatchHasSettled()
    {
        var tracker = new LaunchBatchAlertTracker();
        var now = DateTimeOffset.UtcNow;
        LaunchOperation Row(Guid id, LaunchState state, LaunchProblem problem = LaunchProblem.None)
            => new(new(id, Guid.NewGuid(), 1, 42, 9001, 0, now, 7), state, problem, now);
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        tracker.Track(ids, 9001);
        Assert.Empty(tracker.Observe(new(false, [Row(ids[0], LaunchState.Queued), Row(ids[1], LaunchState.Launching), Row(ids[2], LaunchState.Blocked, LaunchProblem.InsufficientCapacity), Row(ids[3], LaunchState.Arrived)]), now));
        Assert.Empty(tracker.Observe(new(false, [Row(ids[0], LaunchState.Failed, LaunchProblem.StartupRejected), Row(ids[1], LaunchState.Stabilizing), Row(ids[2], LaunchState.Blocked, LaunchProblem.InsufficientCapacity), Row(ids[3], LaunchState.Arrived)]), now));
        // The last request settles (one is gone from the queue: counted as removed, never as a success).
        var alert = Assert.Single(tracker.Observe(new(false, [Row(ids[0], LaunchState.Failed, LaunchProblem.StartupRejected), Row(ids[1], LaunchState.ClientStartedUnverified, LaunchProblem.ClientStartedUnverified), Row(ids[3], LaunchState.Arrived)]), now));
        Assert.Equal(AlertKind.BatchCompleted, alert.Kind);
        Assert.Equal(new LaunchBatchOutcome(9001, 4, 1, 1, 0, 1, 0, 1), alert.Batch);
        Assert.Equal(9001, alert.PlaceId);
        Assert.Empty(tracker.Observe(new(false, [Row(ids[3], LaunchState.Arrived)]), now)); // Once.
        Assert.Equal(0, tracker.TrackedBatches);
        // Review states settle a batch too: automation has nothing more to do with them.
        var review = Guid.NewGuid(); tracker.Track([review], 5);
        alert = Assert.Single(tracker.Observe(new(false, [Row(review, LaunchState.ReviewRequired, LaunchProblem.TimedOut)]), now));
        Assert.Equal(new LaunchBatchOutcome(5, 1, 0, 0, 1, 0, 0, 0), alert.Batch);
        // Bounded: the oldest tracked batch gives way.
        for (int i = 0; i < LaunchBatchAlertTracker.MaximumBatches + 3; i++) tracker.Track([Guid.NewGuid()], i + 10);
        Assert.Equal(LaunchBatchAlertTracker.MaximumBatches, tracker.TrackedBatches);
        Assert.Empty(tracker.Observe(new(false, []), now)); // Every tracked request is missing: removed, not settled, so nothing is announced.
    }

    [Fact]
    public async Task TheStoreKeepsTheWebhookProtectedOnDiskAndRedactedInSummaries()
    {
        using var store = new DiscordWebhookStore(_root);
        Assert.Null(await store.ReadSummaryAsync(default)); Assert.Null(await store.ReadAsync(default));
        using (var address = Address()) await store.SaveAsync(address, default);
        Assert.True(File.Exists(store.Path), "Saving the webhook wrote no protected file.");
        byte[] raw = await File.ReadAllBytesAsync(store.Path);
        Assert.DoesNotContain(Token, Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, Encoding.Unicode.GetString(raw), StringComparison.Ordinal);
        var summary = await store.ReadSummaryAsync(default);
        Assert.NotNull(summary);
        Assert.Equal("discord.com", summary!.Host); Assert.Equal("AbCd", summary.Last4);
        Assert.DoesNotContain(Token, summary.ToString(), StringComparison.Ordinal);
        Assert.Contains("AbCd", summary.Description, StringComparison.Ordinal);
        using (var read = await store.ReadAsync(default))
        {
            Assert.NotNull(read);
            string revealed = ""; read!.Reveal(span => revealed = new string(span));
            Assert.Equal(Url, revealed);
        }
        // A second instance over the same folder reads the same secret (one file, cross-process lock), and Clear removes it.
        using (var other = new DiscordWebhookStore(_root)) Assert.NotNull(await other.ReadSummaryAsync(default));
        await store.ClearAsync(default);
        Assert.False(File.Exists(store.Path)); Assert.Null(await store.ReadSummaryAsync(default));
        await store.ClearAsync(default); // Idempotent.
        // A damaged file is reported by type and never silently treated as "no webhook".
        await File.WriteAllBytesAsync(store.Path, [1, 2, 3]);
        await Assert.ThrowsAsync<DiscordWebhookStorageException>(() => store.ReadSummaryAsync(default));
        using (var address = Address()) await store.SaveAsync(address, default); // An explicit save replaces a damaged file.
        Assert.NotNull(await store.ReadSummaryAsync(default));
    }

    [Fact]
    public async Task TheClientPostsZenithsOwnUserAgentAndAFixedJsonBodyToTheWebhookPathAndReadsOnlyTheStatus()
    {
        string retryAfter = LoopbackHttpServer.Response("429 Too Many Requests", "{\"message\":\"rate limited\",\"retry_after\":1.5}", "Retry-After: 2", "Content-Type: application/json");
        await using var server = new LoopbackHttpServer(LoopbackHttpServer.Response("204 No Content"), retryAfter,
            LoopbackHttpServer.Response("404 Not Found", "{\"message\":\"Unknown Webhook\"}"), LoopbackHttpServer.Response("500 Internal Server Error", "boom"),
            LoopbackHttpServer.Response("400 Bad Request", "{}"), LoopbackHttpServer.Response("302 Found", "", "Location: https://example.net/"));
        await using var client = new DiscordWebhookClient(server.BaseAddress);
        using var address = Address();
        var delivered = await client.SendAsync(address, "**Zenith Sessions** · 1 alert\nTest line", default);
        Assert.Equal(AlertDeliveryStatus.Delivered, delivered.Status); Assert.Equal(204, delivered.StatusCode);
        string request = server.Requests[0];
        Assert.Equal("POST /api/webhooks/123456789012345678/" + Token + " HTTP/1.1", LoopbackHttpServer.RequestLine(request));
        Assert.Equal(DiscordWebhookClient.UserAgent, Assert.Single(LoopbackHttpServer.HeaderValues(request, "User-Agent")));
        Assert.StartsWith("Zenith-Sessions/", DiscordWebhookClient.UserAgent, StringComparison.Ordinal);
        Assert.Empty(LoopbackHttpServer.HeaderValues(request, "Cookie")); Assert.Empty(LoopbackHttpServer.HeaderValues(request, "Referer")); Assert.Empty(LoopbackHttpServer.HeaderValues(request, "Origin"));
        Assert.StartsWith("application/json", Assert.Single(LoopbackHttpServer.HeaderValues(request, "Content-Type")), StringComparison.Ordinal);
        string body = request[(request.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        using (var json = JsonDocument.Parse(body))
        {
            Assert.Equal("**Zenith Sessions** · 1 alert\nTest line", json.RootElement.GetProperty("content").GetString());
            // No @everyone or role ping can come out of an account label.
            Assert.Equal(0, json.RootElement.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
            Assert.Equal(2, json.RootElement.EnumerateObject().Count());
        }
        var limited = await client.SendAsync(address, "x", default);
        Assert.Equal(AlertDeliveryStatus.RateLimited, limited.Status); Assert.Equal(TimeSpan.FromSeconds(2), limited.RetryAfter);
        Assert.Equal(AlertDeliveryStatus.Rejected, (await client.SendAsync(address, "x", default)).Status);
        var unavailable = await client.SendAsync(address, "x", default);
        Assert.Equal(AlertDeliveryStatus.Unavailable, unavailable.Status); Assert.Equal(500, unavailable.StatusCode);
        Assert.Equal(AlertDeliveryStatus.Refused, (await client.SendAsync(address, "x", default)).Status);
        // A redirect is never followed (the production handler setting), so it is an unavailable answer, not a request elsewhere.
        Assert.Equal(AlertDeliveryStatus.Unavailable, (await client.SendAsync(address, "x", default)).Status);
        Assert.Equal(6, server.Requests.Count);
        // Retry-After is bounded whatever Discord says; a missing header gets the default wait.
        Assert.Equal(AlertMessages.MaximumRetryAfter, DiscordWebhookClient.BoundedRetryAfter(TimeSpan.FromHours(1)));
        Assert.Equal(AlertMessages.DefaultRetryAfter, DiscordWebhookClient.BoundedRetryAfter(null));
        Assert.Equal(TimeSpan.FromSeconds(1), DiscordWebhookClient.BoundedRetryAfter(TimeSpan.FromMilliseconds(10)));
        // The seam accepts only a loopback origin, so no token can travel over plain http to another host.
        Assert.Throws<ArgumentException>(() => new DiscordWebhookClient(new Uri("http://example.net/")));
    }

    [Fact]
    public async Task AlertsAreOffByDefaultOlderFilesStayOffAndASavedChoiceSurvivesLoadAndSave()
    {
        Assert.False(new SessionsSettings().DiscordAlerts);
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"DenseAccounts\":true}");
        using (var store = new SettingsStore(_root))
        {
            var loaded = await store.LoadAsync(default);
            Assert.Null(loaded.Notice); Assert.False(loaded.Settings.DiscordAlerts);
            Assert.Contains("\"DiscordAlerts\":false", JsonSerializer.Serialize(loaded.Settings), StringComparison.Ordinal);
            await store.SaveAsync(loaded.Settings, default);
        }
        Assert.False(SavedChoice(), "Saving an older settings file turned Discord alerts on.");
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"DiscordAlerts\":true}");
        using (var store = new SettingsStore(_root))
        {
            var loaded = await store.LoadAsync(default);
            Assert.Null(loaded.Notice); Assert.True(loaded.Settings.DiscordAlerts, "A saved Discord alerts choice was dropped when settings loaded.");
            await store.SaveAsync(loaded.Settings, default);
        }
        Assert.True(SavedChoice(), "Saving settings dropped the chosen Discord alerts.");
        // The settings file never carries the webhook URL: it lives in the protected store only.
        Assert.DoesNotContain("ebhook", await File.ReadAllTextAsync(Primary), StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsOffersTheAlertControlsWithAMaskedWebhookFieldAndTheGuideNamesThem()
    {
        var document = ProductionWindow();
        XElement? Named(string name) => document.Descendants().SingleOrDefault(e => (string?)e.Attribute(X + "Name") == name);
        var option = Named("SettingsDiscordAlerts");
        Assert.True(option is not null, "Settings has no control for Discord alerts.");
        Assert.Equal("CheckBox", option!.Name.LocalName);
        Assert.Equal("{Binding DiscordAlerts}", (string?)option.Attribute("IsChecked"));
        Assert.False(string.IsNullOrWhiteSpace((string?)option.Attribute("AutomationProperties.Name")));
        // The URL is a secret: it is typed into a masked field and never bound to a text property.
        var field = Named("SettingsDiscordWebhook");
        Assert.True(field is not null, "Settings has no field for the webhook URL.");
        Assert.Equal("PasswordBox", field!.Name.LocalName);
        Assert.Null(field.Attribute("Text")); Assert.Null(field.Attribute("Password"));
        Assert.False(string.IsNullOrWhiteSpace((string?)field.Attribute("AutomationProperties.Name")));
        var save = Named("SettingsSaveDiscordWebhook");
        Assert.True(save is not null, "Settings has no Save webhook button.");
        Assert.Equal("{Binding CanSaveDiscordWebhook}", (string?)save!.Attribute("IsEnabled"));
        var clear = Named("SettingsClearDiscordWebhook");
        Assert.Equal("{Binding ClearDiscordWebhook}", (string?)clear?.Attribute("Command"));
        Assert.Equal("{Binding CanClearDiscordWebhook}", (string?)clear?.Attribute("IsEnabled"));
        var test = Named("SettingsSendTestAlert");
        Assert.Equal("{Binding SendTestAlert}", (string?)test?.Attribute("Command"));
        Assert.Equal("{Binding CanSendTestAlert}", (string?)test?.Attribute("IsEnabled"));
        var resume = Named("SettingsResumeDiscordAlerts");
        Assert.Equal("{Binding ResumeDiscordAlerts}", (string?)resume?.Attribute("Command"));
        Assert.Equal("{Binding CanResumeDiscordAlerts}", (string?)resume?.Attribute("IsEnabled"));
        Assert.Contains(document.Descendants(), e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == "{Binding DiscordWebhookStatus}");
        Assert.Contains(document.Descendants(), e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == "{Binding DiscordAlertStatus}");
        var all = document.Descendants().ToList();
        int appearance = all.FindIndex(e => (string?)e.Attribute(X + "Name") == "SettingsAppearanceAnchor");
        int performance = all.FindIndex(e => (string?)e.Attribute(X + "Name") == "SettingsPerformanceAnchor");
        Assert.InRange(all.IndexOf(option), appearance + 1, performance - 1);
        var guide = Assert.Single(document.Descendants().Where(e => ((string?)e.Attribute("Text"))?.Contains("Lock hides account controls", StringComparison.Ordinal) == true));
        Assert.Contains("Discord", (string)guide.Attribute("Text")!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("discord")]
    [InlineData("webhook")]
    [InlineData("alerts")]
    [InlineData("notification")]
    public void SettingsSearchFindsDiscordAlertsInAppearance(string query)
    {
        var entry = SettingsSearchCatalog.Search(query).FirstOrDefault(e => e.Id == "discord-alerts");
        Assert.True(entry is not null, $"Searching settings for '{query}' does not find Discord alerts.");
        Assert.Equal(SettingsSearchTarget.Appearance, entry!.Target);
    }

    [Fact]
    public Task NothingIsSentByDefaultAndASavedWebhookReceivesAHangAlertAndATestAlertPanicStopsAndResumeRestarts() => OnDispatcherAsync(async () =>
    {
        var monitor = new ClientMonitor(); var store = new StoreProbe(); var transport = new TransportProbe();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, monitor, discordWebhookStore: store, discordTransport: transport);
        await vm.InitializeAsync(); Assert.True(vm.IsReady, vm.Notice);
        // Off by default: no webhook, nothing running, nothing sent, and the status says so.
        Assert.False(vm.DiscordAlerts); Assert.False(vm.IsDiscordAlertsActive); Assert.False(vm.HasDiscordWebhook);
        Assert.Contains("Off", vm.DiscordAlertStatus, StringComparison.Ordinal);
        Assert.Contains("No webhook", vm.DiscordWebhookStatus, StringComparison.Ordinal);
        Assert.False(vm.CanSendTestAlert); Assert.False(vm.CanClearDiscordWebhook); Assert.True(vm.CanSaveDiscordWebhook);

        // A refused URL saves nothing and says why, without echoing it.
        vm.SaveDiscordWebhook.Execute("https://example.net/api/webhooks/123456789012345678/" + Token); await vm.SaveDiscordWebhook.Execution;
        Assert.False(vm.HasDiscordWebhook); Assert.True(vm.HasErrorNotice); Assert.DoesNotContain(Token, vm.ErrorNotice, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, vm.DiscordWebhookStatus, StringComparison.Ordinal);
        vm.DismissError.Execute(null);
        // The saved webhook is shown masked: host and the token's last four only.
        vm.SaveDiscordWebhook.Execute(Url); await vm.SaveDiscordWebhook.Execution;
        Assert.True(vm.HasDiscordWebhook, vm.ErrorNotice); Assert.Equal(1, store.Saves);
        Assert.Contains("discord.com", vm.DiscordWebhookStatus, StringComparison.Ordinal);
        Assert.Contains("AbCd", vm.DiscordWebhookStatus, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, vm.DiscordWebhookStatus, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, string.Join("\n", vm.Events), StringComparison.Ordinal);
        Assert.Contains(Diagnostics(vm).Events, e => e.Kind == ManagerEventKind.DiscordWebhookSaved);
        Assert.False(vm.CanSendTestAlert); // Alerts are still off.
        Assert.True(vm.CanClearDiscordWebhook);

        // An unsaved draft starts nothing; Save settings starts the outbox.
        vm.DiscordAlerts = true; Assert.Equal("Unsaved settings", vm.SaveStatus); Assert.False(vm.IsDiscordAlertsActive);
        vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        Assert.Equal("Settings saved", vm.SaveStatus);
        Assert.True(vm.IsDiscordAlertsActive, "Saving Discord alerts on did not start the outbox.");
        Assert.Contains("\"DiscordAlerts\":true", File.ReadAllText(Primary), StringComparison.Ordinal);
        Assert.Contains("On", vm.DiscordAlertStatus, StringComparison.Ordinal);
        Assert.True(vm.CanSendTestAlert);
        Assert.Empty(transport.Requests);

        // The first scan is the baseline; the client then stops responding: one alert, with Zenith's own evidence only.
        await RefreshFrame(vm);
        monitor.Hang(); await RefreshFrame(vm);
        var sent = await transport.Sent(1);
        Assert.Contains("Client not responding", sent.Content, StringComparison.Ordinal);
        Assert.Contains("no matching saved account", sent.Content, StringComparison.Ordinal);
        Assert.Equal("AbCd", sent.Last4);
        await WaitUntil(() => Diagnostics(vm).Events.Any(e => e.Kind == ManagerEventKind.DiscordAlertSent));
        await Pump();
        Assert.Contains("sent", vm.DiscordAlertStatus, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.HasErrorNotice);
        // Still hung on the next scans: no repeat.
        monitor.Hang(); await RefreshFrame(vm); await Task.Delay(100);
        Assert.Single(transport.Requests);

        // Lock leaves alerts running: monitoring continues behind the lock, and the idle lock would otherwise silence them.
        vm.LockDashboard(); Assert.True(vm.IsLocked); Assert.True(vm.IsDiscordAlertsActive);
        vm.Unlock.Execute(null); await vm.Unlock.Execution;
        // Panic stops the outbox for this run; Resume alerts is the explicit start, as for automation.
        vm.Panic.Execute(null);
        Assert.False(vm.IsDiscordAlertsActive, "Panic left the alert outbox running.");
        Assert.True(vm.IsDiscordAlertsStopped); Assert.True(vm.CanResumeDiscordAlerts); Assert.False(vm.CanSendTestAlert);
        Assert.Contains("Panic", vm.DiscordAlertStatus, StringComparison.Ordinal);
        await vm.DiscordAlertWork;
        monitor.Recover(); await RefreshFrame(vm); monitor.Hang(); await RefreshFrame(vm); await Task.Delay(100);
        Assert.Single(transport.Requests); // Nothing was sent while stopped, and the hang that happened meanwhile does not alert later.
        vm.ResumeDiscordAlerts.Execute(null);
        Assert.True(vm.IsDiscordAlertsActive); Assert.False(vm.IsDiscordAlertsStopped); Assert.True(vm.CanSendTestAlert);
        vm.SendTestAlert.Execute(null); await vm.SendTestAlert.Execution;
        var test = await transport.Sent(2);
        Assert.Contains("Test alert", test.Content, StringComparison.Ordinal);

        // Saved off: the outbox stops and nothing more is sent; the webhook stays saved for next time.
        vm.DiscordAlerts = false; vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        Assert.False(vm.IsDiscordAlertsActive); Assert.True(vm.HasDiscordWebhook); Assert.False(vm.CanSendTestAlert);
        monitor.Recover(); await RefreshFrame(vm); monitor.Hang(); await RefreshFrame(vm); await Task.Delay(100);
        Assert.Equal(2, transport.Requests.Count);
        // Clear forgets the secret.
        vm.ClearDiscordWebhook.Execute(null); await vm.ClearDiscordWebhook.Execution;
        Assert.False(vm.HasDiscordWebhook); Assert.Null(store.Address);
        Assert.Contains(Diagnostics(vm).Events, e => e.Kind == ManagerEventKind.DiscordWebhookCleared);
    });

    [Fact]
    public Task AFailedSendIsRecordedByTypeAndShownOnceUntilASendSucceedsAndARefusedWebhookStopsAlerts() => OnDispatcherAsync(async () =>
    {
        var monitor = new ClientMonitor(); var store = new StoreProbe { Address = Address() }; var transport = new TransportProbe();
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"DiscordAlerts\":true}");
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, monitor, discordWebhookStore: store, discordTransport: transport);
        await vm.InitializeAsync(); Assert.True(vm.IsReady, vm.Notice);
        Assert.True(vm.DiscordAlerts); Assert.True(vm.IsDiscordAlertsActive, "A saved Discord alerts choice did not start the outbox at startup.");
        Assert.True(vm.HasDiscordWebhook);
        transport.Answers.Enqueue(new(AlertDeliveryStatus.Unavailable, ErrorType: typeof(System.Net.Http.HttpRequestException)));
        transport.Answers.Enqueue(new(AlertDeliveryStatus.Unavailable, 503));
        transport.Answers.Enqueue(new(AlertDeliveryStatus.Delivered, 204));
        transport.Answers.Enqueue(new(AlertDeliveryStatus.Unavailable, 502));
        transport.Answers.Enqueue(new(AlertDeliveryStatus.Rejected, 404));

        vm.SendTestAlert.Execute(null); await vm.SendTestAlert.Execution; await transport.Sent(1);
        await WaitUntil(() => Diagnostics(vm).Events.Count(e => e.Kind == ManagerEventKind.OperationFailed && e.Subsystem == ManagerSubsystem.DiscordAlerts) == 1);
        await Pump();
        Assert.True(vm.HasErrorNotice, "The first failed send was not shown.");
        Assert.Contains("Discord", vm.ErrorNotice, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, vm.ErrorNotice, StringComparison.Ordinal);
        Assert.Equal("HttpRequestException", Assert.Single(Diagnostics(vm).Events, e => e.Kind == ManagerEventKind.OperationFailed && e.Subsystem == ManagerSubsystem.DiscordAlerts).ErrorType);
        vm.DismissError.Execute(null);
        // The second failure in a row is kept in Diagnostics but not shown again: no storm of errors, and no retry.
        vm.SendTestAlert.Execute(null); await vm.SendTestAlert.Execution; await transport.Sent(2);
        await WaitUntil(() => Diagnostics(vm).Events.Count(e => e.Kind == ManagerEventKind.OperationFailed && e.Subsystem == ManagerSubsystem.DiscordAlerts) == 2);
        await Pump();
        Assert.False(vm.HasErrorNotice, "A repeated failed send was shown again.");
        Assert.Contains("not sent", vm.DiscordAlertStatus, StringComparison.Ordinal);
        Assert.True(vm.IsDiscordAlertsActive);
        // A success resets the rule, so the next failure is shown once more.
        vm.SendTestAlert.Execute(null); await vm.SendTestAlert.Execution; await transport.Sent(3);
        await WaitUntil(() => Diagnostics(vm).Events.Any(e => e.Kind == ManagerEventKind.DiscordAlertSent));
        vm.SendTestAlert.Execute(null); await vm.SendTestAlert.Execution; await transport.Sent(4);
        await WaitUntil(() => Diagnostics(vm).Events.Count(e => e.Kind == ManagerEventKind.OperationFailed && e.Subsystem == ManagerSubsystem.DiscordAlerts) == 3);
        await Pump();
        Assert.True(vm.HasErrorNotice);
        vm.DismissError.Execute(null);
        // Discord no longer knows the webhook: alerts stop until a new URL is saved, so a deleted webhook is never hammered.
        vm.SendTestAlert.Execute(null); await vm.SendTestAlert.Execution; await transport.Sent(5);
        await WaitUntil(() => Diagnostics(vm).Events.Count(e => e.Kind == ManagerEventKind.OperationFailed && e.Subsystem == ManagerSubsystem.DiscordAlerts) == 4);
        await Pump();
        Assert.False(vm.IsDiscordAlertsActive, "A refused webhook left the outbox running.");
        Assert.True(vm.IsDiscordAlertsStopped);
        Assert.Contains("404", vm.ErrorNotice, StringComparison.Ordinal);
        Assert.Equal(5, transport.Requests.Count);
        // Saving a new webhook starts alerts again.
        vm.SaveDiscordWebhook.Execute(Url); await vm.SaveDiscordWebhook.Execution;
        Assert.True(vm.IsDiscordAlertsActive, vm.ErrorNotice); Assert.False(vm.IsDiscordAlertsStopped);
    });

    [Fact]
    public Task ExitStopsTheOutboxAndDisposesTheTransportAndSampleModeNeverSends() => OnDispatcherAsync(async () =>
    {
        var store = new StoreProbe { Address = Address() }; var transport = new TransportProbe { Hold = true };
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"DiscordAlerts\":true}");
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new ClientMonitor(), discordWebhookStore: store, discordTransport: transport);
        try
        {
            await vm.InitializeAsync(); Assert.True(vm.IsReady, vm.Notice); Assert.True(vm.IsDiscordAlertsActive);
            vm.SendTestAlert.Execute(null); await vm.SendTestAlert.Execution;
            await WaitUntil(() => transport.InFlight > 0);
        }
        finally { await vm.DisposeAsync(); }
        Assert.True(transport.Cancelled >= 1, "Exit did not cancel the send in flight.");
        Assert.True(transport.Disposed, "Exit did not dispose the webhook transport.");
        Assert.True(store.Disposed, "Exit did not dispose the webhook store.");
        Assert.False(vm.IsDiscordAlertsActive);

        await using var sample = new ShellViewModel(_root, true, Dispatcher.CurrentDispatcher, new ClientMonitor());
        await sample.InitializeAsync(); Assert.True(sample.IsReady, sample.Notice);
        Assert.Contains("sample", sample.DiscordAlertStatus, StringComparison.OrdinalIgnoreCase);
        Assert.False(sample.CanSaveDiscordWebhook); Assert.False(sample.CanSendTestAlert);
        sample.DiscordAlerts = true; sample.SaveSettings.Execute(null); await sample.SaveSettings.Execution;
        Assert.Equal("Settings saved", sample.SaveStatus); Assert.False(sample.IsDiscordAlertsActive);
    });

    [Fact]
    public Task AlertLabelsComeFromSavedAccountsByTheirKnownPlayerIdAndNeverFromAnythingElse() => OnDispatcherAsync(async () =>
    {
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new ClientMonitor(), discordWebhookStore: new StoreProbe(), discordTransport: new TransportProbe());
        await vm.InitializeAsync(); Assert.True(vm.IsReady, vm.Notice);
        await vm.SaveAccountAsync(AccountProfile.Create("Alpha runner") with { RobloxUserId = 4242 });
        await RefreshFrame(vm);
        Assert.Equal("Alpha runner", await vm.ResolveAlertLabelAsync(4242, default));
        Assert.Null(await vm.ResolveAlertLabelAsync(4243, default));
        Assert.Null(await vm.ResolveAlertLabelAsync(null, default));
    });

    private static DiscordWebhookAddress Address()
    {
        Assert.True(DiscordWebhookAddress.TryParse(Url, out var address, out string problem), problem);
        return address!;
    }
    private static RecentClientHistoryView View(RecentClientHistoryEntry[] active, RecentClientHistoryEntry[] ended) => new(1, active, ended);
    private static ManagerDiagnosticSnapshot Diagnostics(ShellViewModel shell)
        => ((ManagerDiagnostics)typeof(ShellViewModel).GetField("_diagnostics", Private)!.GetValue(shell)!).GetSnapshot();
    private static async Task RefreshFrame(ShellViewModel vm)
    {
        vm.Refresh.Execute(null); await vm.Refresh.Execution; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await vm.DiscordAlertWork; await Pump();
    }
    private static async Task Pump()
    {
        for (int i = 0; i < 3; i++) await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Positive) throw new TimeoutException("The awaited condition did not happen within " + Positive.TotalSeconds + " s.");
            await Task.Delay(20);
        }
    }
    private bool SavedChoice()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Primary));
        return json.RootElement.TryGetProperty("DiscordAlerts", out var value) && value.GetBoolean();
    }
    private static XDocument ProductionWindow()
    {
        using var stream = typeof(DiscordAlertTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        return XDocument.Load(stream);
    }

    internal sealed record SentRequest(string Last4, string Content, DateTimeOffset At);

    /// <summary>Records what the outbox hands it; answers by queue, else Delivered. Hold keeps sends waiting until Release.</summary>
    internal sealed class TransportProbe : IDiscordWebhookTransport
    {
        private readonly List<SentRequest> _requests = [];
        private TaskCompletionSource _hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _holding;
        private int _inFlight, _cancelled;
        public Queue<AlertDeliveryResult> Answers { get; } = new();
        public bool Disposed { get; private set; }
        public int InFlight => Volatile.Read(ref _inFlight);
        public int Cancelled => Volatile.Read(ref _cancelled);
        public bool Hold { get => _holding; set { _holding = value; if (value && _hold.Task.IsCompleted) _hold = new(TaskCreationOptions.RunContinuationsAsynchronously); } }
        public void Release() { _holding = false; _hold.TrySetResult(); }
        public IReadOnlyList<SentRequest> Requests { get { lock (_requests) return [.. _requests]; } }
        public async Task<SentRequest> Sent(int count)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                lock (_requests) if (_requests.Count >= count) return _requests[count - 1];
                if (watch.Elapsed > Positive) throw new TimeoutException($"Only {Requests.Count} of {count} expected sends happened within {Positive.TotalSeconds} s.");
                await Task.Delay(10);
            }
        }
        public async Task<AlertDeliveryResult> SendAsync(DiscordWebhookAddress address, string content, CancellationToken ct)
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                if (_holding)
                {
                    try { await _hold.Task.WaitAsync(ct); }
                    catch (OperationCanceledException) { Interlocked.Increment(ref _cancelled); throw; }
                }
                ct.ThrowIfCancellationRequested();
                string revealed = ""; address.Reveal(span => revealed = new string(span));
                if (!revealed.StartsWith("https://", StringComparison.Ordinal)) throw new InvalidOperationException("The outbox handed the transport something other than the saved address.");
                lock (_requests) _requests.Add(new(address.Last4, content, DateTimeOffset.UtcNow));
                AlertDeliveryResult answer;
                lock (Answers) answer = Answers.Count > 0 ? Answers.Dequeue() : new(AlertDeliveryStatus.Delivered, 204);
                return answer;
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    /// <summary>An in-memory protected store: the address object itself is kept, never a string.</summary>
    internal sealed class StoreProbe : IDiscordWebhookStore, IDisposable
    {
        private string? _url;
        public int Saves { get; private set; }
        public bool Disposed { get; private set; }
        public DiscordWebhookAddress? Address
        {
            get { if (_url is null) return null; Assert.True(DiscordWebhookAddress.TryParse(_url, out var a, out _)); return a; }
            set { string? url = null; value?.Reveal(span => url = new string(span)); _url = url; }
        }
        public Task<DiscordWebhookSummary?> ReadSummaryAsync(CancellationToken ct)
        { using var a = Address; return Task.FromResult(a is null ? null : new DiscordWebhookSummary(a.Host, a.Last4)); }
        public Task<DiscordWebhookAddress?> ReadAsync(CancellationToken ct) => Task.FromResult(Address);
        public Task SaveAsync(DiscordWebhookAddress address, CancellationToken ct) { Address = address; Saves++; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken ct) { _url = null; return Task.CompletedTask; }
        public void Dispose() => Disposed = true;
    }

    /// <summary>One synthetic client whose window can be made to hang (Windows' hung-window check, as the real monitor reports it).</summary>
    private sealed class ClientMonitor : IDesktopMonitor
    {
        private readonly long _started = DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks;
        private long _revision = 1;
        private bool _hung;
        public void Hang() { _hung = true; _revision++; }
        public void Recover() { _hung = false; _revision++; }
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new DesktopSnapshot(new(now, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 20),
                [new(10001, _started, "Synthetic", 1, "Synthetic", false, true, 1, 1, null, now, WindowHung: _hung, NotResponding: _hung)],
                ProcessObservedUtc: now, ProcessRevision: _revision, ProcessInventoryComplete: true));
        }
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
    }

    private static Task OnDispatcherAsync(Func<Task> work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            Task operation = Task.CompletedTask;
            dispatcher.BeginInvoke(() => { operation = RunAsync(); });
            async Task RunAsync()
            {
                try { await work(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
            Dispatcher.Run(); operation.GetAwaiter().GetResult();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(120));
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-discord-alerts-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
