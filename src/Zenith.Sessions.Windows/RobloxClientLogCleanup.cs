using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>One Roblox Player log a check found removable: its path (kept only so the delete touches exactly this file), size and last write.</summary>
public sealed record ClientLogCleanupFile(string Path, long Length, DateTimeOffset ModifiedUtc)
{
    public override string ToString() => $"ClientLogCleanupFile {{ Path = [local path], Length = {Length}, ModifiedUtc = {ModifiedUtc:O} }}";
}

/// <summary>
/// What one check found: the Player logs older than <see cref="OlderThan"/> that may be deleted, and how many were kept because
/// they are newer (<see cref="KeptRecent"/>), because a running client still holds them or Zenith is watching them for session
/// evidence (<see cref="KeptInUse"/>), or because they could not be checked (<see cref="KeptUnchecked"/>: a reparse point, a
/// denied or failed open). <see cref="FoldersChecked"/> is 0 when no documented Roblox logs folder exists for this user.
/// <see cref="BudgetLimited"/> says the check stopped at its file, time or result limit, so running it again may find more.
/// </summary>
public sealed record ClientLogCleanupSurvey(TimeSpan OlderThan, DateTimeOffset SurveyedUtc, IReadOnlyList<ClientLogCleanupFile> Removable,
    int KeptRecent, int KeptInUse, int KeptUnchecked, int FoldersChecked, bool BudgetLimited)
{
    public int RemovableCount => Removable.Count;
    public long RemovableBytes { get { long total = 0; foreach (var file in Removable) total += file.Length; return total; } }
    /// <summary>The cutoff in whole days, as the user entered it.</summary>
    public int Days => (int)Math.Round(OlderThan.TotalDays);
}

/// <summary>
/// What a confirmed delete did with the checked files: deleted, kept because a client holds them or Zenith watches them
/// (<see cref="KeptInUse"/>), kept because they changed or were already gone since the check (<see cref="KeptChanged"/>), failed
/// (denied or another error), and, when <see cref="Stopped"/> by Panic, Lock, Cancel or exit, not attempted at all.
/// </summary>
public sealed record ClientLogCleanupResult(int Deleted, long DeletedBytes, int KeptInUse, int KeptChanged, int Failed, int NotAttempted, bool Stopped);

/// <summary>Check first, then delete exactly what was checked. The shell owns the calls; tests inject a fake.</summary>
public interface IClientLogCleanup
{
    /// <summary>Counts the removable Player logs and what is kept. Throws <see cref="ArgumentOutOfRangeException"/> for a cutoff under a day or over a year, and <see cref="OperationCanceledException"/> when stopped.</summary>
    Task<ClientLogCleanupSurvey> SurveyAsync(TimeSpan olderThan, CancellationToken ct);
    /// <summary>Deletes the files <paramref name="survey"/> listed that still qualify. A stop returns the partial result instead of throwing, so the report says what happened.</summary>
    Task<ClientLogCleanupResult> DeleteAsync(ClientLogCleanupSurvey survey, CancellationToken ct);
}

