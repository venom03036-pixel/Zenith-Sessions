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
        return HeldLeases.TryGetValue(System.IO.Path.Combine(System.IO.Path.GetFullPath(dataRoot), "signin-browser-data"), out int held) && held > 0;
    }

    public static SignInProfileDirectory Create(string dataRoot, Guid profileId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        if (profileId == Guid.Empty) throw new ArgumentException("A saved account profile is required.");
        ct.ThrowIfCancellationRequested();
        string root = System.IO.Path.GetFullPath(dataRoot);
        if (root.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Browser sign-in needs local private storage.");
        // Reject links in existing ancestors before creating anything below the app's data root.
        AssertAncestors(root);
        string browserRoot = System.IO.Path.Combine(root, "signin-browser-data");
        var directory = Directory.CreateDirectory(browserRoot);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Browser sign-in storage is redirected.");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
        // Only exact, marked folders created by this component may be recovered. A held lease
        // proves another sign-in still owns its directory; do not inspect or remove its contents.
        // A folder holding only this component's marker and lock (or nothing at all) was left by an
        // interrupted create or remove: it has no browser data to review, so it is recovered even
        // when its marker is incomplete or its lock is missing.
        int prior = 0;
        foreach (string old in Directory.EnumerateDirectories(browserRoot))
        {
            ct.ThrowIfCancellationRequested();
            if (++prior > 8 || !OwnedName(System.IO.Path.GetFileName(old))) throw new IOException("Private sign-in cleanup needs review before another browser can open.");
            if ((File.GetAttributes(old) & FileAttributes.ReparsePoint) != 0) throw new IOException("Private sign-in cleanup found redirected storage.");
            bool ownershipFilesOnly = HoldsOnlyOwnershipFiles(old);
            if (!ownershipFilesOnly) AssertMarker(old);
            // FileShare.None still rejects a live owner; a missing lock is recreated and held until the folder is gone.
            var recovered = new SignInProfileDirectory(old, OpenLease(old));
            try { if (ownershipFilesOnly) recovered.DeleteOwnershipFiles(); else recovered.Remove(ct); }
            finally { recovered.ReleaseOwnership(); }
        }
        string path = System.IO.Path.Combine(browserRoot, profileId.ToString("N") + "-" + Guid.NewGuid().ToString("N"));
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(path);
        File.WriteAllText(System.IO.Path.Combine(path, Marker), MarkerContents);
        return new(path, new FileStream(System.IO.Path.Combine(path, OwnerLock), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None));
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

/// <summary>Another private browser (an open sign-in, or one still closing) holds its sign-in folder. Nothing was opened or removed.</summary>
public sealed class SignInDirectoryInUseException() : IOException("Another private browser still owns its sign-in folder. Wait for it to close, then try again.");
