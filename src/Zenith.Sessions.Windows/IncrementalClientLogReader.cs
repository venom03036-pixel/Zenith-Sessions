using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed record ClientLogReaderOptions(int MaximumBytesPerRead = 64 * 1024, int MaximumLineBytes = 8192,
    int MaximumObservationsPerRead = 128, TimeSpan? Deadline = null)
{
    public ClientLogReaderOptions Validated()
    {
        if (MaximumBytesPerRead is < 1 or > 1024 * 1024 || MaximumLineBytes is < 64 or > 65536
            || MaximumObservationsPerRead is < 1 or > 1024 || Deadline is { } deadline && (deadline < TimeSpan.Zero || deadline > TimeSpan.FromSeconds(30)))
            throw new ArgumentOutOfRangeException(nameof(ClientLogReaderOptions));
        return this;
    }
}

/// <summary>
/// Reads only a caller-selected local file, never discovers/enumerates logs. Call ReadAsync on filesystem hints
/// or a shared bounded reconciliation schedule; no timer, watcher or worker is created. One immediate owner,
/// bounded bytes/line/results, no raw output and no disk writes. Await owned reads before Dispose.
/// </summary>
public sealed class IncrementalClientLogReader : IDisposable
{
    // Formats are independently implemented from a public-text study, not copied parser code:
    // https://raw.githubusercontent.com/inspectsoftware/RoALT/v0.9.2/src-tauri/src/instances/logwatch.rs
    // https://en.help.roblox.com/hc/en-us/articles/360016022492-How-to-Retrieve-Roblox-Studio-Logs
    // The official page documents locations/names, NOT event schemas. Tests include synthetic cases
    // and one structurally sanitized historical Win32 0.745 fixture. Neither verifies a live writer,
    // account binding, every version/variant or authentication; runtime format support stays Unverified.
    public const ClientLogFormatSupport FormatSupport = ClientLogFormatSupport.Unverified;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _path;
    private readonly ClientLogReaderOptions _options;
    private readonly TimeProvider _clock;
    private readonly Guid _readerId = Guid.NewGuid();
    private readonly SemaphoreSlim _owner = new(1, 1);
    private readonly byte[] _buffer = new byte[4096];
    private readonly byte[] _line;
    private readonly byte[] _anchor = new byte[64];
    private FileIdentity? _identity;
    private long _generation, _offset, _completeOffset, _lineStart;
    private long _readSequence;
    private int _lineLength, _anchorLength, _anchorCursor;
    private bool _discardLine, _disposed;
    internal readonly record struct FileIdentity(uint Volume, uint IndexHigh, uint IndexLow, uint CreationHigh, uint CreationLow);
    // Only the single Windows correlator owner reads this after awaiting ReadAsync. No path or raw data escapes.
    internal FileIdentity? SourceIdentity => _identity;
    internal static FileIdentity ProbeIdentity(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        ValidatePhysicalPath(stream.SafeFileHandle, Path.GetFullPath(path));
        return Identity(stream.SafeFileHandle);
    }

    /// <summary>
    /// The browser trackers one log's bounded head names (at most four), with the file's identity, creation time and
    /// length when read. Complete: the head can no longer change (it reached the first join line or HeadBytes).
    /// Not a secret, and corroboration only. No path or line text is kept.
    /// </summary>
    internal sealed record ClientLogHead(FileIdentity Identity, long CreatedUtcTicks, long Length, long[] Trackers, bool Complete);
    internal const int HeadBytes = 64 * 1024;
    private const int MaximumHeadTrackers = 4;
    private static readonly long FileTimeEpochTicks = DateTime.FromFileTimeUtc(0).Ticks;

