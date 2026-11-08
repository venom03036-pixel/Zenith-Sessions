using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Zenith.Sessions.Core;

// Discord rich presence (R-api-17). Pure policy, the frame grammar of Discord's local IPC pipe and the one worker that talks to it:
// what Zenith shows (the count of running clients and, when chosen, the game), how a frame is written and read, and when a frame
// is sent (only when the content changes). Nothing here knows named pipes; the Windows layer opens the pipe and hands back a
// stream, the shell decides what the content is. Zenith writes the handshake and SET_ACTIVITY frames itself (no dependency,
// Rule 19). Discord's own replies are read for their verdict only: the READY payload describes the signed-in Discord user and is
// never kept, and an error's free text is dropped, only its code is kept (Rule 10).

/// <summary>Fixed wording and bounds. Discord shows a details line and a state line of at most 128 characters each.</summary>
public static class PresenceText
{
    public const int MaximumLength = 128, MaximumGameNameLength = 100;
    private const char LineSeparator = (char)0x2028, ParagraphSeparator = (char)0x2029;

    /// <summary>The details line: how many Roblox clients Zenith sees running.</summary>
    public static string Details(int clients) => clients switch
    {
        <= 0 => "No clients running",
        1 => "1 client running",
        _ => clients.ToString(CultureInfo.InvariantCulture) + " clients running"
    };

    /// <summary>
    /// The state line, or null when no running client has a known place: one place is "Playing" its label, several distinct
    /// places are counted. <paramref name="nameOf"/> is a name Zenith already knows for a place (favorites, recently played),
    /// never a lookup; a place without one is labelled by its ID, as elsewhere in Zenith.
    /// </summary>
    public static string? Game(IEnumerable<long> places, Func<long, string?> nameOf)
    {
        ArgumentNullException.ThrowIfNull(places); ArgumentNullException.ThrowIfNull(nameOf);
        var distinct = new HashSet<long>();
        foreach (long place in places) if (place > 0) distinct.Add(place);
        if (distinct.Count == 0) return null;
        if (distinct.Count > 1) return "In " + distinct.Count.ToString(CultureInfo.InvariantCulture) + " games";
        long only = distinct.First();
        return "Playing " + Label(only, nameOf(only));
    }

    public static string Label(long placeId, string? name) => NormalizeName(name) ?? "Place " + placeId.ToString(CultureInfo.InvariantCulture);

    /// <summary>A game name fit for one line: control characters dropped, bounded to <see cref="MaximumGameNameLength"/>; null when nothing is left.</summary>
    public static string? NormalizeName(string? name)
    {
        if (name is null) return null;
        var text = new StringBuilder(Math.Min(name.Length, MaximumGameNameLength + 1));
        foreach (char c in name)
        {
            if (char.IsControl(c) || c is LineSeparator or ParagraphSeparator) continue;
            if (text.Length == MaximumGameNameLength) { text.Append('…'); break; }
            text.Append(c);
        }
        string result = text.ToString().Trim();
        return result.Length == 0 ? null : result;
    }
}

/// <summary>What Discord is asked to show: the client count, and the game line when the user chose to include it.</summary>
public sealed record PresenceActivity
{
    public PresenceActivity(int clients, string? game = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(clients);
        if (game is not null && game.Length is < 2 or > PresenceText.MaximumLength)
            throw new ArgumentException("A game line is 2 to 128 characters, as Discord shows it.", nameof(game));
        Clients = clients; Game = game;
    }
    public int Clients { get; }
    public string? Game { get; }
    public string Details => PresenceText.Details(Clients);
    /// <summary>The status as one line, for Settings.</summary>
    public string Line => Game is null ? Details : Details + " · " + Game;
}

/// <summary>
/// The Discord application the status is shown under: a snowflake of 17 to 20 digits from the Discord Developer Portal. It is a
/// public identifier (every program showing a Discord status carries one), not a secret, so it lives in ordinary settings.
/// </summary>
public static class DiscordApplicationId
{
    public const int MinimumLength = 17, MaximumLength = 20;

