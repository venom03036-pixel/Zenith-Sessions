using System.Security.AccessControl;
using System.Security.Principal;

namespace Zenith.Sessions.Windows;

/// <summary>Owns only a newly created, private, disposable browser directory. Never opens another browser's data.</summary>
public sealed class SignInProfileDirectory
{
    private const string Marker = ".zenith-private-signin";
    private const string MarkerContents = "Zenith Sessions private sign-in profile v1";
    private FileStream? _lease;
    public string Path { get; }
    private SignInProfileDirectory(string path, FileStream? lease = null) { Path = path; _lease = lease; }

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
        int prior = 0;
        foreach (string old in Directory.EnumerateDirectories(browserRoot))
        {
            ct.ThrowIfCancellationRequested();
            if (++prior > 8 || !OwnedName(System.IO.Path.GetFileName(old))) throw new IOException("Private sign-in cleanup needs review before another browser can open.");
            AssertMarker(old);
            using (new FileStream(System.IO.Path.Combine(old, "owner.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            new SignInProfileDirectory(old).Remove(ct);
        }
        string path = System.IO.Path.Combine(browserRoot, profileId.ToString("N") + "-" + Guid.NewGuid().ToString("N"));
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(path);
        File.WriteAllText(System.IO.Path.Combine(path, Marker), MarkerContents);
        return new(path, new FileStream(System.IO.Path.Combine(path, "owner.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None));
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
        _lease?.Dispose(); _lease = null;
        Directory.Delete(Path, true);
    }
    public void ReleaseOwnership() { _lease?.Dispose(); _lease = null; }
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