    /// <summary>
    /// Reads at most HeadBytes from the start of one caller-selected local log, synchronously, for the browser tracker
    /// the client writes near its top ("websiteBTId is N", or "BTID is overriden to N" when it was replaced; marker
    /// shapes from the same public-text study as the facts above, so support stays Unverified). Only complete lines
    /// before the first "Joining game" line count: a game's own output cannot appear before the client joins one.
    /// A file with the same identity and length as previous is not read again. Bytes are cleared before return.
    /// </summary>
    internal static ClientLogHead ReadHead(string path, ClientLogHead? previous, CancellationToken ct)
    {
        string full = Path.GetFullPath(path);
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 0, FileOptions.SequentialScan);
        ValidatePhysicalPath(stream.SafeFileHandle, full);
        var identity = Identity(stream.SafeFileHandle);
        long length = stream.Length;
        if (previous is { } prior && prior.Identity == identity && prior.Length == length) return prior;
        long fileTime = ((long)identity.CreationHigh << 32) | identity.CreationLow;
        long created = fileTime >= 0 && fileTime <= DateTime.MaxValue.Ticks - FileTimeEpochTicks ? fileTime + FileTimeEpochTicks : long.MinValue;
        int count = (int)Math.Min(length, HeadBytes);
        byte[] bytes = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(1, count));
        try
        {
            int read = 0;
            while (read < count)
            {
                ct.ThrowIfCancellationRequested();
                int next = stream.Read(bytes, read, count - read);
                if (next == 0) break;
                read += next;
            }
            var trackers = new List<long>(MaximumHeadTrackers);
            bool joined = false;
            for (int index = 0, start = 0; index < read && !joined; index++)
            {
                if (bytes[index] != (byte)'\n') continue;
                int end = index > start && bytes[index - 1] == (byte)'\r' ? index - 1 : index;
                // A partial last line waits for a later read; an overlong line is skipped, as the incremental reader does.
                if (end - start <= 8192) joined = HeadLine(bytes.AsSpan(start, end - start), trackers);
                start = index + 1;
            }
            return new(identity, created, length, trackers.ToArray(), joined || read >= HeadBytes);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(bytes, clearArray: true); }
    }

    /// <summary>The same "time,... [first tag] body" shape facts use. Returns true at the first join line, which ends the head.</summary>
    private static bool HeadLine(ReadOnlySpan<byte> bytes, List<long> trackers)
    {
        string line;
        try { line = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return false; }
        int comma = line.IndexOf(',');
        if (comma is < 20 or > 40 || !DateTimeOffset.TryParse(line.AsSpan(0, comma), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _)) return false;
        int opening = line.IndexOf('[', comma), closing = opening < 0 ? -1 : line.IndexOf(']', opening + 1);
        if (opening < 0 || closing < 0 || closing - opening > 80) return false;
        var tag = line.AsSpan(opening + 1, closing - opening - 1);
        var body = line.AsSpan(closing + 1).TrimStart();
        if (tag.SequenceEqual("FLog::Output") && body.StartsWith("! Joining game '", StringComparison.Ordinal)) return true;
        if ((HeadTracker(body, "websiteBTId is ") ?? HeadTracker(body, "BTID is overriden to ")) is { } tracker
            && trackers.Count < MaximumHeadTrackers && !trackers.Contains(tracker)) trackers.Add(tracker);
        return false;
    }
    private static long? HeadTracker(ReadOnlySpan<char> body, ReadOnlySpan<char> marker)
    {
        int at = body.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0 || at > 0 && char.IsAsciiLetterOrDigit(body[at - 1])) return null;
        return Number(body[(at + marker.Length)..]) is { } value && value <= LaunchProcessReceipt.MaximumBrowserTrackerId ? value : null;
    }

    public IncrementalClientLogReader(string path, ClientLogReaderOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        // Device paths, UNC shares and alternate data streams are outside this local diagnostic reader.
        if (_path.StartsWith(@"\\", StringComparison.Ordinal) || _path.AsSpan(Path.GetPathRoot(_path)!.Length).Contains(':'))
            throw new ArgumentException("A normal local log file is required.", nameof(path));
        _options = (options ?? new()).Validated();
        _line = new byte[_options.MaximumLineBytes];
        _clock = timeProvider ?? TimeProvider.System;
    }

    public async Task<ClientLogBatch> ReadAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        if (!await _owner.WaitAsync(0, ct).ConfigureAwait(false)) return Batch(ClientLogReadStatus.Busy, ClientLogDiscontinuity.None, [], 0, false);
        var observations = new List<ClientLogObservation>();
        int rejected = 0;
        var discontinuity = ClientLogDiscontinuity.None;
        var checkpoint = (_identity, _generation, _offset, _completeOffset, _lineStart, _lineLength, _anchorLength, _anchorCursor, _discardLine);
        byte[] previousLine = _line.AsSpan(0, _lineLength).ToArray(), previousAnchor = _anchor.ToArray();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_options.Deadline == TimeSpan.Zero) deadline.Cancel();
        else deadline.CancelAfter(_options.Deadline ?? TimeSpan.FromSeconds(2));
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            deadline.Token.ThrowIfCancellationRequested();
            // OS-local open is synchronous; cancellable/deadlined reads follow. No network path is permitted.
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            ValidatePhysicalPath(stream.SafeFileHandle, _path);
            var identity = Identity(stream.SafeFileHandle);
            if (_identity is null)
            {
                Reset(); _identity = identity; discontinuity = ClientLogDiscontinuity.Initial;
            }
            else if (_identity != identity)
            {
                Reset(); _identity = identity; discontinuity = ClientLogDiscontinuity.Replaced;
            }
            else if (stream.Length < _offset || !await AnchorMatchesAsync(stream, deadline.Token).ConfigureAwait(false))
            {
                Reset(); discontinuity = ClientLogDiscontinuity.TruncatedOrRewritten;
            }
            long available = Math.Max(0, stream.Length - _offset);
            int budget = (int)Math.Min(available, _options.MaximumBytesPerRead);
            var readUtc = _clock.GetUtcNow();
            stream.Position = _offset;
            while (budget > 0 && observations.Count < _options.MaximumObservationsPerRead)
            {
                deadline.Token.ThrowIfCancellationRequested();
                int count = await stream.ReadAsync(_buffer.AsMemory(0, Math.Min(_buffer.Length, budget)), deadline.Token).ConfigureAwait(false);
                if (count == 0) break;
                int used = 0;
                while (used < count && observations.Count < _options.MaximumObservationsPerRead)
                {
                    byte next = _buffer[used++]; _offset++; budget--;
                    _anchor[_anchorCursor] = next; _anchorCursor = (_anchorCursor + 1) % _anchor.Length;
                    _anchorLength = Math.Min(_anchorLength + 1, _anchor.Length);
                    if (next == (byte)'\n')
                    {
                        if (!_discardLine)
                        {
                            int length = _lineLength > 0 && _line[_lineLength - 1] == (byte)'\r' ? _lineLength - 1 : _lineLength;
                            try
                            {
                                var parsed = Parse(StrictUtf8.GetString(_line, 0, length), StreamId, _lineStart, _offset, readUtc);
                                if (parsed is not null) observations.Add(parsed);
                            }
                            catch (DecoderFallbackException) { rejected++; }
                        }
                        else rejected++;
                        CryptographicOperations.ZeroMemory(_line.AsSpan(0, _lineLength));
                        _lineLength = 0; _discardLine = false; _lineStart = _offset; _completeOffset = _offset;
                    }
                    else if (!_discardLine)
                    {
                        if (_lineLength == _line.Length)
                        {
                            CryptographicOperations.ZeroMemory(_line); _lineLength = 0; _discardLine = true;
                        }
                        else _line[_lineLength++] = next;
                    }
                }
                CryptographicOperations.ZeroMemory(_buffer);
                // A result cap may stop inside the fetched chunk; next read seeks the consumed cursor, so no line is lost.
                if (used < count) break;
            }
            return Batch(ClientLogReadStatus.Ready, discontinuity, observations, rejected, stream.Length > _offset);
        }
        catch (FileNotFoundException) { return Missing(observations, rejected); }
        catch (DirectoryNotFoundException) { return Missing(observations, rejected); }
        catch (UnauthorizedAccessException) { return Batch(ClientLogReadStatus.AccessDenied, discontinuity, observations, rejected, false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return Batch(ClientLogReadStatus.DeadlineExceeded, discontinuity, observations, rejected, false); }
        catch (OperationCanceledException)
        {
            // No result was delivered: restore the owned cursor so canceled observations cannot be silently lost.
            (_identity, _generation, _offset, _completeOffset, _lineStart, _lineLength, _anchorLength, _anchorCursor, _discardLine) = checkpoint;
            CryptographicOperations.ZeroMemory(_line); previousLine.CopyTo(_line, 0); previousAnchor.CopyTo(_anchor, 0);
            throw;
        }
        catch (IOException) { return Batch(ClientLogReadStatus.IoFailure, discontinuity, observations, rejected, false); }
        catch (Win32Exception) { return Batch(ClientLogReadStatus.IoFailure, discontinuity, observations, rejected, false); }
        finally
        {
            CryptographicOperations.ZeroMemory(_buffer); CryptographicOperations.ZeroMemory(previousLine);
            CryptographicOperations.ZeroMemory(previousAnchor); _owner.Release();
        }
    }

    private ClientLogBatch Missing(List<ClientLogObservation> observations, int rejected)
    {
        if (_identity is not null) { Reset(); _identity = null; }
        return Batch(ClientLogReadStatus.Missing, ClientLogDiscontinuity.Missing, observations, rejected, false);
    }
    private ClientLogStreamId StreamId => new(_readerId, _generation);
    private ClientLogBatch Batch(ClientLogReadStatus status, ClientLogDiscontinuity discontinuity,
        List<ClientLogObservation> observations, int rejected, bool more) => new(StreamId, status, discontinuity,
            _offset, _completeOffset, more, rejected, observations.AsReadOnly())
        { ReadSequence = status == ClientLogReadStatus.Ready ? ++_readSequence : 0 };
    private void Reset()
    {
        if (_generation == long.MaxValue) throw new InvalidOperationException("This log reader generation is exhausted.");
        _generation++; _offset = 0; _completeOffset = 0; _lineStart = 0; _lineLength = 0; _anchorLength = 0; _anchorCursor = 0; _discardLine = false;
        CryptographicOperations.ZeroMemory(_line); CryptographicOperations.ZeroMemory(_anchor);
    }
    private async Task<bool> AnchorMatchesAsync(FileStream stream, CancellationToken ct)
    {
        if (_anchorLength == 0) return true;
        stream.Position = _offset - _anchorLength;
        await stream.ReadExactlyAsync(_buffer.AsMemory(0, _anchorLength), ct).ConfigureAwait(false);
        int first = (_anchorCursor - _anchorLength + _anchor.Length) % _anchor.Length;
        bool matches = true;
        for (int index = 0; index < _anchorLength; index++)
            if (_buffer[index] != _anchor[(first + index) % _anchor.Length]) { matches = false; break; }
        CryptographicOperations.ZeroMemory(_buffer.AsSpan(0, _anchorLength));
        return matches;
    }
    private static ClientLogObservation? Parse(string line, ClientLogStreamId stream, long start, long end, DateTimeOffset readUtc)
    {
        // First tag only: text printed by a game cannot smuggle a second network/identity tag through Output.
        int comma = line.IndexOf(',');
        if (comma is < 20 or > 40 || !DateTimeOffset.TryParse(line.AsSpan(0, comma), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)) return null;
        int opening = line.IndexOf('[', comma), closing = opening < 0 ? -1 : line.IndexOf(']', opening + 1);
        if (opening < 0 || closing < 0 || closing - opening > 80) return null;
        var tag = line.AsSpan(opening + 1, closing - opening - 1);
        var body = line.AsSpan(closing + 1).TrimStart();
        if (tag.SequenceEqual("FLog::Output") && body.StartsWith("! Joining game '", StringComparison.Ordinal))
        {
            var rest = body[16..];
            int quote = rest.IndexOf('\'');
            if (quote != 36 || !Guid.TryParseExact(rest[..quote], "D", out var job) || job == Guid.Empty) return null;
            rest = rest[(quote + 1)..];
            if (!rest.StartsWith(" place ", StringComparison.Ordinal) || Number(rest[7..]) is not { } place) return null;
            return new(stream, start, end, time, readUtc, ClientLogFact.Joining, PlaceId: place, JobId: job);
        }
        if (tag.SequenceEqual("FLog::GameJoinLoadTime") && body.StartsWith("Report game_join_loadtime:", StringComparison.Ordinal))
        {
            int marker = body.IndexOf("userid:", StringComparison.Ordinal);
            if (marker < 0 || marker > 0 && !char.IsWhiteSpace(body[marker - 1]) && body[marker - 1] != ',') return null;
            if (Number(body[(marker + 7)..]) is not { } user) return null;
            return new(stream, start, end, time, readUtc, ClientLogFact.UserIdentityObserved, UserId: user);
        }
        if (tag.SequenceEqual("FLog::Network") && StartsPhrase(body, "Replicator created"))
            return new(stream, start, end, time, readUtc, ClientLogFact.Connected);
        if (Disconnect(tag, body, out int? reason))
            return new(stream, start, end, time, readUtc, ClientLogFact.Disconnected, DisconnectReason: reason);
        if (tag.SequenceEqual("FLog::UgcExperienceController") && StartsPhrase(body, "UgcExperienceController: doTeleport"))
            return new(stream, start, end, time, readUtc, ClientLogFact.Teleporting);
        // Its tag is not yet seen in a recorded line, so any first tag except the channels a game's own print/warn/error write.
        if (!GameWritable(tag) && ContainsToken(body, "challengedByGcs"))
            return new(stream, start, end, time, readUtc, ClientLogFact.HumanCheck);
        return null;
    }
    /// <summary>Tags are those seen in recorded 0.743 lines; only a bounded numeric reason code leaves this method, never text.</summary>
    private static bool Disconnect(ReadOnlySpan<char> tag, ReadOnlySpan<char> body, out int? reason)
    {
        reason = null;
        bool network = tag.SequenceEqual("FLog::Network"), transport = tag.SequenceEqual("DFLog::RbxTransportDummyClient");
        // "Time to disconnect replication data: <seconds>" carries a duration, not a reason.
        if (network && StartsPhrase(body, "Time to disconnect replication data")) return true;
        if (network && Marker(body, "Disconnection Notification", ". Reason:", out reason)
            || network && Marker(body, "Lost connection with reason", " :", out reason)
            || network && Marker(body, "Sending disconnect with reason", ":", out reason)
            || transport && Marker(body, "Disconnected from server for reason", ":", out reason)
            || (network || transport) && Marker(body, "Connection closed by server", " for reason:", out reason)) return true;
        return false;
    }
    /// <summary>"marker lead N" or "marker lead Label: N", e.g. ". Reason: 267" or ": Player: 285 (Name)". Other text has no code.</summary>
    private static bool Marker(ReadOnlySpan<char> body, ReadOnlySpan<char> marker, ReadOnlySpan<char> lead, out int? reason)
    {
        reason = null;
        if (!StartsPhrase(body, marker)) return false;
        var rest = body[marker.Length..];
        // A space before the colon is optional ("reason : text" and "reason: 267" are both seen).
        if (lead[0] == ' ' && !rest.StartsWith(lead, StringComparison.Ordinal)) lead = lead[1..];
        if (!rest.StartsWith(lead, StringComparison.Ordinal)) return true;
        rest = rest[lead.Length..].TrimStart();
        int label = 0;
        while (label < rest.Length && label <= 16 && char.IsAsciiLetter(rest[label])) label++;
        if (label is > 0 and <= 16 && label < rest.Length && rest[label] == ':') rest = rest[(label + 1)..].TrimStart();
        if (Number(rest) is { } code && code <= ClientLogObservation.MaximumDisconnectReason) reason = (int)code;
        return true;
    }
    private static bool GameWritable(ReadOnlySpan<char> tag)
    {
        int separator = tag.IndexOf("::", StringComparison.Ordinal);
        var channel = separator < 0 ? tag : tag[(separator + 2)..];
        return channel.SequenceEqual("Output") || channel.SequenceEqual("Warning") || channel.SequenceEqual("Error");
    }
    private static bool ContainsToken(ReadOnlySpan<char> body, ReadOnlySpan<char> token)
    {
        for (int offset = 0, found; (found = body[offset..].IndexOf(token, StringComparison.Ordinal)) >= 0; offset += found + 1)
        {
            int at = offset + found, after = at + token.Length;
            if ((at == 0 || !char.IsAsciiLetterOrDigit(body[at - 1]))
                && (after == body.Length || !char.IsAsciiLetterOrDigit(body[after]))) return true;
        }
        return false;
    }
    private static bool StartsPhrase(ReadOnlySpan<char> body, ReadOnlySpan<char> marker) => body.StartsWith(marker, StringComparison.Ordinal)
        && (body.Length == marker.Length || char.IsWhiteSpace(body[marker.Length]) || body[marker.Length] is ':' or '.');
    private static long? Number(ReadOnlySpan<char> text)
    {
        int end = 0;
        while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
        if (end == 0 || end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ',' and not ';') return null;
        return long.TryParse(text[..end], NumberStyles.None, CultureInfo.InvariantCulture, out long value) && value > 0 ? value : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (!_owner.Wait(0)) throw new InvalidOperationException("Await the owned log read before disposal.");
        _disposed = true;
        CryptographicOperations.ZeroMemory(_line); CryptographicOperations.ZeroMemory(_buffer); CryptographicOperations.ZeroMemory(_anchor);
        _owner.Release(); _owner.Dispose();
    }
    private static FileIdentity Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(info.VolumeSerial, info.IndexHigh, info.IndexLow, info.Creation.High, info.Creation.Low);
    }
    private static void ValidatePhysicalPath(SafeFileHandle handle, string expected)
    {
        // Reject redirected paths before reading contents. No junction/symlink may turn a discovered log into another file.
        var path = new StringBuilder(1024);
        uint length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (length >= path.Capacity) throw new UnauthorizedAccessException("The local log path cannot be verified.");
        string actual = path.ToString();
        if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("A redirected local log path is outside this reader.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes; public FileTime Creation, Access, Write; public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
