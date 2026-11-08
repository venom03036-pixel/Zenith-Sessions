using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Zenith.Sessions.Core;

// Discord webhook alerts (R-api-12, also R-shell-22). Pure policy and the one bounded outbox: what an alert is, how it reads, how a
// webhook URL is recognised and held, and how sends are paced. Nothing here knows HTTP or the disk; the Windows layer sends and
// stores, the shell decides when an event happened. Every message is a fixed template that carries account labels, places and
// counts only (never a log line, a URL, a token or an exception message, Rule 10).

/// <summary>What happened. Built only from Zenith's own lifecycle evidence: presence history, hung-window checks, logged disconnects,
/// Windows fault records and the launch queue; Test is the user's own "Send test alert".</summary>
public enum AlertKind { ClientCrashed, ClientPossiblyCrashed, ClientNotResponding, ClientDisconnected, BatchCompleted, Test }

/// <summary>How a tracked launch batch ended: counts only. Removed are requests the queue no longer lists (expired or cleared).</summary>
public sealed record LaunchBatchOutcome(long PlaceId, int Requests, int Arrived, int StartedUnverified, int NeedReview, int Failed, int Cancelled, int Removed);

/// <summary>One alert to send. AccountLabel is a saved account's label, or null when no saved account matched the client.</summary>
public sealed record AlertEvent(AlertKind Kind, DateTimeOffset Utc, string? AccountLabel = null, long? PlaceId = null, int? DisconnectReason = null,
    LaunchBatchOutcome? Batch = null)
{
    public string Line => AlertMessages.Describe(this);
}

/// <summary>Fixed templates and bounds. One message per <see cref="MinimumGap"/>, at most <see cref="MaximumLinesPerMessage"/> alerts in it,
/// at most <see cref="MaximumQueued"/> waiting; Discord's own limit is 2,000 characters.</summary>
public static class AlertMessages
{
    public const int MaximumQueued = 50, MaximumLinesPerMessage = 10, MaximumContentLength = 1900, MaximumLabelLength = 40;
    public static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(2);
    /// <summary>A 429 is honoured once, for Discord's Retry-After bounded to this; a missing or unreadable header waits the default.</summary>
    public static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromSeconds(60), DefaultRetryAfter = TimeSpan.FromSeconds(5), MinimumRetryAfter = TimeSpan.FromSeconds(1);
    private const char LineSeparator = (char)0x2028, ParagraphSeparator = (char)0x2029;