    public static bool IsValid(string? id) => id is not null && id.Length is >= MinimumLength and <= MaximumLength && id.All(char.IsAsciiDigit);

    /// <summary>Accepts an empty field (no application: nothing is shown) or a snowflake. The problem text never repeats the input.</summary>
    public static bool TryParse(string? text, out string id, out string problem)
    {
        id = (text ?? "").Trim(); problem = "";
        if (id.Length == 0 || IsValid(id)) return true;
        id = "";
        problem = "A Discord application ID is 17 to 20 digits, shown as Application ID on the application's page in the Discord Developer Portal.";
        return false;
    }
}

public enum DiscordIpcOpcode { Handshake = 0, Frame = 1, Close = 2, Ping = 3, Pong = 4 }

/// <summary>One frame of Discord's local IPC: an 8-byte little-endian header (opcode, payload length) and a UTF-8 JSON payload.</summary>
public readonly record struct DiscordIpcFrame(DiscordIpcOpcode Opcode, byte[] Payload);

/// <summary>What Zenith reads from a reply: the command, the event (READY or ERROR), the nonce it answers and an error code. Never a message text.</summary>
public sealed record DiscordIpcAnswer(string? Command, string? Event, string? Nonce, int? Code)
{
    public const string Ready = "READY", Error = "ERROR", SetActivity = "SET_ACTIVITY";
    /// <summary>Discord's close code for an application ID it does not know.</summary>
    public const int InvalidApplicationCode = 4000;
}

/// <summary>The frame grammar and the two payloads Zenith sends. A frame longer than <see cref="MaximumFrameLength"/> is refused unread.</summary>
public static class DiscordIpc
{
    public const int HeaderLength = 8, MaximumFrameLength = 64 * 1024, ProtocolVersion = 1;

    public static byte[] Encode(DiscordIpcOpcode opcode, ReadOnlySpan<byte> payload)
    {
        if (!Enum.IsDefined(opcode)) throw new ArgumentOutOfRangeException(nameof(opcode));
        if (payload.Length > MaximumFrameLength) throw new ArgumentException("The frame is larger than Discord's pipe accepts.", nameof(payload));
        byte[] frame = new byte[HeaderLength + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, (int)opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    /// <summary>False for an opcode Discord does not define or a length outside [0, <see cref="MaximumFrameLength"/>].</summary>
    public static bool TryDecodeHeader(ReadOnlySpan<byte> header, out DiscordIpcOpcode opcode, out int length)
    {
        opcode = default; length = 0;
        if (header.Length < HeaderLength) return false;
        int code = BinaryPrimitives.ReadInt32LittleEndian(header);
        length = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4));
        if (!Enum.IsDefined(typeof(DiscordIpcOpcode), code) || length < 0 || length > MaximumFrameLength) return false;
        opcode = (DiscordIpcOpcode)code;
        return true;
    }

