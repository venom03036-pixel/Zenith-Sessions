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
    private FileStream? _lease;
    public string Path { get; }
    private SignInProfileDirectory(string path, FileStream? lease = null)
    {
        Path = path; _lease = lease;
        if (lease is not null) HeldLeases.AddOrUpdate(LeaseRoot(path), 1, static (_, held) => held + 1);
    }

    /// <summary>
    /// True while an owner in this process still holds a sign-in folder under <paramref name="dataRoot"/>
    /// (an open or closing sign-in, or a retained cleanup), so <see cref="Create"/> would report it in use.
    /// </summary>
    public static bool HasLiveOwner(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return HeldLeases.TryGetValue(BrowserRootOf(dataRoot), out int held) && held > 0;
    }

    /// <summary>
    /// True while the last admission or sweep under <paramref name="dataRoot"/> was stopped by leftover data it could not
    /// recover (unrecognized, redirected or unremovable), so the next <see cref="Create"/> would refuse as well. Memory
    /// only, like <see cref="HasLiveOwner"/>; a clean admission or sweep clears it, and every start begins with a sweep.
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
                    if (++prior > RecoveryBudget) throw new IOException(NeedsReview);
                    RecoverLeftover(old, ct);
                }
            }
            // What stopped admission stays on disk until someone reviews it. Remember it so the handoff checks refuse
            // before reserving a grant; a folder another owner holds is only busy and a cancel changes nothing.
            catch (Exception ex) when (ex is (IOException or UnauthorizedAccessException) and not SignInDirectoryInUseException)
            { ReviewPending.TryAdd(browserRoot, 0); throw; }
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
    /// unrecognized, redirected or cannot be removed is left untouched and counted as kept. A redirected storage root
    /// is refused as a whole with an <see cref="IOException"/>. Either way, data the next admission would also refuse
    /// is reported by <see cref="LeftoverNeedsReview"/>. Meant for startup, under the single-instance lock.
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
            AssertAncestors(browserRoot);
            int removed = 0, kept = 0; bool review = false;
            foreach (string old in Directory.EnumerateDirectories(browserRoot))
            {
                ct.ThrowIfCancellationRequested();
                // Same per-run budget as admission: later folders are counted, not opened, and wait for the next run.
                if (removed + kept >= RecoveryBudget) { kept++; continue; }
                try { RecoverLeftover(old, ct); removed++; }
                // A held folder is busy and frees itself; anything else waits for review.
                catch (SignInDirectoryInUseException) { kept++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { kept++; review = true; }
            }
            // Admission also refuses when more folders remain than one admission may open.
            if (review || kept > RecoveryBudget) ReviewPending.TryAdd(browserRoot, 0); else ReviewPending.TryRemove(browserRoot, out _);
            return new(removed, kept);
        }
        // A redirected or unreadable root refuses every admission too.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ReviewPending.TryAdd(browserRoot, 0); throw; }
        finally { Admission.Release(); }
    }

    // Only exact, marked folders created by this component may be recovered. A held lease
    // proves another sign-in still owns its directory; do not inspect or remove its contents.
    // A folder holding only this component's marker and lock (or nothing at all) was left by an
    // interrupted create or remove: it has no browser data to review, so it is recovered even
    // when its marker is incomplete or its lock is missing.
    private static void RecoverLeftover(string old, CancellationToken ct)
    {
        if (!OwnedName(System.IO.Path.GetFileName(old))) throw new IOException(NeedsReview);
        if ((File.GetAttributes(old) & FileAttributes.ReparsePoint) != 0) throw new IOException("Private sign-in cleanup found redirected storage.");
        bool ownershipFilesOnly = HoldsOnlyOwnershipFiles(old);
        if (!ownershipFilesOnly) AssertMarker(old);
        // FileShare.None still rejects a live owner; a missing lock is recreated and held until the folder is gone.
        var recovered = new SignInProfileDirectory(old, OpenLease(old));
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
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Sign-in cleanup found a redirected directory; its files were preserved.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(current))
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > 30000) throw new IOException("Sign-in cleanup exceeded its file budget; its directory was preserved.");
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Sign-in cleanup found redirected data; its files were preserved.");
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
    private void DropLease()
    {
        if (Interlocked.Exchange(ref _lease, null) is not { } lease) return;
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
    private void DeleteOwnershipFiles()
    {
        DropLease();
        File.Delete(System.IO.Path.Combine(Path, OwnerLock));
        File.Delete(System.IO.Path.Combine(Path, Marker));
        Directory.Delete(Path, false);
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
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Private sign-in cleanup found redirected storage.");
        string marker = System.IO.Path.Combine(path, Marker);
        var info = new FileInfo(marker);
        if (!info.Exists || info.Length > 100 || (info.Attributes & FileAttributes.ReparsePoint) != 0 || File.ReadAllText(marker) != MarkerContents)
            throw new IOException("Private sign-in cleanup found unrecognized data; it was preserved for review.");
    }
}

/// <summary>What a startup sweep did: leftover sign-in folders removed, and folders kept untouched for review or a later run.</summary>
public readonly record struct SignInSweepResult(int Removed, int Kept);

/// <summary>Another private browser (an open sign-in, or one still closing) holds its sign-in folder. Nothing was opened or removed.</summary>
public sealed class SignInDirectoryInUseException() : IOException("Another private browser still owns its sign-in folder. Wait for it to close, then try again.");