    /// <summary>A label fit for one line of a message: control characters dropped, a backtick (which would end the label's code
    /// span) becomes an apostrophe, and anything past <see cref="MaximumLabelLength"/> is an ellipsis. Null when nothing is left.</summary>
    public static string? NormalizeLabel(string? label)
    {
        if (label is null) return null;
        var text = new StringBuilder(Math.Min(label.Length, MaximumLabelLength + 1));
        foreach (char c in label)
        {
            if (char.IsControl(c) || c is LineSeparator or ParagraphSeparator) continue;
            if (text.Length == MaximumLabelLength) { text.Append('…'); break; }
            text.Append(c == '`' ? '\'' : c);
        }
        string result = text.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    public static string Describe(AlertEvent alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        string who = Who(alert);
        return alert.Kind switch
        {
            AlertKind.ClientCrashed => "Client crashed: " + who + ".",
            AlertKind.ClientPossiblyCrashed => "Client may have crashed (a Roblox crash dump was written as it ended): " + who + ".",
            AlertKind.ClientNotResponding => "Client not responding: " + who + ".",
            AlertKind.ClientDisconnected => alert.DisconnectReason == ClientLogObservation.AccountLaunchedElsewhereReason
                ? "Client disconnected because the account launched in another client: " + who + "."
                : alert.DisconnectReason is { } reason
                    ? "Client disconnected (reason " + reason.ToString(CultureInfo.InvariantCulture) + "): " + who + "."
                    : "Client disconnected: " + who + ".",
            AlertKind.BatchCompleted => alert.Batch is { } batch
                ? FormattableString.Invariant($"Launch batch for place {batch.PlaceId} finished: {batch.Requests} requests, {batch.Arrived} arrived, {batch.StartedUnverified} started unverified, {batch.NeedReview} need review, {batch.Failed} failed, {batch.Cancelled} cancelled.")
                    + (batch.Removed > 0 ? FormattableString.Invariant($" {batch.Removed} no longer listed by the queue.") : "")
                : "Launch batch finished.",
            AlertKind.Test => "Test alert from Zenith Sessions. Alerts for crashes, hangs, disconnects and finished launch batches arrive here.",
            _ => "Unrecognized alert."
        };
    }

    private static string Who(AlertEvent alert)
    {
        string who = NormalizeLabel(alert.AccountLabel) is { } label ? "account `" + label + "`" : "a client with no matching saved account";
        return alert.PlaceId is { } place and > 0 ? who + ", place " + place.ToString(CultureInfo.InvariantCulture) : who;
    }

    /// <summary>One message: a header with the count, the dropped note when the queue overflowed, then one line per alert, within
    /// <see cref="MaximumContentLength"/>.</summary>
    public static string Compose(IReadOnlyList<AlertEvent> alerts, int dropped)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        if (alerts.Count == 0) throw new ArgumentException("A message carries at least one alert.", nameof(alerts));
        var text = new StringBuilder("**Zenith Sessions** · ").Append(alerts.Count.ToString(CultureInfo.InvariantCulture)).Append(alerts.Count == 1 ? " alert" : " alerts");
        if (dropped > 0)
            text.Append('\n').Append(dropped.ToString(CultureInfo.InvariantCulture)).Append(dropped == 1 ? " earlier alert was" : " earlier alerts were").Append(" dropped because the queue was full.");
        foreach (var alert in alerts)
        {
            string line = Describe(alert);
            if (text.Length + 1 + line.Length > MaximumContentLength) { text.Append("\n…"); break; }
            text.Append('\n').Append(line);
        }
        return text.ToString();
    }

    /// <summary>Discord's wait, bounded to [<see cref="MinimumRetryAfter"/>, <see cref="MaximumRetryAfter"/>]; null is the default wait.</summary>
    public static TimeSpan BoundedRetryAfter(TimeSpan? retryAfter)
    {
        if (retryAfter is not { } wait) return DefaultRetryAfter;
        return wait < MinimumRetryAfter ? MinimumRetryAfter : wait > MaximumRetryAfter ? MaximumRetryAfter : wait;
    }
}

/// <summary>
/// A Discord webhook URL, accepted only as https on an exact Discord host with the webhook path shape, and then held like a code: in a
/// pinned char array, revealed only to a caller-owned span, zeroed on dispose, never serialized, logged or printed. Anyone holding
/// the URL can post to the channel, so only the host and the token's last four characters are ever shown.
/// </summary>
public sealed class DiscordWebhookAddress : IDisposable
{
    public static readonly IReadOnlyList<string> AllowedHosts = Array.AsReadOnly(new[] { "discord.com", "discordapp.com", "ptb.discord.com", "canary.discord.com" });
    public const int MaximumLength = 512;
    private static readonly Regex PathShape = new(@"^/api/(?:v\d{1,2}/)?webhooks/(\d{17,20})/([A-Za-z0-9_-]{32,128})$", RegexOptions.CultureInvariant);
    private char[]? _value;
    private GCHandle _pin;

    private DiscordWebhookAddress(string url, string host, string last4)
    {
        _value = url.ToCharArray();
        _pin = GCHandle.Alloc(_value, GCHandleType.Pinned);
        Host = host; Last4 = last4;
    }
    ~DiscordWebhookAddress() => Dispose(false);

    /// <summary>The exact Discord host, lower case; not a secret.</summary>
    public string Host { get; }
    /// <summary>The last four characters of the webhook token, for telling one saved webhook from another.</summary>
    public string Last4 { get; }