    /// <summary>
    /// Writes one frame as a single write. Never flushed: a pipe write is handed to Windows at once, and PipeStream.Flush is
    /// FlushFileBuffers, which on a pipe blocks, without cancellation, until the other side has read everything written, so a
    /// Discord that is slow to read (or one that writes to Zenith at the same moment) would hold the worker's thread.
    /// </summary>
    public static async Task WriteFrameAsync(Stream stream, DiscordIpcOpcode opcode, byte[] payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream); ArgumentNullException.ThrowIfNull(payload);
        await stream.WriteAsync(Encode(opcode, payload), ct).ConfigureAwait(false);
    }

    /// <summary>Reads one frame. End of stream is <see cref="EndOfStreamException"/>; a header Zenith does not understand is <see cref="InvalidDataException"/>, read no further.</summary>
    public static async Task<DiscordIpcFrame> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] header = new byte[HeaderLength];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        if (!TryDecodeHeader(header, out var opcode, out int length)) throw new InvalidDataException("Discord sent a frame Zenith does not understand.");
        byte[] payload = new byte[length];
        if (length > 0) await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return new(opcode, payload);
    }

    /// <summary>The handshake: protocol version 1 and the application the status is shown under.</summary>
    public static byte[] HandshakePayload(string applicationId)
    {
        if (!DiscordApplicationId.IsValid(applicationId)) throw new ArgumentException("The Discord application ID is not a snowflake.", nameof(applicationId));
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(64);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("v", ProtocolVersion);
            json.WriteString("client_id", applicationId);
            json.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>SET_ACTIVITY for this process: the details line and, when there is one, the state line. A null activity clears the status.</summary>
    public static byte[] SetActivityPayload(int processId, string nonce, PresenceActivity? activity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("cmd", DiscordIpcAnswer.SetActivity);
            json.WriteString("nonce", nonce);
            json.WriteStartObject("args");
            json.WriteNumber("pid", processId);
            if (activity is not null)
            {
                json.WriteStartObject("activity");
                json.WriteString("details", activity.Details);
                if (activity.Game is { } game) json.WriteString("state", game);
                json.WriteEndObject();
            }
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The verdict in a reply or close payload. Only cmd, evt, nonce and a numeric code are read; everything else, the message text included, is dropped.</summary>
    public static DiscordIpcAnswer ReadAnswer(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length == 0) return new(null, null, null, null);
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Discord sent a reply Zenith does not understand.");
            int? code = Code(root);
            if (code is null && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object) code = Code(data);
            return new(Text(root, "cmd"), Text(root, "evt"), Text(root, "nonce"), code);
        }
        catch (JsonException error) { throw new InvalidDataException("Discord sent a reply Zenith does not understand.", error); }

        static string? Text(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        static int? Code(JsonElement element)
            => element.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int code) ? code : null;
    }
}

/// <summary>Discord's local IPC pipe. ConnectAsync hands back a connected duplex stream, or null when Discord is not running (no pipe to connect to).</summary>
public interface IDiscordPresencePipe
{
    Task<Stream?> ConnectAsync(CancellationToken ct);
}

/// <summary>
/// What the publisher reports. Connected: the handshake was accepted. Published: Discord took the status. DiscordNotRunning: no pipe,
/// or Discord closed it (not a failure). HandshakeRefused: Discord closed the handshake with a code (4000 is an application ID it does
/// not know); the publisher ends. UpdateRefused: Discord answered a status update with an error code; the connection is kept.
/// Failed: a pipe fault, a timeout or a frame Zenith could not read; the connection is dropped and tried again later.
/// </summary>
public enum PresenceStatus { Connected, Published, DiscordNotRunning, HandshakeRefused, UpdateRefused, Failed }

/// <summary>Code is Discord's, ErrorType the fault's type only (Rule 10). ApplicationId names the application a refused handshake was for, so a late refusal of an ID no longer in use is told apart.</summary>
public sealed record PresenceReport(PresenceStatus Status, PresenceActivity? Activity = null, int? Code = null, Type? ErrorType = null, string? ApplicationId = null);

/// <summary>
/// The one worker behind the Discord status. Start opens a worker that connects to Discord's pipe the first time something is to
/// be shown, performs the handshake, and then sends SET_ACTIVITY only when the published content differs from the last one sent;
/// a repeated Publish of the same content wakes nothing. While connected it keeps one read pending, so Discord closing (end of
/// stream) and its pings are seen when they happen (Rule 14); nothing else runs between changes. While Discord is not running, or
/// after a fault, it waits for the next change or <see cref="DefaultRetryGap"/>, whichever comes first, before trying again: one
/// bounded attempt per gap, never a storm (Rule 16). Stop (Lock, Panic, exit) cancels the worker and closes the pipe, which is how
/// Discord clears a status (Rule 11). The stream is owned here from ConnectAsync to Stop (Rule 13). <see cref="Reported"/> is raised
/// on the worker's thread.
/// </summary>
public sealed class DiscordPresencePublisher : IAsyncDisposable
{
    public static readonly TimeSpan DefaultRetryGap = TimeSpan.FromSeconds(60), OperationTimeout = TimeSpan.FromSeconds(5);
    /// <summary>Frames Discord may send before the one Zenith waits for (pings, unrelated dispatches) before the exchange is given up.</summary>
    public const int MaximumUnrelatedFrames = 8;
    private readonly IDiscordPresencePipe _pipe;
    private readonly int _processId;
    private readonly TimeSpan _retryGap;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private PresenceActivity? _desired;
    private TaskCompletionSource _changed = NewSignal();
    private CancellationTokenSource? _stop;
    private string? _applicationId;
    private Task _work = Task.CompletedTask;
    private int _disposed;
    private volatile bool _connected;

