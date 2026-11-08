using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>Only current-user documented log roots; no recursive scan, credential folders or file writes.</summary>
internal sealed class WindowsClientLogProbe : IClientLogProbe
{
    private static readonly string[] ClientImageNames = ["RobloxPlayerBeta.exe", "RobloxPlayer.exe", "Roblox.exe"];
    private readonly (string Path, ClientLogVariant Variant)[] _roots;
    private readonly string _anchor;
    private readonly Dictionary<string, ClientLogCandidate> _known = new(StringComparer.OrdinalIgnoreCase);
    private IEnumerator<string>? _scan;
    private int _root;
    private readonly SecurityIdentifier? _user;

    internal WindowsClientLogProbe() : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) { }

    private WindowsClientLogProbe(string local) : this(RobloxClientFolders.LogRoots(local), local, CurrentUser()) { }

    /// <summary>
    /// Every root must sit below <paramref name="trustedAnchor"/> with no reparse point on the way; production passes
    /// LocalApplicationData. <paramref name="user"/> is the token owner a client process must have (null matches none).
    /// </summary>
    internal WindowsClientLogProbe(IReadOnlyList<(string Path, ClientLogVariant Variant)> roots, string trustedAnchor, SecurityIdentifier? user)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(trustedAnchor);
        _roots = roots.ToArray();
        _anchor = trustedAnchor;
        _user = user;
    }

    internal IReadOnlyList<(string Path, ClientLogVariant Variant)> Roots => Array.AsReadOnly(_roots);
    internal string TrustedAnchor => _anchor;

    private static SecurityIdentifier? CurrentUser()
    {
        using var current = WindowsIdentity.GetCurrent();
        return current.User;
    }

    public ClientLogCandidates Discover(int maximum, CancellationToken ct)
    {
        // The enumerator cursor survives bounded passes so a large historical folder cannot permanently hide later entries.
        long began = Stopwatch.GetTimestamp();
        int inspected = 0, completed = 0;
        bool denied = false;
        while (inspected < 256 && completed < _roots.Length && Stopwatch.GetElapsedTime(began) < TimeSpan.FromMilliseconds(50))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var root = _roots[_root];
                if (_scan is null)
                {
                    if (!Directory.Exists(root.Path) || !SafeDirectories(root.Path)) { NextRoot(); completed++; continue; }
                    _scan = Directory.EnumerateFiles(root.Path, "*.log", SearchOption.TopDirectoryOnly).GetEnumerator();
                }
                if (!_scan.MoveNext()) { NextRoot(); completed++; continue; }
                inspected++;
                string path = _scan.Current;
                var name = Path.GetFileName(path);
                if (!name.Contains("_Player_", StringComparison.OrdinalIgnoreCase)
                    || !name.EndsWith("_last.log", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("_CrashHandler_", StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(path);
                if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) continue;
                var written = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                if (DateTimeOffset.UtcNow - written > TimeSpan.FromDays(1)) continue;
                _known[path] = new(path, root.Variant, written);
                if (_known.Count > maximum)
                {
                    string oldest = _known.MinBy(pair => pair.Value.ModifiedUtc).Key;
                    _known.Remove(oldest);
                }
            }
            catch (UnauthorizedAccessException) { denied = true; NextRoot(); completed++; }
            catch (IOException) { denied = true; NextRoot(); completed++; }
        }
        // Metadata only; files are never opened here. Reading occurs only after a holder matches an eligible process instance.
        foreach (var pair in _known.ToArray())
            if (!File.Exists(pair.Key) || DateTimeOffset.UtcNow - pair.Value.ModifiedUtc > TimeSpan.FromDays(1)) _known.Remove(pair.Key);
        return new(Array.AsReadOnly(_known.Values.OrderByDescending(item => item.ModifiedUtc).ToArray()),
            inspected >= 256 || Stopwatch.GetElapsedTime(began) >= TimeSpan.FromMilliseconds(50), denied);
    }

    private bool SafeDirectories(string path) => RobloxClientFolders.IsBelowAnchor(path, _anchor);

    private void NextRoot() { _scan?.Dispose(); _scan = null; _root = (_root + 1) % _roots.Length; }

    public bool IsCurrentClient(SessionProcessKey key, ClientLogVariant variant)
    {
        if (!key.IsValid || _user is null) return false;
        using var process = Native.OpenProcess(0x1000, false, key.ProcessId); // QUERY_LIMITED_INFORMATION only.
        // No liveness test here: the correlator also requires the key in the current observed set and a Restart Manager holder.
        if (process.IsInvalid || !ProcessInstanceIdentity.MatchesStart(process, key.ProcessStartUtcTicks)) return false;
        var image = new StringBuilder(1024); uint length = (uint)image.Capacity;
        if (!Native.QueryFullProcessImageName(process, 0, image, ref length)) return false;
        if (!IsClientImage(image.ToString(), variant)) return false;
        if (!Native.OpenProcessToken(process, 0x8, out var token)) return false; // TOKEN_QUERY; never impersonates.
        using (token)
        {
            try { using var owner = new WindowsIdentity(token.DangerousGetHandle()); return owner.User is { } sid && _user.Equals(sid); }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }
    }

    /// <summary>The image must be an allowed client name, and a WindowsApps path component means the packaged (UWP) variant.</summary>
    internal static bool IsClientImage(string imagePath, ClientLogVariant variant)
    {
        string name = Path.GetFileName(imagePath);
        if (!ClientImageNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        return RobloxNativePlayerFiles.IsPackagedPath(imagePath) == (variant == ClientLogVariant.Uwp);
    }

    public ResourceHolderResult QueryHolders(string path, CancellationToken ct) => RestartManagerLogQuery.Query(path, ct);
    public void Dispose() { _scan?.Dispose(); _scan = null; }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint rights, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref uint length);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    }
}