    /// <summary>Accepts the URL Discord shows for a channel webhook and nothing else. The problem text never repeats the input.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out DiscordWebhookAddress? address, out string problem)
    {
        address = null;
        string trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) { problem = "Enter the webhook URL Discord shows for the channel (Edit channel → Integrations → Webhooks)."; return false; }
        if (trimmed.Length > MaximumLength || trimmed.Any(char.IsControl) || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        { problem = "That is not a complete web address."; return false; }
        if (uri.Scheme != Uri.UriSchemeHttps) { problem = "A Discord webhook URL starts with https://."; return false; }
        if (!uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        { problem = "A Discord webhook URL has no port, user name, query or fragment."; return false; }
        if (uri.HostNameType != UriHostNameType.Dns || !AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        { problem = "Only a webhook on discord.com (or discordapp.com) is accepted."; return false; }
        var match = PathShape.Match(uri.AbsolutePath);
        if (!match.Success) { problem = "A Discord webhook URL looks like https://discord.com/api/webhooks/<id>/<token>."; return false; }
        problem = "";
        address = new(trimmed, uri.Host.ToLowerInvariant(), match.Groups[2].Value[^4..]);
        return true;
    }

    public void Reveal(ProtectedCodeReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        char[] value = _value ?? throw new ObjectDisposedException(nameof(DiscordWebhookAddress));
        reader(value);
    }

    public override string ToString() => "[Discord webhook]";
    public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
    private void Dispose(bool disposing)
    {
        _ = disposing;
        char[]? value = _value; _value = null;
        if (value is null) return;
        Array.Clear(value);
        if (_pin.IsAllocated) _pin.Free();
    }
}

/// <summary>What Settings may show about a saved webhook: the host and the token's last four characters.</summary>
public sealed record DiscordWebhookSummary(string Host, string Last4)
{
    public string Description => "Webhook saved for " + Host + " (token ends with …" + Last4 + "). The URL is kept protected and is never shown or logged.";
    public override string ToString() => "Discord webhook { Host = " + Host + ", Token = …" + Last4 + " }";
}

/// <summary>Protected storage for the one webhook URL. The URL leaves the store only as a <see cref="DiscordWebhookAddress"/>.</summary>
public interface IDiscordWebhookStore
{
    Task<DiscordWebhookSummary?> ReadSummaryAsync(CancellationToken ct);
    Task<DiscordWebhookAddress?> ReadAsync(CancellationToken ct);
    Task SaveAsync(DiscordWebhookAddress address, CancellationToken ct);
    Task ClearAsync(CancellationToken ct);
}

/// <summary>
/// How one send ended. Rejected is Discord saying the webhook itself is gone or wrong (401, 403, 404): sending more would only
/// repeat the refusal. Refused is a message Discord would not take (another 4xx). Unavailable is a 5xx, a redirect that is never
/// followed, a network fault or a timeout. NoWebhook: nothing is saved to send to.
/// </summary>
public enum AlertDeliveryStatus { Delivered, RateLimited, Rejected, Refused, Unavailable, NoWebhook }

/// <summary>StatusCode and RetryAfter come from Discord's answer; ErrorType is the fault's type only (Rule 10). No body text is kept.</summary>
public sealed record AlertDeliveryResult(AlertDeliveryStatus Status, int? StatusCode = null, TimeSpan? RetryAfter = null, Type? ErrorType = null)
{
    public string Explanation => Status switch
    {
        AlertDeliveryStatus.Delivered => "Discord accepted the alert.",
        AlertDeliveryStatus.RateLimited => "Discord is rate limiting this webhook.",
        AlertDeliveryStatus.Rejected => "Discord refused the saved webhook (HTTP " + Code + "): it was deleted or the URL is wrong.",
        AlertDeliveryStatus.Refused => "Discord refused the alert message (HTTP " + Code + ").",
        AlertDeliveryStatus.NoWebhook => "No webhook URL is saved, so nothing was sent.",
        _ => StatusCode is not null ? "Discord did not accept the alert (HTTP " + Code + ")." : "The alert could not reach Discord (network error or timeout)."
    };
    private string Code => (StatusCode ?? 0).ToString(CultureInfo.InvariantCulture);
}

/// <summary>Posts one composed message to the webhook. Reads only the status and Retry-After; never the body.</summary>
public interface IDiscordWebhookTransport : IAsyncDisposable
{
    Task<AlertDeliveryResult> SendAsync(DiscordWebhookAddress address, string content, CancellationToken ct);
}

/// <summary>One message's outcome: how many alerts it carried and how many earlier ones were dropped before it.</summary>
public sealed record AlertDeliveryReport(AlertDeliveryResult Result, int Alerts, int Dropped);

/// <summary>
/// The bounded outbox. Start opens a queue of <see cref="AlertMessages.MaximumQueued"/> alerts (the oldest give way) and one worker
/// that waits for alerts (Channel, no polling, Rule 14), gathers up to <see cref="AlertMessages.MaximumLinesPerMessage"/> into one
/// message, reads the webhook from its protected source for that send only (Rule 10) and posts it, never sooner than
/// <see cref="AlertMessages.MinimumGap"/> after the previous one. A 429 waits once for Discord's bounded Retry-After and sends the same
/// message once more; any other failure is reported and never retried (Rule 16). Stop (Panic) drops what is queued and cancels the
/// send in flight (Rule 11); Start afterwards opens a fresh queue. <see cref="Reported"/> is raised on the worker's thread.
/// The transport is not owned here; its owner disposes it after this (Rule 13).
/// </summary>
public sealed class DiscordAlertOutbox : IAsyncDisposable
{
    private readonly IDiscordWebhookTransport _transport;
    private readonly Func<CancellationToken, Task<DiscordWebhookAddress?>> _address;
    private readonly TimeSpan _gap;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private Channel<AlertEvent>? _queue;
    private CancellationTokenSource? _stop;
    private Task _work = Task.CompletedTask;
    private int _dropped, _disposed;
    private long _lastSendAt;

    public DiscordAlertOutbox(IDiscordWebhookTransport transport, Func<CancellationToken, Task<DiscordWebhookAddress?>> address, TimeSpan? minimumGap = null, TimeProvider? clock = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _address = address ?? throw new ArgumentNullException(nameof(address));
        _gap = minimumGap ?? AlertMessages.MinimumGap;
        if (_gap < TimeSpan.Zero || _gap > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(minimumGap));
        _clock = clock ?? TimeProvider.System;
    }

    public event Action<AlertDeliveryReport>? Reported;
    public bool IsRunning { get { lock (_gate) return _queue is not null; } }
    /// <summary>Alerts waiting for the worker; 0 while stopped.</summary>
    public int Pending { get { lock (_gate) return _queue?.Reader.Count ?? 0; } }
    /// <summary>The worker, for Exit and tests; completed while nothing runs.</summary>
    public Task Work { get { lock (_gate) return _work; } }

    /// <summary>Idempotent. A worker still finishing an earlier run ends before the new one sends.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_queue is not null) return;
            // Not SingleReader: Stop drains the queue from the caller's thread while the worker may still be reading.
            var queue = Channel.CreateBounded<AlertEvent>(new BoundedChannelOptions(AlertMessages.MaximumQueued)
                { FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false }, _ => Interlocked.Increment(ref _dropped));
            var stop = new CancellationTokenSource();
            _queue = queue; _stop = stop; Interlocked.Exchange(ref _dropped, 0);
            _work = RunAsync(queue, stop, _work);
        }
    }