    public DiscordPresencePublisher(IDiscordPresencePipe pipe, int processId, TimeSpan? retryGap = null, TimeProvider? clock = null)
    {
        _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        _processId = processId;
        _retryGap = retryGap ?? DefaultRetryGap;
        if (_retryGap <= TimeSpan.Zero || _retryGap > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(retryGap));
        _clock = clock ?? TimeProvider.System;
    }

    public event Action<PresenceReport>? Reported;
    /// <summary>A worker runs: Start was called and neither Stop nor a refused handshake has ended it.</summary>
    public bool IsRunning { get { lock (_gate) return _stop is not null; } }
    /// <summary>The handshake was accepted and the pipe is open.</summary>
    public bool IsConnected => _connected;
    public string? ApplicationId { get { lock (_gate) return _applicationId; } }
    /// <summary>The worker, for exit and tests; completed while nothing runs.</summary>
    public Task Work { get { lock (_gate) return _work; } }

    /// <summary>Idempotent for the same application; a different one ends the running worker and starts a new one after it.</summary>
    public void Start(string applicationId)
    {
        if (!DiscordApplicationId.IsValid(applicationId)) throw new ArgumentException("The Discord application ID is not a snowflake.", nameof(applicationId));
        CancellationTokenSource? previous = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_stop is not null)
            {
                if (string.Equals(_applicationId, applicationId, StringComparison.Ordinal)) return;
                previous = _stop;
            }
            var stop = new CancellationTokenSource();
            _stop = stop; _applicationId = applicationId;
            _work = RunAsync(applicationId, stop, _work);
        }
        previous?.Cancel();
    }

    /// <summary>Any thread. The latest content wins; the same content again is not a change. False when nothing runs: it is not kept.</summary>
    public bool Publish(PresenceActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        TaskCompletionSource? signal = null;
        lock (_gate)
        {
            if (_stop is null) return false;
            if (activity == _desired) return true;
            _desired = activity;
            signal = _changed; _changed = NewSignal();
        }
        signal.TrySetResult();
        return true;
    }

    /// <summary>Idempotent. Cancels the worker; it closes the pipe on its way out, which clears the status in Discord.</summary>
    public void Stop()
    {
        CancellationTokenSource? stop;
        lock (_gate) { stop = _stop; _stop = null; _applicationId = null; _desired = null; }
        stop?.Cancel();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Report(PresenceReport report) => Reported?.Invoke(report);

    private async Task RunAsync(string applicationId, CancellationTokenSource stop, Task previous)
    {
        var ct = stop.Token;
        Connection? link = null;
        try
        {
            // Never runs inside Start's lock, and never holds a pipe beside an earlier worker that is still closing its own.
            await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding);
            bool absenceReported = false;
            PresenceActivity? shown = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                PresenceActivity? desired; Task changed;
                lock (_gate) { desired = _desired; changed = _changed.Task; }
                if (desired is null) { await changed.WaitAsync(ct).ConfigureAwait(false); continue; }
                if (link is null)
                {
                    var opened = await OpenAsync(applicationId, ct).ConfigureAwait(false);
                    if (opened.Link is null)
                    {
                        var problem = opened.Problem!;
                        if (problem.Status == PresenceStatus.HandshakeRefused) { Report(problem); return; }
                        if (problem.Status != PresenceStatus.DiscordNotRunning || !absenceReported) Report(problem);
                        absenceReported = problem.Status == PresenceStatus.DiscordNotRunning;
                        await WaitForChangeOrGapAsync(changed, ct).ConfigureAwait(false);
                        continue;
                    }
                    link = opened.Link; shown = null; absenceReported = false; _connected = true;
                    Report(new(PresenceStatus.Connected));
                }
                if (desired != shown)
                {
                    var sent = await SetActivityAsync(link, desired, ct).ConfigureAwait(false);
                    Report(sent.Report);
                    if (!sent.KeepLink)
                    {
                        await DropAsync().ConfigureAwait(false);
                        absenceReported = sent.Report.Status == PresenceStatus.DiscordNotRunning;
                        await WaitForChangeOrGapAsync(changed, ct).ConfigureAwait(false);
                        continue;
                    }
                    shown = desired;
                }
                // Idle: the next change, or whatever Discord sends first (a ping to answer, or the end of the stream when it quits).
                var pending = link.Pending(ct);
                var first = await Task.WhenAny(changed, pending).ConfigureAwait(false);
                if (first != pending) continue;
                link.Consumed();
                PresenceReport? ended = null;
                try
                {
                    var frame = await pending.ConfigureAwait(false);
                    if (frame.Opcode == DiscordIpcOpcode.Ping) await DiscordIpc.WriteFrameAsync(link.Stream, DiscordIpcOpcode.Pong, frame.Payload, ct).ConfigureAwait(false);
                    else if (frame.Opcode == DiscordIpcOpcode.Close) ended = new(PresenceStatus.DiscordNotRunning, Code: DiscordIpc.ReadAnswer(frame.Payload).Code);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (EndOfStreamException) { ended = new(PresenceStatus.DiscordNotRunning); }
                catch (Exception error) when (IsPipeFault(error)) { ended = new(PresenceStatus.Failed, ErrorType: error.GetType()); }
                if (ended is null) continue;
                await DropAsync().ConfigureAwait(false);
                Report(ended);
                absenceReported = ended.Status == PresenceStatus.DiscordNotRunning;
                await WaitForChangeOrGapAsync(changed, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            await DropAsync().ConfigureAwait(false);
            lock (_gate) { if (ReferenceEquals(_stop, stop)) { _stop = null; _applicationId = null; _desired = null; } }
            stop.Dispose();
        }

        async Task DropAsync()
        {
            _connected = false;
            if (link is null) return;
            var closing = link; link = null;
            await closing.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task WaitForChangeOrGapAsync(Task changed, CancellationToken ct)
    {
        using var gap = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(_retryGap, _clock, gap.Token);
        await Task.WhenAny(changed, delay).ConfigureAwait(false);
        gap.Cancel();
        await delay.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        ct.ThrowIfCancellationRequested();
    }

    private static bool IsPipeFault(Exception error)
        => error is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException;

    private async Task<(Connection? Link, PresenceReport? Problem)> OpenAsync(string applicationId, CancellationToken ct)
    {
        Stream? stream;
        try { stream = await _pipe.ConnectAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (IsPipeFault(error)) { return (null, new(PresenceStatus.Failed, ErrorType: error.GetType())); }
        if (stream is null) return (null, new(PresenceStatus.DiscordNotRunning));
        var link = new Connection(stream);
        try
        {
            await DiscordIpc.WriteFrameAsync(link.Stream, DiscordIpcOpcode.Handshake, DiscordIpc.HandshakePayload(applicationId), ct).ConfigureAwait(false);
            for (int unrelated = 0; unrelated <= MaximumUnrelatedFrames; unrelated++)
            {
                var frame = await link.TakeAsync(OperationTimeout, _clock, ct).ConfigureAwait(false);
                switch (frame.Opcode)
                {
                    case DiscordIpcOpcode.Ping:
                        await DiscordIpc.WriteFrameAsync(link.Stream, DiscordIpcOpcode.Pong, frame.Payload, ct).ConfigureAwait(false);
                        continue;
                    case DiscordIpcOpcode.Close:
                        await link.DisposeAsync().ConfigureAwait(false);
                        return (null, new(PresenceStatus.HandshakeRefused, Code: DiscordIpc.ReadAnswer(frame.Payload).Code, ApplicationId: applicationId));
                    case DiscordIpcOpcode.Frame:
                        var answer = DiscordIpc.ReadAnswer(frame.Payload);
                        if (answer.Event == DiscordIpcAnswer.Error)
                        {
                            await link.DisposeAsync().ConfigureAwait(false);
                            return (null, new(PresenceStatus.HandshakeRefused, Code: answer.Code, ApplicationId: applicationId));
                        }
                        if (answer.Event == DiscordIpcAnswer.Ready) return (link, null);
                        continue;
                    default:
                        continue;
                }
            }
            throw new InvalidDataException("Discord did not answer the handshake.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await link.DisposeAsync().ConfigureAwait(false); throw; }
        catch (Exception error) when (IsPipeFault(error))
        {
            await link.DisposeAsync().ConfigureAwait(false);
            return (null, new(PresenceStatus.Failed, ErrorType: error.GetType()));
        }
    }

    private async Task<(PresenceReport Report, bool KeepLink)> SetActivityAsync(Connection link, PresenceActivity activity, CancellationToken ct)
    {
        string nonce = Guid.NewGuid().ToString("D");
        try
        {
            await DiscordIpc.WriteFrameAsync(link.Stream, DiscordIpcOpcode.Frame, DiscordIpc.SetActivityPayload(_processId, nonce, activity), ct).ConfigureAwait(false);
            for (int unrelated = 0; unrelated <= MaximumUnrelatedFrames; unrelated++)
            {
                var frame = await link.TakeAsync(OperationTimeout, _clock, ct).ConfigureAwait(false);
                switch (frame.Opcode)
                {
                    case DiscordIpcOpcode.Ping:
                        await DiscordIpc.WriteFrameAsync(link.Stream, DiscordIpcOpcode.Pong, frame.Payload, ct).ConfigureAwait(false);
                        continue;
                    case DiscordIpcOpcode.Close:
                        return (new(PresenceStatus.DiscordNotRunning, Code: DiscordIpc.ReadAnswer(frame.Payload).Code), false);
                    case DiscordIpcOpcode.Frame:
                        var answer = DiscordIpc.ReadAnswer(frame.Payload);
                        if (!string.Equals(answer.Nonce, nonce, StringComparison.Ordinal)) continue;
                        return answer.Event == DiscordIpcAnswer.Error
                            ? (new(PresenceStatus.UpdateRefused, activity, answer.Code), true)
                            : (new(PresenceStatus.Published, activity), true);
                    default:
                        continue;
                }
            }
            throw new InvalidDataException("Discord did not answer the status update.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (EndOfStreamException) { return (new(PresenceStatus.DiscordNotRunning), false); }
        catch (Exception error) when (IsPipeFault(error)) { return (new(PresenceStatus.Failed, activity, ErrorType: error.GetType()), false); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { await Work.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); return; }
        Stop();
        await Work.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>The open pipe and the one read kept pending on it. Dispose closes the pipe first, which ends that read.</summary>
    private sealed class Connection(Stream stream) : IAsyncDisposable
    {
        private Task<DiscordIpcFrame>? _pending;
        public Stream Stream { get; } = stream;
        public Task<DiscordIpcFrame> Pending(CancellationToken ct) => _pending ??= DiscordIpc.ReadFrameAsync(Stream, ct);
        public void Consumed() => _pending = null;
        public async Task<DiscordIpcFrame> TakeAsync(TimeSpan timeout, TimeProvider clock, CancellationToken ct)
        {
            var frame = await Pending(ct).WaitAsync(timeout, clock, ct).ConfigureAwait(false);
            _pending = null;
            return frame;
        }
        public async ValueTask DisposeAsync()
        {
            try { await Stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            // Closing the pipe ends the pending read; it is awaited (bounded) so no fault is left unobserved.
            if (_pending is { } pending) await ((Task)pending).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _pending = null;
        }
    }
}
