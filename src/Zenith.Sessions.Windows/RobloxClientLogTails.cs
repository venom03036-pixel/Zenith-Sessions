using System.Buffers;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>One recognised Roblox client log line, reduced to the fields Zenith parses from it. Never the line, a player ID, a URL or a path.</summary>
public sealed record ClientLogTailEntry(DateTimeOffset LoggedUtc, ClientLogFact Fact, long? PlaceId = null, Guid? JobId = null, int? DisconnectReason = null);

public enum ClientLogTailOutcome { Read, AccessDenied, ReadFailed }

/// <summary>
/// The tail of one recent Roblox Player log: its order (1 is the most recently written), whether the Store package wrote it,
/// its last write and size, how much of its end was read and how many complete lines that held, how many recognised entries
/// beyond the per-log cap were dropped (the oldest go first), and the recognised entries in file order. No path or name.
/// </summary>
public sealed record ClientLogTail(int Order, bool StorePackage, DateTimeOffset ModifiedUtc, long Length, long BytesRead, int LinesRead,
    int EntriesDropped, ClientLogTailOutcome Outcome, IReadOnlyList<ClientLogTailEntry> Entries);

/// <summary>
/// What one read found: how many documented log folders were enumerated (<see cref="FoldersChecked"/>) or could not be
/// (<see cref="FoldersUnreadable"/>), how many recent Player logs were found before the per-report cap, whether the read stopped
/// at a budget, and the tails included, newest first.
/// </summary>
public sealed record ClientLogTailReport(DateTimeOffset ReadUtc, int FoldersChecked, int FoldersUnreadable, int LogsFound, bool BudgetLimited,
    IReadOnlyList<ClientLogTail> Logs)
{
    public int EntryCount { get { int total = 0; foreach (var log in Logs) total += log.Entries.Count; return total; } }
}

/// <summary>Reads recent Roblox Player log tails as typed entries for the support report. The shell owns the call; tests inject a fake.</summary>
public interface IClientLogTailReader
{
    /// <summary>Never throws for a log that cannot be read (its tail says so); throws <see cref="OperationCanceledException"/> when stopped.</summary>
    Task<ClientLogTailReport> ReadAsync(CancellationToken ct);
}

/// <summary>
/// Recent Roblox client log tails for the support report, as typed entries only. Zenith's own version of a capability the RoALT
/// reference has (it appends the last few hundred raw lines of several logs); the owner's decision of 2026-11-08 shaped it:
/// opt-in at save time, passed through an allowlist filter, bounded per client. The allowlist is the one the log correlator
/// already parses with (<see cref="IncrementalClientLogReader.Parse"/>): a line is either one of the recognised shapes (join,
/// identity, connection, teleport, disconnect, human check) and yields its typed fields, or it yields nothing. No line text is
/// kept at all, so a URL, a ticket, a cookie, a chat message or an address can never reach the report; the player ID the identity
/// line carries is dropped too, and so are the file's path and name. The bytes read are cleared before the buffer is returned.
/// Only the current user's documented Roblox log roots below LocalApplicationData (<see cref="RobloxClientFolders.LogRoots"/>),
/// each below the anchor with no reparse point on the way, only <c>*.log</c> files directly inside them whose name says a Player
/// client wrote them (not Studio, not the crash handler), only logs written within <see cref="MaximumAge"/>, the newest
/// <see cref="MaximumLogs"/> of those, the last <see cref="TailBytes"/> of each, and the newest <see cref="MaximumEntriesPerLog"/>
/// recognised lines of each. Each file is opened sharing read, write and delete, so a running client is never disturbed, and its
/// final path is checked against the enumerated one before any byte is read. Nothing is written and nothing runs on its own.
/// Rule 18: one read inspects at most <see cref="MaximumEntries"/> directory entries and at most <see cref="MaximumLogs"/> files
/// within <see cref="Deadline"/>, holds one rented <see cref="TailBytes"/> buffer, creates one pool task per call and no timer.
/// </summary>
public sealed class RobloxClientLogTails : IClientLogTailReader
{
    public const int MaximumLogs = 8;
    public const int TailBytes = 256 * 1024;
    public const int MaximumEntriesPerLog = 200;
    public static readonly TimeSpan MaximumAge = TimeSpan.FromDays(1);
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    internal const int MaximumEntries = 8192;
    internal const int MaximumLineBytes = 8192;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly (string Path, ClientLogVariant Variant)[] _roots;
    private readonly string _anchor;
    private readonly TimeProvider _clock;
    private readonly record struct Found(string Path, bool StorePackage, DateTimeOffset ModifiedUtc, long Length);

    public RobloxClientLogTails()
        : this(RobloxClientFolders.LogRoots(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) { }

    internal RobloxClientLogTails(IReadOnlyList<(string Path, ClientLogVariant Variant)> roots, string trustedAnchor, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedAnchor);
        _roots = roots.ToArray(); _anchor = trustedAnchor; _clock = clock ?? TimeProvider.System;
    }

    public Task<ClientLogTailReport> ReadAsync(CancellationToken ct) => Task.Run(() => Read(ct), ct);