    /// <summary>Any thread. False when the outbox is not running: the alert is not kept for later.</summary>
    public bool Enqueue(AlertEvent alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        Channel<AlertEvent>? queue;
        lock (_gate) queue = _queue;
        return queue is not null && queue.Writer.TryWrite(alert);
    }

    /// <summary>Idempotent. Drops every queued alert and cancels the send in flight; the worker ends on its own.</summary>
    public void Stop()
    {
        Channel<AlertEvent>? queue; CancellationTokenSource? stop;
        lock (_gate) { queue = _queue; stop = _stop; _queue = null; _stop = null; }
        if (queue is null || stop is null) return;
        queue.Writer.TryComplete();
        while (queue.Reader.TryRead(out _)) { }
        stop.Cancel();
    }

    private async Task RunAsync(Channel<AlertEvent> queue, CancellationTokenSource stop, Task previous)
    {
        var ct = stop.Token;
        try
        {
            // Never runs inside Start's lock, and never sends beside an earlier worker that Stop is still ending.
            await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding);
            var batch = new List<AlertEvent>(AlertMessages.MaximumLinesPerMessage);
            while (await queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                // Pacing is a timestamp gate, not a sleep after every send: a quiet outbox sends the next alert at once.
                if (_lastSendAt != 0 && _gap - _clock.GetElapsedTime(_lastSendAt) is { } remaining && remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, _clock, ct).ConfigureAwait(false);
                batch.Clear();
                while (batch.Count < AlertMessages.MaximumLinesPerMessage && queue.Reader.TryRead(out var alert)) batch.Add(alert);
                if (batch.Count == 0) continue;
                int dropped = Interlocked.Exchange(ref _dropped, 0);
                var result = await DeliverAsync(batch, dropped, ct).ConfigureAwait(false);
                _lastSendAt = _clock.GetTimestamp();
                Reported?.Invoke(new(result, batch.Count, dropped));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ChannelClosedException) { }
        finally { stop.Dispose(); }
    }

