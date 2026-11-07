using System.Runtime.InteropServices;

namespace Zenith.Sessions.Windows;

/// <summary>Top-level desktop windows in Windows' Z order. The native source is the only implementation outside tests.</summary>
internal interface ITopLevelWindows
{
    /// <summary>Calls <paramref name="visitor"/> with each top-level window and its owning process id, in Z order, until it returns false.</summary>
    void Enumerate(ITopLevelWindowVisitor visitor);

    /// <summary>The main-window test .NET's Process.MainWindowHandle applies: no owner window and visible.</summary>
    bool IsUnownedAndVisible(nint window);
}

internal interface ITopLevelWindowVisitor
{
    /// <summary>Returns false to stop the enumeration.</summary>
    bool Visit(nint window, int processId);
}

/// <summary>
/// Finds the main window of every sampled client with one top-level window pass per process sample. Reading
/// Process.MainWindowHandle instead runs a full pass over every desktop window for each client.
/// The rule is .NET's own (MainWindowFinder): the first window in Z order that belongs to the process, has no owner
/// and is visible. A client without such a window maps to 0, which the sampler already reports as not visible.
/// The caller serializes calls; collections are reused between samples and hold at most one entry per client.
/// </summary>
internal sealed class ClientMainWindowIndex(ITopLevelWindows windows) : ITopLevelWindowVisitor
{
    private readonly ITopLevelWindows _windows = windows ?? throw new ArgumentNullException(nameof(windows));
    private readonly HashSet<int> _clients = [];
    private readonly Dictionary<int, nint> _mainWindows = [];
    private CancellationToken _ct;

    public void Build(ReadOnlySpan<int> processIds, CancellationToken ct)
    {
        _clients.Clear(); _mainWindows.Clear();
        ct.ThrowIfCancellationRequested();
        foreach (int processId in processIds) if (processId > 0) _clients.Add(processId);
        if (_clients.Count == 0) return; // No client: no window pass.
        bool completed = false;
        _ct = ct;
        try
        {
            _windows.Enumerate(this);
            ct.ThrowIfCancellationRequested();
            completed = true;
        }
        finally
        {
            _ct = default;
            // A stopped pass leaves no partial result for a later reader.
            if (!completed) _mainWindows.Clear();
        }
    }

    /// <summary>The client's main window from the last <see cref="Build"/>, or 0 when it had none.</summary>
    public nint MainWindow(int processId) => _mainWindows.TryGetValue(processId, out nint window) ? window : 0;

    bool ITopLevelWindowVisitor.Visit(nint window, int processId)
    {
        if (_ct.IsCancellationRequested) return false;
        // Window attributes are read only for client windows, and only until that client has its main window.
        if (_clients.Contains(processId) && !_mainWindows.ContainsKey(processId) && _windows.IsUnownedAndVisible(window))
            _mainWindows.Add(processId, window);
        return _mainWindows.Count < _clients.Count; // Stop once every client has its window.
    }
}

/// <summary>Reads top-level windows through user32 without sending any message to them.</summary>
internal sealed class NativeTopLevelWindows : ITopLevelWindows
{
    private const uint OwnerWindow = 4; // GW_OWNER
    // A static delegate stays alive for every native callback and allocates nothing per pass.
    private static readonly Native.EnumWindowsCallback Callback = Visit;
    public static NativeTopLevelWindows Instance { get; } = new();
    private NativeTopLevelWindows() { }

    public void Enumerate(ITopLevelWindowVisitor visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        var state = GCHandle.Alloc(visitor);
        // EnumWindows also returns false when the visitor stops early, so its result is not a failure signal.
        // As with Process.MainWindowHandle, a client whose window was not seen keeps window 0.
        try { Native.EnumWindows(Callback, GCHandle.ToIntPtr(state)); }
        finally { state.Free(); }
    }

    public bool IsUnownedAndVisible(nint window) => Native.GetWindow(window, OwnerWindow) == 0 && Native.IsWindowVisible(window);

    private static bool Visit(nint window, nint state)
    {
        // A window closed during the pass reports no process and can never match a client.
        int processId = Native.GetWindowThreadProcessId(window, out uint owner) == 0 ? 0 : unchecked((int)owner);
        return ((ITopLevelWindowVisitor)GCHandle.FromIntPtr(state).Target!).Visit(window, processId);
    }

    private static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] [return: MarshalAs(UnmanagedType.Bool)]
        internal delegate bool EnumWindowsCallback(nint window, nint state);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowsCallback callback, nint state);
        [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
    }
}