/// <summary>
/// Query-only Restart Manager adapter. A holder is not a writer. Native calls have no hard cancellation ABI;
/// the two-second budget and cancellation are checked between calls. No shutdown/restart API is imported.
/// https://learn.microsoft.com/windows/win32/api/restartmanager/nf-restartmanager-rmgetlist
/// </summary>
internal static class RestartManagerLogQuery
{
    internal const int MaximumHolders = 256;
    internal const int MaximumAttempts = 3;
    internal static ResourceHolderResult Query(string path, CancellationToken ct)
    {
        long began = Stopwatch.GetTimestamp();
        ct.ThrowIfCancellationRequested();
        var key = new StringBuilder(33);
        uint error = RmStartSession(out uint session, 0, key);
        if (error != 0) return Failure(error);
        ResourceHolderResult result;
        uint end;
        try { result = WithinSession(session, path, began, ct); }
        finally { end = RmEndSession(session); }
        ct.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(began) >= TimeSpan.FromSeconds(2)) return new(SessionLogCorrelationStatus.BudgetExceeded, Array.Empty<ResourceHolder>());
        return end == 0 ? result : Failure(end);
    }

    private static ResourceHolderResult WithinSession(uint session, string path, long began, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        uint error = RmRegisterResources(session, 1, [path], 0, nint.Zero, 0, nint.Zero);
        if (error != 0) return Failure(error);
        uint capacity = 0;
        ProcessInfo[]? buffer = null;
        for (int attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(began) >= TimeSpan.FromSeconds(2)) return new(SessionLogCorrelationStatus.BudgetExceeded, Array.Empty<ResourceHolder>());
            uint filled = capacity;
            error = RmGetList(session, out uint needed, ref filled, buffer, out _);
            ct.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(began) >= TimeSpan.FromSeconds(2)) return new(SessionLogCorrelationStatus.BudgetExceeded, Array.Empty<ResourceHolder>());
            if (error == 234)
            {
                if (needed is 0 or > MaximumHolders) return new(SessionLogCorrelationStatus.BudgetExceeded, Array.Empty<ResourceHolder>());
                capacity = needed; buffer = new ProcessInfo[capacity]; continue;
            }
            if (error != 0) return Failure(error);
            if (filled > capacity || filled > MaximumHolders) return new(SessionLogCorrelationStatus.Unavailable, Array.Empty<ResourceHolder>());
            var holders = new List<ResourceHolder>((int)filled);
            for (int i = 0; i < filled; i++)
            {
                var native = buffer![i].Process;
                long ticks = ProcessInstanceIdentity.StartTicks(native.Created.Low, native.Created.High) ?? 0;
                if (native.Pid == 0 || native.Pid > int.MaxValue || ticks == 0) return new(SessionLogCorrelationStatus.Unavailable, Array.Empty<ResourceHolder>());
                holders.Add(new((int)native.Pid, ticks));
            }
            ct.ThrowIfCancellationRequested();
            return new(SessionLogCorrelationStatus.ResourceHolderObserved, holders.AsReadOnly());
        }
        return new(SessionLogCorrelationStatus.BudgetExceeded, Array.Empty<ResourceHolder>());
    }

    private static ResourceHolderResult Failure(uint code) => new(code == 5 ? SessionLogCorrelationStatus.AccessDenied
        : code is 2 or 3 ? SessionLogCorrelationStatus.Missing : SessionLogCorrelationStatus.Unavailable, Array.Empty<ResourceHolder>());

    // Stays two DWORDs: RM_UNIQUE_PROCESS is 12 bytes with 4-byte alignment, which a long field would break.
    [StructLayout(LayoutKind.Sequential)] internal struct FileTime { public uint Low, High; }
    [StructLayout(LayoutKind.Sequential)] internal struct UniqueProcess { public uint Pid; public FileTime Created; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
        public int AppType; public uint Status, SessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern uint RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern uint RmRegisterResources(uint session, uint count,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] paths, uint applications, nint applicationArray, uint services, nint serviceArray);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern uint RmGetList(uint session, out uint needed, ref uint count,
        [In, Out] ProcessInfo[]? processes, out uint rebootReasons);
    [DllImport("rstrtmgr.dll")] private static extern uint RmEndSession(uint session);
}