    private async Task<AlertDeliveryResult> DeliverAsync(List<AlertEvent> batch, int dropped, CancellationToken ct)
    {
        var address = await _address(ct).ConfigureAwait(false);
        if (address is null) return new(AlertDeliveryStatus.NoWebhook);
        using (address)
        {
            string content = AlertMessages.Compose(batch, dropped);
            var result = await _transport.SendAsync(address, content, ct).ConfigureAwait(false);
            if (result.Status != AlertDeliveryStatus.RateLimited) return result;
            // Discord's own wait, bounded, then the same message once more. A second 429 is the answer: no storm.
            await Task.Delay(AlertMessages.BoundedRetryAfter(result.RetryAfter), _clock, ct).ConfigureAwait(false);
            return await _transport.SendAsync(address, content, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { await Work.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); return; }
        Stop();
        await Work.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
}

/// <summary>One client lifecycle alert before its account label is known: the process, the client's last reported player and place.</summary>
public sealed record ClientAlert(AlertKind Kind, SessionProcessKey Process, long? UserId, long? PlaceId, int? DisconnectReason, DateTimeOffset Utc);

/// <summary>
/// Pure. Turns the presence history's view into client alerts, once each: a hang when a client's NotRespondingUtc appears (again
/// after it answered again), a disconnect when DisconnectedUtc appears (again after a rejoin; the player's own leave, reason 285,
/// never alerts; the account launched elsewhere, 273, does), and a crash once an ended client's one fault check is in and names a
/// Windows fault record (or, weaker, a crash dump). The first look is the baseline: history restored from disk and clients already in
/// a state never alert. Clients the history forgets leave the tracker, so it is bounded by the history's own limits.
/// </summary>
public sealed class ClientAlertTracker
{
    private sealed class Watched { public bool Hang, Disconnect, Crash, Seen; }
    private readonly Dictionary<SessionProcessKey, Watched> _clients = [];
    private bool _baselineTaken;

    public int TrackedClients => _clients.Count;

    public IReadOnlyList<ClientAlert> Observe(RecentClientHistoryView view, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(view);
        bool baseline = !_baselineTaken; _baselineTaken = true;
        foreach (var watched in _clients.Values) watched.Seen = false;
        List<ClientAlert>? alerts = null;
        foreach (var entry in view.Active) Observe(entry, baseline, now, ref alerts);
        foreach (var entry in view.Ended) Observe(entry, baseline, now, ref alerts);
        foreach (var key in _clients.Where(pair => !pair.Value.Seen).Select(pair => pair.Key).ToArray()) _clients.Remove(key);
        return alerts is null ? [] : Array.AsReadOnly(alerts.ToArray());
    }

    private void Observe(RecentClientHistoryEntry entry, bool baseline, DateTimeOffset now, ref List<ClientAlert>? alerts)
    {
        if (entry is null) return;
        if (!_clients.TryGetValue(entry.Process, out var watched))
        {
            watched = new();
            if (baseline)
            {
                watched.Hang = entry.NotRespondingUtc is not null;
                watched.Disconnect = entry.DisconnectedUtc is not null;
                watched.Crash = entry.NoLongerObservedUtc is not null && entry.FaultCheck is not null;
            }
            _clients[entry.Process] = watched;
        }
        watched.Seen = true;
        if (entry.NotRespondingUtc is not null)
        {
            if (!watched.Hang) { watched.Hang = true; Add(ref alerts, AlertKind.ClientNotResponding, entry, now); }
        }
        else watched.Hang = false;
        if (entry.DisconnectedUtc is not null)
        {
            if (!watched.Disconnect)
            {
                watched.Disconnect = true;
                bool ownLeave = ClientLogObservation.IsPlayerInitiated(entry.DisconnectReason) && entry.DisconnectReason != ClientLogObservation.AccountLaunchedElsewhereReason;
                if (!ownLeave) Add(ref alerts, AlertKind.ClientDisconnected, entry, now);
            }
        }
        else watched.Disconnect = false;
        if (entry.NoLongerObservedUtc is not null)
        {
            if (entry.FaultCheck is { } check && !watched.Crash)
            {
                watched.Crash = true;
                if (check.FaultRecordedUtc is not null) Add(ref alerts, AlertKind.ClientCrashed, entry, now);
                else if (check.CrashDumps > 0) Add(ref alerts, AlertKind.ClientPossiblyCrashed, entry, now);
            }
        }
        else watched.Crash = false;
    }

    private static void Add(ref List<ClientAlert>? alerts, AlertKind kind, RecentClientHistoryEntry entry, DateTimeOffset now)
        => (alerts ??= []).Add(new(kind, entry.Process, entry.LastKnownUserId, entry.LastKnownDestination?.PlaceId,
            kind == AlertKind.ClientDisconnected ? entry.DisconnectReason : null, now));
}

/// <summary>
/// Pure. Remembers the requests each queued batch recorded and announces the batch once every one of them has settled: arrived,
/// started unverified, needs review, failed or cancelled. A request the queue no longer lists counts as removed; a batch whose
/// requests all vanished unseen is forgotten without a word. At most <see cref="MaximumBatches"/> batches are tracked.
/// </summary>
public sealed class LaunchBatchAlertTracker
{
    public const int MaximumBatches = 16;
    private sealed record Batch(Guid[] Operations, long PlaceId);
    private readonly List<Batch> _batches = [];

    public int TrackedBatches => _batches.Count;

    public void Track(IReadOnlyList<Guid> operationIds, long placeId)
    {
        ArgumentNullException.ThrowIfNull(operationIds);
        if (operationIds.Count == 0) return;
        if (_batches.Count >= MaximumBatches) _batches.RemoveAt(0);
        _batches.Add(new([.. operationIds], placeId));
    }

    /// <summary>A request automation has nothing more to do with, whatever the user does with it next.</summary>
    public static bool Settled(LaunchState state) => state is LaunchState.Arrived or LaunchState.Failed or LaunchState.Cancelled
        or LaunchState.ClientStartedUnverified or LaunchState.Uncertain or LaunchState.ReviewRequired;

    public IReadOnlyList<AlertEvent> Observe(LaunchQueueSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_batches.Count == 0) return [];
        var operations = new Dictionary<Guid, LaunchOperation>(snapshot.Operations.Count);
        foreach (var operation in snapshot.Operations) operations[operation.Intent.OperationId] = operation;
        List<AlertEvent>? alerts = null;
        for (int index = 0; index < _batches.Count; index++)
        {
            var batch = _batches[index];
            int arrived = 0, started = 0, review = 0, failed = 0, cancelled = 0, removed = 0;
            bool pending = false;
            foreach (var id in batch.Operations)
            {
                if (!operations.TryGetValue(id, out var operation)) { removed++; continue; }
                switch (operation.State)
                {
                    case LaunchState.Arrived: arrived++; break;
                    case LaunchState.ClientStartedUnverified: started++; break;
                    case LaunchState.Uncertain or LaunchState.ReviewRequired: review++; break;
                    case LaunchState.Failed: failed++; break;
                    case LaunchState.Cancelled: cancelled++; break;
                    default: pending = true; break;
                }
                if (pending) break;
            }
            if (pending) continue;
            _batches.RemoveAt(index--);
            if (removed == batch.Operations.Length) continue;
            (alerts ??= []).Add(new(AlertKind.BatchCompleted, now, PlaceId: batch.PlaceId,
                Batch: new(batch.PlaceId, batch.Operations.Length, arrived, started, review, failed, cancelled, removed)));
        }
        return alerts is null ? [] : Array.AsReadOnly(alerts.ToArray());
    }
}
