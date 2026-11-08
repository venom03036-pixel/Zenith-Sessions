using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Zenith.Sessions.Windows;

/// <summary>Owns only a newly created, private, disposable browser directory. Never opens another browser's data.</summary>
public sealed class SignInProfileDirectory
{
    private const string Marker = ".zenith-private-signin";
    private const string MarkerContents = "Zenith Sessions private sign-in profile v1";
    private const string OwnerLock = "owner.lock";
    // Leases this process holds, per browser-data root. Reading it opens no lock file, so a check
    // can never disturb a folder whose owner is still removing it.
    private static readonly ConcurrentDictionary<string, int> HeldLeases = new(StringComparer.OrdinalIgnoreCase);
    // Serializes admission and the startup sweep in this process. A folder Create has made but not yet marked and
    // locked holds only ownership files, which recovery deletes, so a sweep must never see it mid-creation.
    private static readonly SemaphoreSlim Admission = new(1, 1);
    // Leftover folders one admission or sweep may open; each removal is itself bounded by Remove's file budget.
    private const int RecoveryBudget = 8;
    private const string NeedsReview = "Private sign-in cleanup needs review before another browser can open.";
    // Browser-data roots whose last admission or sweep kept leftover data for review. Read before any grant is
    // reserved, including from the UI thread, so it is memory only; the value is unused.
    private static readonly ConcurrentDictionary<string, byte> ReviewPending = new(StringComparer.OrdinalIgnoreCase);
    // Leases an owner keeps after its cleanup failed, per browser-data root and per folder: a subset of HeldLeases. The
    // owner holds them until it is disposed, when Zenith closes, so nothing in this session frees them and every admission
    // under that root is refused until then. Like HeldLeases, reading them opens no lock file.
    private static readonly ConcurrentDictionary<string, int> KeptLeases = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> KeptFolders = new(StringComparer.OrdinalIgnoreCase);
    // An owner must close its lock file before it can delete it, so its emptied folder looks like a leftover until the
    // folder is gone. That release and a recovery's inspection and lease run under this gate, so a recovery finds the
    // folder either still held or already removed, never in between. Held only for a few file-metadata calls.
    private static readonly object ReleaseGate = new();
    // Guards _lease and _kept together, so a folder is never marked kept after its lease is gone.
    private readonly object _leaseGate = new();
    private FileStream? _lease;
    private bool _kept;
    public string Path { get; }
    private SignInProfileDirectory(string path, FileStream? lease = null)
    {
        Path = path; _lease = lease;
        if (lease is not null) HeldLeases.AddOrUpdate(LeaseRoot(path), 1, static (_, held) => held + 1);
    }

    /// <summary>
    /// True while an owner in this process still holds a sign-in folder under <paramref name="dataRoot"/> that it may yet
    /// release (an open sign-in, or one still closing), so <see cref="Create"/> would report it in use for now. A folder
    /// kept after a failed cleanup is not counted: see <see cref="HasFolderKeptAfterFailedCleanup"/>.
    /// </summary>
    public static bool HasLiveOwner(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        string browserRoot = BrowserRootOf(dataRoot);
        if (!HeldLeases.TryGetValue(browserRoot, out int held)) return false;
        return held - (KeptLeases.TryGetValue(browserRoot, out int kept) ? kept : 0) > 0;
    }

    /// <summary>
    /// True while an owner in this process keeps a sign-in folder under <paramref name="dataRoot"/> after its cleanup
    /// failed (<see cref="KeepAfterFailedCleanup"/>). That owner releases it only when it is disposed, when Zenith closes,
    /// so every <see cref="Create"/> under this root is refused until then and retrying in this session cannot help.
    /// Memory only, like <see cref="HasLiveOwner"/>; nothing is remembered once the folder is released.
    /// </summary>
    public static bool HasFolderKeptAfterFailedCleanup(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return KeptLeases.TryGetValue(BrowserRootOf(dataRoot), out int kept) && kept > 0;
    }

    /// <summary>
    /// True while the last admission or sweep under <paramref name="dataRoot"/> kept leftover data it must not open or
    /// remove (see <see cref="SignInDirectoryNeedsReviewException"/>), so the next <see cref="Create"/> would refuse as
    /// well. Other storage failures are not remembered: they say nothing about leftover data and may clear on retry.
    /// Memory only, like <see cref="HasLiveOwner"/>; a clean admission or sweep clears it, and every start begins with a sweep.
    /// </summary>
    public static bool LeftoverNeedsReview(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return ReviewPending.ContainsKey(BrowserRootOf(dataRoot));
    }