/// <summary>
/// On-demand cleanup of old Roblox Player logs in the current user's documented Roblox log folders (the same roots the log
/// probe watches), and nowhere else: each root must sit below the trusted anchor (LocalApplicationData) with no reparse point
/// on the way, only files directly inside it are considered, only <c>*.log</c> files whose name contains <c>_Player_</c>, and a
/// cutoff shorter than one day is refused because the log correlator still discovers logs written within a day.
/// A file is kept, never deleted, when it is newer than the cutoff, when the correlator is watching it (bound to a client or a
/// discovered candidate), or when another process holds it open: the check opens each candidate sharing only deletion, which
/// Windows refuses while a client has the file open for reading or writing, and the delete uses the same open with
/// delete-on-close, so there is no window in which a running client's log could be taken. The delete also re-reads each file
/// and keeps one whose size or last write changed since the check. Nothing is read from the logs, no path or name is kept in
/// diagnostics, nothing of Roblox's settings is touched, and nothing runs on its own: every call is a user action.
/// Rule 18: one check inspects at most <see cref="MaximumEntries"/> directory entries within <see cref="SurveyDeadline"/> and
/// lists at most <see cref="MaximumRemovable"/> files, each costing one metadata read and one open; a delete costs one open per
/// listed file and checks cancellation before each; no thread, timer or poller is created beyond the one pool task per call.
/// </summary>
public sealed class RobloxClientLogCleanup : IClientLogCleanup
{
    public const int MinimumDays = 1, MaximumDays = 365;
    /// <summary>Files one check may list; a larger folder is cleaned in several rounds, each shown and confirmed on its own.</summary>
    public const int MaximumRemovable = 2048;
    internal const int MaximumEntries = 8192;
    internal static readonly TimeSpan SurveyDeadline = TimeSpan.FromSeconds(10);
    private const int SharingViolation = unchecked((int)0x80070020), LockViolation = unchecked((int)0x80070021);
    private readonly (string Path, ClientLogVariant Variant)[] _roots;
    private readonly string _anchor;
    private readonly Func<IReadOnlyList<string>> _watchedLogs;
    private readonly TimeProvider _clock;

    /// <param name="watchedLogs">The logs the correlator currently watches, read at the start of every check and delete; never null, empty when it is not running.</param>
    public RobloxClientLogCleanup(Func<IReadOnlyList<string>> watchedLogs)
        : this(RobloxClientFolders.LogRoots(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), watchedLogs) { }