    internal ClientLogTailReport Read(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        long began = _clock.GetTimestamp();
        var cutoff = now - MaximumAge;
        var found = new List<Found>();
        int folders = 0, unreadable = 0, inspected = 0;
        bool limited = false;
        foreach (var root in _roots)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root.Path) || !RobloxClientFolders.IsBelowAnchor(root.Path, _anchor)) continue;
            try
            {
                foreach (string path in Directory.EnumerateFiles(root.Path, "*.log", SearchOption.TopDirectoryOnly))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++inspected > MaximumEntries || _clock.GetElapsedTime(began) >= Deadline) { limited = true; break; }
                    if (!IsPlayerLog(path)) continue;
                    if (Describe(path, cutoff, root.Variant == ClientLogVariant.Uwp) is { } log) found.Add(log);
                }
                folders++;
            }
            catch (IOException) { unreadable++; }
            catch (UnauthorizedAccessException) { unreadable++; }
            if (limited) break;
        }
        found.Sort(static (left, right) => right.ModifiedUtc.CompareTo(left.ModifiedUtc));
        var logs = new List<ClientLogTail>(Math.Min(found.Count, MaximumLogs));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(TailBytes);
        try
        {
            foreach (var log in found)
            {
                if (logs.Count >= MaximumLogs) break;
                ct.ThrowIfCancellationRequested();
                if (_clock.GetElapsedTime(began) >= Deadline) { limited = true; break; }
                if (ReadTail(log, logs.Count + 1, buffer, now, ct) is { } tail) logs.Add(tail);
            }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); ArrayPool<byte>.Shared.Return(buffer); }
        return new(now, folders, unreadable, found.Count, limited, logs.AsReadOnly());
    }

    /// <summary>A <c>*_Player_*.log</c> file that is not the crash handler's. Studio logs and anything else are never read.</summary>
    internal static bool IsPlayerLog(string path)
        => RobloxClientLogCleanup.IsPlayerLog(path) && !Path.GetFileName(path).Contains("_CrashHandler_", StringComparison.OrdinalIgnoreCase);

    private static Found? Describe(string path, DateTimeOffset cutoff, bool storePackage)
    {
        try
        {
            var info = new FileInfo(path);
            if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return null;
            var written = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            return written < cutoff ? null : new(info.FullName, storePackage, written, info.Length);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Null when the file is gone; otherwise a tail whose Outcome says whether its bytes could be read.</summary>
    private static ClientLogTail? ReadTail(Found log, int order, byte[] buffer, DateTimeOffset readUtc, CancellationToken ct)
    {
        long length;
        int read = 0;
        bool partialStart;
        try
        {
            // Sharing read, write and delete: a running client keeps writing and Roblox may still rotate the file.
            using var stream = new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 0, FileOptions.SequentialScan);
            IncrementalClientLogReader.ValidatePhysicalPath(stream.SafeFileHandle, log.Path);
            length = stream.Length;
            long start = Math.Max(0, length - TailBytes);
            partialStart = start > 0;
            int want = (int)Math.Min(length - start, TailBytes);
            stream.Position = start;
            while (read < want)
            {
                ct.ThrowIfCancellationRequested();
                int next = stream.Read(buffer, read, want - read);
                if (next == 0) break;
                read += next;
            }
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (UnauthorizedAccessException) { return Unread(log, order, ClientLogTailOutcome.AccessDenied); }
        catch (IOException) { return Unread(log, order, ClientLogTailOutcome.ReadFailed); }
        catch (Win32Exception) { return Unread(log, order, ClientLogTailOutcome.ReadFailed); }
        try
        {
            var entries = new List<ClientLogTailEntry>();
            int lines = 0, dropped = 0, cursor = 0;
            // A tail that starts inside a line skips that partial line; a partial last line (still being written) waits for nobody.
            if (partialStart) { int newline = Array.IndexOf(buffer, (byte)'\n', 0, read); cursor = newline < 0 ? read : newline + 1; }
            for (int index = cursor; index < read; index++)
            {
                if (buffer[index] != (byte)'\n') continue;
                int end = index > cursor && buffer[index - 1] == (byte)'\r' ? index - 1 : index;
                lines++;
                if (end - cursor <= MaximumLineBytes && Recognize(buffer.AsSpan(cursor, end - cursor), readUtc) is { } entry)
                {
                    if (entries.Count == MaximumEntriesPerLog) { entries.RemoveAt(0); dropped++; }
                    entries.Add(entry);
                }
                cursor = index + 1;
            }
            return new(order, log.StorePackage, log.ModifiedUtc, length, read, lines, dropped, ClientLogTailOutcome.Read, entries.AsReadOnly());
        }
        finally { CryptographicOperations.ZeroMemory(buffer.AsSpan(0, read)); }
    }

    private static ClientLogTail Unread(Found log, int order, ClientLogTailOutcome outcome)
        => new(order, log.StorePackage, log.ModifiedUtc, log.Length, 0, 0, 0, outcome, Array.Empty<ClientLogTailEntry>());

    private static ClientLogTailEntry? Recognize(ReadOnlySpan<byte> bytes, DateTimeOffset readUtc)
    {
        string line;
        try { line = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
        return Recognize(line, readUtc);
    }

    /// <summary>
    /// The allowlist: the correlator's own parser decides whether the line is a recognised shape, and only the fields the support
    /// report may carry are copied out of its observation (time, kind, place, server, disconnect code). The player ID is dropped.
    /// </summary>
    internal static ClientLogTailEntry? Recognize(string line, DateTimeOffset readUtc)
    {
        if (IncrementalClientLogReader.Parse(line, default, 0, 0, readUtc) is not { LoggedUtc: { } logged } observation) return null;
        return new(logged, observation.Fact, observation.PlaceId, observation.JobId, observation.DisconnectReason);
    }
}