    public static SignInProfileDirectory Create(string dataRoot, Guid profileId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        if (profileId == Guid.Empty) throw new ArgumentException("A saved account profile is required.");
        ct.ThrowIfCancellationRequested();
        string root = System.IO.Path.GetFullPath(dataRoot);
        if (root.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Browser sign-in needs local private storage.");
        Admission.Wait(ct);
        try
        {
            string browserRoot = System.IO.Path.Combine(root, "signin-browser-data");
            try
            {
                // Reject links in existing ancestors before creating anything below the app's data root.
                AssertAncestors(root);
                var directory = Directory.CreateDirectory(browserRoot);
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Browser sign-in storage is redirected.");
                var acl = new DirectorySecurity();
                acl.SetAccessRuleProtection(true, false);
                var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                    acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
                directory.SetAccessControl(acl);
                // Admission needs a clean root, so the first leftover it cannot recover stops it.
                int prior = 0;
                foreach (string old in Directory.EnumerateDirectories(browserRoot))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++prior > RecoveryBudget) throw new SignInDirectoryNeedsReviewException(NeedsReview);
                    RecoverLeftover(old, ct);
                }
            }
            // Leftover data that stopped admission stays on disk until someone reviews it. Remember it so the handoff
            // checks refuse before reserving a grant. Nothing else is remembered: a folder another owner holds is only
            // busy, a cancel changes nothing, and remote, redirected, unwritable or full storage, or a file another
            // program still has open, is a storage problem the next attempt may not have.
            catch (SignInDirectoryNeedsReviewException) { ReviewPending.TryAdd(browserRoot, 0); throw; }
            ReviewPending.TryRemove(browserRoot, out _);
            string path = System.IO.Path.Combine(browserRoot, profileId.ToString("N") + "-" + Guid.NewGuid().ToString("N"));
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(path);
            File.WriteAllText(System.IO.Path.Combine(path, Marker), MarkerContents);
            return new(path, new FileStream(System.IO.Path.Combine(path, OwnerLock), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None));
        }
        finally { Admission.Release(); }
    }

    /// <summary>
    /// Removes leftover sign-in folders under <paramref name="dataRoot"/> (left by a crash or an interrupted cleanup)
    /// that no browser holds, with the same name, marker, lease and redirection checks as <see cref="Create"/>, and
    /// creates nothing. Unlike admission it keeps going past a folder it must preserve: a folder that is held,
    /// unrecognized, redirected or cannot be removed is left untouched and counted as kept. Only leftover data that needs
    /// review, which the next admission would also refuse, is reported by <see cref="LeftoverNeedsReview"/>. A redirected
    /// or unreadable storage root is refused as a whole with an <see cref="IOException"/>; like every other storage
    /// failure it is not remembered for review. Meant for startup, under the single-instance lock.
    /// </summary>
    public static SignInSweepResult SweepOwned(string dataRoot, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ct.ThrowIfCancellationRequested();
        string root = System.IO.Path.GetFullPath(dataRoot);
        // Create never makes sign-in folders on remote storage, so there is nothing of ours to sweep there.
        if (root.StartsWith(@"\\", StringComparison.Ordinal)) return default;
        string browserRoot = System.IO.Path.Combine(root, "signin-browser-data");
        Admission.Wait(ct);
        try
        {
            if (!Directory.Exists(browserRoot)) { ReviewPending.TryRemove(browserRoot, out _); return default; }
            // A redirected or unreadable root is a storage problem, not leftover data, so it is not remembered for review.
            AssertAncestors(browserRoot);
            int removed = 0, kept = 0; bool review = false;
            foreach (string old in Directory.EnumerateDirectories(browserRoot))
            {
                ct.ThrowIfCancellationRequested();
                // Same per-run budget as admission: later folders are counted, not opened, and wait for the next run.
                if (removed + kept >= RecoveryBudget) { kept++; continue; }
                try { RecoverLeftover(old, ct); removed++; }
                // A held folder is busy and frees itself, and a storage error (such as a file another program still has
                // open) may clear on the next attempt; only data that needs review waits for someone to look at it.
                catch (SignInDirectoryInUseException) { kept++; }
                catch (SignInDirectoryNeedsReviewException) { kept++; review = true; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { kept++; }
            }
            // Admission also refuses when more folders remain than one admission may open.
            if (review || kept > RecoveryBudget) ReviewPending.TryAdd(browserRoot, 0); else ReviewPending.TryRemove(browserRoot, out _);
            return new(removed, kept);
        }
        finally { Admission.Release(); }
    }

    // Only exact, marked folders created by this component may be recovered. A held lease
    // proves another sign-in still owns its directory; do not inspect or remove its contents.
    // A folder holding only this component's marker and lock (or nothing at all) was left by an
    // interrupted create or remove: it has no browser data to review, so it is recovered even
    // when its marker is incomplete or its lock is missing.
    private static void RecoverLeftover(string old, CancellationToken ct)
    {
        bool ownershipFilesOnly; SignInProfileDirectory recovered;
        lock (ReleaseGate)
        {
            // Its owner finished removing it after it was listed: there is nothing left to recover.
            if (!Directory.Exists(old)) return;
            // A folder another owner here kept after its cleanup failed stays held until that owner is disposed: it is not
            // inspected, and the refusal says so instead of suggesting the owner will soon let go.
            if (KeptFolders.ContainsKey(old)) throw new SignInDirectoryInUseException(keptAfterFailedCleanup: true);
            if (!OwnedName(System.IO.Path.GetFileName(old))) throw new SignInDirectoryNeedsReviewException(NeedsReview);
            if ((File.GetAttributes(old) & FileAttributes.ReparsePoint) != 0) throw new SignInDirectoryNeedsReviewException("Private sign-in cleanup found redirected storage.");
            ownershipFilesOnly = HoldsOnlyOwnershipFiles(old);
            if (!ownershipFilesOnly) AssertMarker(old);
            // FileShare.None still rejects a live owner; a missing lock is recreated and held until the folder is gone.
            recovered = new SignInProfileDirectory(old, OpenLease(old));
        }
        try { if (ownershipFilesOnly) recovered.DeleteOwnershipFiles(); else recovered.Remove(ct); }
        finally { recovered.ReleaseOwnership(); }
    }

    public void Remove(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(Path)) return;
        AssertAncestors(Path);
        AssertMarker(Path);
        // A bounded inspection prevents following a replacement junction or spending unlimited time
        // walking browser caches. Failure preserves this directory and is reported, never ignored.
        var pending = new Stack<string>(); pending.Push(Path); int entries = 0;
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string current = pending.Pop();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new SignInDirectoryNeedsReviewException("Sign-in cleanup found a redirected directory; its files were preserved.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(current))
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > 30000) throw new SignInDirectoryNeedsReviewException("Sign-in cleanup exceeded its file budget; its directory was preserved.");
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new SignInDirectoryNeedsReviewException("Sign-in cleanup found redirected data; its files were preserved.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
        ct.ThrowIfCancellationRequested();
        AssertAncestors(Path);
        // Delete browser data while the marker and lease stay in place. A recursive delete of the
        // whole folder keeps going after a locked file and removes the marker and lock before it
        // throws, which left a folder no later sign-in could recognize or recover.
        foreach (string entry in Directory.GetFileSystemEntries(Path))
        {
            if (IsOwnershipFile(entry)) continue;
            if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) Directory.Delete(entry, true);
            else File.Delete(entry);
        }
        DeleteOwnershipFiles();
    }
    public void ReleaseOwnership() => DropLease();
    // Test-only seam: runs after this owner closes its lock file and before it deletes its ownership files, the window
    // an admission's recovery can land in. Production never sets it.
    internal Action? LeaseReleasedForTest;

    /// <summary>
    /// Records that this folder's cleanup failed and that its owner keeps the folder and its lease until it releases
    /// ownership, which it does only when it is disposed. Other owners are then refused with
    /// <see cref="SignInDirectoryInUseException.KeptAfterFailedCleanup"/> and see <see cref="HasFolderKeptAfterFailedCleanup"/>,
    /// so they report a cleanup failure that needs a restart rather than an owner that is still busy. Repeated calls,
    /// and calls after the lease is gone (a later removal that succeeded), change nothing.
    /// </summary>
    public void KeepAfterFailedCleanup()
    {
        lock (_leaseGate)
        {
            if (_lease is null || _kept) return;
            _kept = true;
            KeptFolders.TryAdd(Path, 0);
            KeptLeases.AddOrUpdate(LeaseRoot(Path), 1, static (_, kept) => kept + 1);
        }
    }
    private void DropLease()
    {
        FileStream? lease; bool kept;
        lock (_leaseGate) { lease = _lease; _lease = null; kept = _kept; _kept = false; }
        if (lease is null) return;
        // Forgotten before the lock file closes, so a folder being released is never reported as kept.
        if (kept) { KeptFolders.TryRemove(Path, out _); KeptLeases.AddOrUpdate(LeaseRoot(Path), 0, static (_, count) => count - 1); }
        try { lease.Dispose(); }
        finally { HeldLeases.AddOrUpdate(LeaseRoot(Path), 0, static (_, held) => held - 1); }
    }
    private static string LeaseRoot(string path) => System.IO.Path.GetDirectoryName(path)!;
    private static string BrowserRootOf(string dataRoot) => System.IO.Path.Combine(System.IO.Path.GetFullPath(dataRoot), "signin-browser-data");
    private static FileStream OpenLease(string path)
    {
        try { return new FileStream(System.IO.Path.Combine(path, OwnerLock), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        // A sharing violation means another browser owner still holds this folder, not that storage or WebView2 is broken.
        catch (IOException ex) when (ex.HResult == SharingViolation) { throw new SignInDirectoryInUseException(); }
    }
    private const int SharingViolation = unchecked((int)0x80070020);
    // The lock goes before the marker, so an interruption here leaves a marked or empty folder; Create recovers both.
    // Under ReleaseGate, so no recovery takes the folder between its lease closing and the folder being deleted, which
    // made these deletes fail and latched a cleanup failure although nothing was left.
    private void DeleteOwnershipFiles()
    {
        lock (ReleaseGate)
        {
            DropLease();
            LeaseReleasedForTest?.Invoke();
            File.Delete(System.IO.Path.Combine(Path, OwnerLock));
            File.Delete(System.IO.Path.Combine(Path, Marker));
            Directory.Delete(Path, false);
        }
    }
    private static bool IsOwnershipFile(string entry)
    {
        string name = System.IO.Path.GetFileName(entry);
        return name.Equals(Marker, StringComparison.OrdinalIgnoreCase) || name.Equals(OwnerLock, StringComparison.OrdinalIgnoreCase);
    }
    private static bool HoldsOnlyOwnershipFiles(string path)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
            if (!IsOwnershipFile(entry) || (File.GetAttributes(entry) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
        return true;
    }
    private static void AssertAncestors(string path)
    {
        for (DirectoryInfo? parent = new(path); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Browser sign-in storage cannot use redirected directories.");
    }
    private static bool OwnedName(string name) => name.Length == 65 && name[32] == '-'
        && Guid.TryParseExact(name[..32], "N", out _) && Guid.TryParseExact(name[33..], "N", out _);
    private static void AssertMarker(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new SignInDirectoryNeedsReviewException("Private sign-in cleanup found redirected storage.");
        string marker = System.IO.Path.Combine(path, Marker);
        var info = new FileInfo(marker);
        if (!info.Exists || info.Length > 100 || (info.Attributes & FileAttributes.ReparsePoint) != 0 || File.ReadAllText(marker) != MarkerContents)
            throw new SignInDirectoryNeedsReviewException("Private sign-in cleanup found unrecognized data; it was preserved for review.");
    }
}

/// <summary>What a startup sweep did: leftover sign-in folders removed, and folders kept untouched for review or a later run.</summary>
public readonly record struct SignInSweepResult(int Removed, int Kept);

/// <summary>
/// Another private browser holds its sign-in folder. Nothing was opened or removed. Usually that owner is open or still
/// closing and lets go by itself. With <see cref="KeptAfterFailedCleanup"/>, its cleanup failed and it keeps the folder
/// until it is disposed, when Zenith closes, so no retry in this session can succeed. The message is fixed text: no path.
/// </summary>
public sealed class SignInDirectoryInUseException(bool keptAfterFailedCleanup = false) : IOException(keptAfterFailedCleanup
    ? "A private browser's cleanup failed, and its sign-in folder is kept until Zenith Sessions closes. Close Zenith Sessions, then try again."
    : "Another private browser still owns its sign-in folder. Wait for it to close, then try again.")
{
    /// <summary>The holder's cleanup failed and it keeps the folder until it is disposed; see <see cref="SignInProfileDirectory.KeepAfterFailedCleanup"/>.</summary>
    public bool KeptAfterFailedCleanup { get; } = keptAfterFailedCleanup;
}

/// <summary>
/// Leftover sign-in data that admission or cleanup must not open or remove: a folder that is not one of this component's,
/// one without a valid marker, one that is redirected or holds redirected data or more entries than cleanup may inspect,
/// or more leftover folders than one admission may open. It stays on disk untouched until someone reviews it, so retrying
/// alone cannot clear it. Every other storage failure (remote or redirected storage, permissions, a full disk, a file
/// another program still has open) stays a plain <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.
/// The message is fixed text: no path.
/// </summary>
public sealed class SignInDirectoryNeedsReviewException(string message) : IOException(message);