    internal RobloxClientLogCleanup(IReadOnlyList<(string Path, ClientLogVariant Variant)> roots, string trustedAnchor,
        Func<IReadOnlyList<string>> watchedLogs, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedAnchor);
        ArgumentNullException.ThrowIfNull(watchedLogs);
        _roots = roots.ToArray(); _anchor = trustedAnchor; _watchedLogs = watchedLogs; _clock = clock ?? TimeProvider.System;
    }

    public Task<ClientLogCleanupSurvey> SurveyAsync(TimeSpan olderThan, CancellationToken ct)
    {
        ValidateCutoff(olderThan);
        return Task.Run(() => Survey(olderThan, ct), ct);
    }

    public Task<ClientLogCleanupResult> DeleteAsync(ClientLogCleanupSurvey survey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(survey);
        ValidateCutoff(survey.OlderThan);
        // Not passed to Task.Run: a stop before the first file still produces a result that says nothing was touched.
        return Task.Run(() => Delete(survey, ct), CancellationToken.None);
    }

    private static void ValidateCutoff(TimeSpan olderThan)
    {
        if (olderThan < TimeSpan.FromDays(MinimumDays) || olderThan > TimeSpan.FromDays(MaximumDays))
            throw new ArgumentOutOfRangeException(nameof(olderThan), "The cutoff must be between one day and one year.");
    }

    internal ClientLogCleanupSurvey Survey(TimeSpan olderThan, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var cutoff = now - olderThan;
        long began = _clock.GetTimestamp();
        var watched = WatchedSet();
        var removable = new List<ClientLogCleanupFile>();
        int keptRecent = 0, keptInUse = 0, keptUnchecked = 0, folders = 0, inspected = 0;
        bool limited = false;
        foreach (var root in _roots)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root.Path) || !RobloxClientFolders.IsBelowAnchor(root.Path, _anchor)) continue;
            folders++;
            foreach (string path in Directory.EnumerateFiles(root.Path, "*.log", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                if (++inspected > MaximumEntries || removable.Count >= MaximumRemovable || _clock.GetElapsedTime(began) >= SurveyDeadline)
                { limited = true; break; }
                if (!IsPlayerLog(path)) continue;
                switch (Classify(path, cutoff, watched, out var file))
                {
                    case Verdict.Removable: removable.Add(file!); break;
                    case Verdict.Recent: keptRecent++; break;
                    case Verdict.InUse: keptInUse++; break;
                    default: keptUnchecked++; break;
                }
            }
            if (limited) break;
        }
        return new(olderThan, now, removable.AsReadOnly(), keptRecent, keptInUse, keptUnchecked, folders, limited);
    }

    private enum Verdict { Removable, Recent, InUse, Unchecked }

    private static Verdict Classify(string path, DateTimeOffset cutoff, HashSet<string> watched, out ClientLogCleanupFile? file)
    {
        file = null;
        try
        {
            var info = new FileInfo(path);
            if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return Verdict.Unchecked;
            var written = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (written > cutoff) return Verdict.Recent;
            if (watched.Contains(info.FullName)) return Verdict.InUse;
            // Sharing only deletion is refused while any other handle has the file open for reading or writing, as a running
            // client's own log is. Nothing is read.
            using (new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Delete, 0, FileOptions.None)) { }
            file = new(info.FullName, info.Length, written);
            return Verdict.Removable;
        }
        catch (IOException ex) when (ex.HResult is SharingViolation or LockViolation) { return Verdict.InUse; }
        catch (IOException) { return Verdict.Unchecked; }
        catch (UnauthorizedAccessException) { return Verdict.Unchecked; }
    }

    internal ClientLogCleanupResult Delete(ClientLogCleanupSurvey survey, CancellationToken ct)
    {
        var cutoff = _clock.GetUtcNow() - survey.OlderThan;
        var watched = WatchedSet();
        var safeFolders = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        int deleted = 0, keptInUse = 0, keptChanged = 0, failed = 0, index = 0;
        long bytes = 0;
        bool stopped = false;
        for (; index < survey.Removable.Count; index++)
        {
            if (ct.IsCancellationRequested) { stopped = true; break; }
            var listed = survey.Removable[index];
            string full;
            try { full = Path.GetFullPath(listed.Path); }
            catch (ArgumentException) { failed++; continue; }
            // Only a Player log directly inside a documented root that is still below the anchor is ever deleted, whatever the list says.
            string? folder = Path.GetDirectoryName(full);
            if (folder is null || !IsPlayerLog(full)) { failed++; continue; }
            if (!safeFolders.TryGetValue(folder, out bool safe))
                safeFolders[folder] = safe = _roots.Any(root => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Path)), folder, StringComparison.OrdinalIgnoreCase))
                    && RobloxClientFolders.IsBelowAnchor(folder, _anchor);
            if (!safe) { failed++; continue; }
            if (watched.Contains(full)) { keptInUse++; continue; }
            try
            {
                var info = new FileInfo(full);
                if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || info.Length != listed.Length
                    || new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) != listed.ModifiedUtc || info.LastWriteTimeUtc > cutoff)
                { keptChanged++; continue; }
                // One open does both: it is refused while a client holds the file, and otherwise the file is gone when the handle closes.
                using (new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Delete, 0, FileOptions.DeleteOnClose)) { }
                if (File.Exists(full)) { failed++; continue; }
                deleted++; bytes += listed.Length;
            }
            catch (IOException ex) when (ex.HResult is SharingViolation or LockViolation) { keptInUse++; }
            catch (FileNotFoundException) { keptChanged++; }
            catch (DirectoryNotFoundException) { keptChanged++; }
            catch (IOException) { failed++; }
            catch (UnauthorizedAccessException) { failed++; }
        }
        return new(deleted, bytes, keptInUse, keptChanged, failed, survey.Removable.Count - index, stopped);
    }

    private HashSet<string> WatchedSet()
    {
        var watched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in _watchedLogs() ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try { watched.Add(Path.GetFullPath(path)); }
            catch (ArgumentException) { }
        }
        return watched;
    }

    /// <summary>A <c>.log</c> file whose name says a Player client wrote it. Studio logs and anything else are never counted or deleted.</summary>
    internal static bool IsPlayerLog(string path)
    {
        string name = Path.GetFileName(path);
        return name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && name.Contains("_Player_", StringComparison.OrdinalIgnoreCase);
    }
}
