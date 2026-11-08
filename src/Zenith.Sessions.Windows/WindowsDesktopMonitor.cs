using System.ComponentModel;
using System.Runtime.InteropServices;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>An explicit enumeration result. Completeness is opt-in and may be downgraded by the sampling owner.</summary>
public sealed record DesktopProcessInventory(IReadOnlyList<SessionObservation> Sessions, bool Complete = false);

/// <summary>
/// Cadence/cache decisions shared by the native monitor and deterministic tests. The caller owns serialization;
/// this class starts no work itself. UTC is observation data only; elapsed decisions use the monotonic clock.
/// </summary>
public sealed class DesktopSamplingCadence(TimeProvider? timeProvider = null)
{
    public const int SystemSampleMs = 1000, MaximumProcessSamples = 128;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly ClientResponsivenessReducer _responsiveness = new();
    private ResourceSnapshot? _resources;
    private IReadOnlyList<SessionObservation>? _sessions;
    private long _systemAt, _processAt;
    private DateTimeOffset? _processObservedUtc;
    private long _processRevision, _revisionCounter;
    private bool _processInventoryComplete;
    private string? _processProblem;

    public DesktopSnapshot Sample(int processSampleMs, Func<CancellationToken, ResourceSnapshot> sampleSystem,
        Func<CancellationToken, IReadOnlyList<SessionObservation>> sampleProcesses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sampleProcesses);
        // Legacy/injected list-only producers have no explicit completeness evidence.
        return SampleInventory(processSampleMs, sampleSystem, token => new(sampleProcesses(token)), ct);
    }

    public DesktopSnapshot SampleInventory(int processSampleMs, Func<CancellationToken, ResourceSnapshot> sampleSystem,
        Func<CancellationToken, DesktopProcessInventory> sampleProcesses, CancellationToken ct)
    {
        if (processSampleMs is < 500 or > 5000) throw new ArgumentOutOfRangeException(nameof(processSampleMs));
        ArgumentNullException.ThrowIfNull(sampleSystem); ArgumentNullException.ThrowIfNull(sampleProcesses);
        ct.ThrowIfCancellationRequested();
        long now = _clock.GetTimestamp();
        if (_sessions is null || Due(_processAt, now, processSampleMs))
        {
            try
            {
                var measured = sampleProcesses(ct);
                ct.ThrowIfCancellationRequested();
                // Rows are observed inside the enumeration. Its evidence time must follow those
                // observations, while subsequent resource-only refreshes retain this exact value.
                DateTimeOffset observedUtc = _clock.GetUtcNow();
                if (measured?.Sessions is null) throw new InvalidOperationException("Process inventory was unavailable.");
                int count = Math.Min(measured.Sessions.Count, MaximumProcessSamples), written = 0;
                var owned = new SessionObservation[count];
                var processIds = new HashSet<int>();
                // Reaching the operational scan budget is not proof that all clients were visited.
                bool complete = measured.Complete && measured.Sessions.Count < MaximumProcessSamples;
                for (int index = 0; index < count; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    var row = measured.Sessions[index];
                    if (row is null) { complete = false; continue; }
                    owned[written++] = row;
                    if (row.Problem is not null || row.ProcessId <= 0 || row.ProcessStartUtcTicks <= 0
                        || row.ProcessStartUtcTicks > DateTime.MaxValue.Ticks
                        || row.ProcessStartUtcTicks > observedUtc.UtcTicks
                        || row.ObservedUtc > observedUtc || row.ProcessStartUtcTicks > row.ObservedUtc.UtcTicks
                        || !processIds.Add(row.ProcessId)) complete = false;
                }
                if (written != owned.Length) Array.Resize(ref owned, written);
                ct.ThrowIfCancellationRequested();
                long revision = checked(_revisionCounter + 1);
                // Hang evidence advances once per accepted enumeration; cached, failed and canceled scans never count.
                owned = _responsiveness.Apply(owned);
                _sessions = Array.AsReadOnly(owned); _processAt = now;
                _processObservedUtc = observedUtc; _processRevision = revision; _revisionCounter = revision;
                _processInventoryComplete = complete; _processProblem = null;
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
            {
                ct.ThrowIfCancellationRequested();
                // A failed scan neither invents a new observation time nor retires a missing client.
                // Throttle another attempt through the existing cadence, including on the first failure.
                _sessions ??= Array.Empty<SessionObservation>(); _processAt = now;
                _processInventoryComplete = false;
                _processProblem = "Windows process inventory is temporarily unavailable. Missing clients were not confirmed absent.";
            }
        }
        ct.ThrowIfCancellationRequested();
        now = _clock.GetTimestamp();
        // When both measurements are due, observe process allocations before system memory.
        // Their original timestamps can then establish conservative accounting order; a
        // process-only refresh still leaves the independently cached system evidence unchanged.
        if (_resources is null || Due(_systemAt, now, SystemSampleMs))
        {
            var measured = sampleSystem(ct);
            ct.ThrowIfCancellationRequested();
            _resources = measured; _systemAt = now;
        }
        // Resource refreshes never renew the cached process inventory's evidence or row timestamps.
        return new(_resources!, _sessions!, _processProblem, _processObservedUtc, _processRevision, _processInventoryComplete);
    }

    private bool Due(long previous, long now, int intervalMs)
    {
        var elapsed = _clock.GetElapsedTime(previous, now);
        return elapsed < TimeSpan.Zero || elapsed >= TimeSpan.FromMilliseconds(intervalMs);
    }

    public void Clear()
    {
        _resources = null; _sessions = null; _systemAt = 0; _processAt = 0;
        _processObservedUtc = null; _processRevision = 0; _processInventoryComplete = false; _processProblem = null;
        _responsiveness.Clear();
        // Keep the instance's revision counter monotonic if the sampling cache is reused after clear.
    }
}

public sealed partial class WindowsDesktopMonitor : IConfigurableDesktopMonitor,IClientWindowControl,IClientWindowArranger,IClientWindowBatchControl,IClientWindowResizer,IDisposable
{
    private readonly SemaphoreSlim _sampling = new(1);
    private readonly SemaphoreSlim _windowControl = new(1,1);
    private readonly ClientWindowOperations _windowOperations;
    private ulong _idle, _kernel, _user;
    private bool _cpuInitialized;
    private bool _disposed;
    private readonly TimeProvider _clock;
    private readonly DesktopSamplingCadence _cadence;
    private readonly ProcessIoRateTracker _ioRates;
    private readonly WindowsGpuTelemetryReader _gpu;
    private readonly Func<CancellationToken, ResourceSnapshot> _sampleSystem;
    private readonly Func<CancellationToken, DesktopProcessInventory> _sampleProcesses;
    private sealed record SamplingConfiguration(int ProcessSampleMs);
    private SamplingConfiguration _configuration = new(1000);
    private static readonly string[] ClientProcessNames = ["RobloxPlayerBeta", "RobloxPlayer", "Roblox"];
    private readonly Dictionary<string, (TimeSpan Cpu, long At,uint? Faults)> _processTimes = [];
    private readonly IClientProcessSource _processes;
    private readonly List<ClientProcessEntry> _clients = []; // Reused by every sample; holds one snapshot's clients.
    private readonly ITopLevelWindows _windows;
    private readonly ClientMainWindowIndex _mainWindows;

    public WindowsDesktopMonitor(TimeProvider? timeProvider = null, WindowsGpuTelemetryReader? gpuReader = null)
        : this(timeProvider, gpuReader, new NativeClientProcessSource(ClientProcessNames), NativeTopLevelWindows.Instance) { }

    /// <summary>Test seam. <paramref name="processes"/> is matched against <see cref="ClientProcessNames"/> by name index.</summary>
    internal WindowsDesktopMonitor(TimeProvider? timeProvider, WindowsGpuTelemetryReader? gpuReader,
        IClientProcessSource processes, ITopLevelWindows windows)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _mainWindows = new(windows);
        _clock = timeProvider ?? TimeProvider.System;
        _cadence = new(_clock);
        _ioRates = new(_clock);
        _gpu = gpuReader ?? new(_clock);
        _sampleSystem = SampleSystem; _sampleProcesses = SampleProcesses;
        _windowOperations = new(_windowControl, new NativeClientWindowAdapter());
    }

    public void ConfigurePerformance(ManagerPerformancePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ObjectDisposedException.ThrowIf(_disposed, this);
        policy.Validated();
        // Immutable publication does not move either last-measured timestamp. The next owned call sees the new interval.
        Volatile.Write(ref _configuration, new(policy.ProcessSampleMs));
    }

    public async Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _sampling.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(() => _cadence.SampleInventory(Volatile.Read(ref _configuration).ProcessSampleMs,
            _sampleSystem, _sampleProcesses, ct), ct).ConfigureAwait(false); }
        finally { _sampling.Release(); }
    }
    private ResourceSnapshot SampleSystem(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var at = _clock.GetUtcNow();
        var memory = new Native.MemoryStatus { Length = (uint)Marshal.SizeOf<Native.MemoryStatus>() };
        var performance = new Native.PerformanceInfo { Size = (uint)Marshal.SizeOf<Native.PerformanceInfo>() };
        bool valid = Native.GlobalMemoryStatusEx(ref memory) && Native.GetPerformanceInfo(ref performance, performance.Size);
        double? cpu = ReadCpu();
        // Optional GPU failure is reported only in its own snapshot. It must not turn a valid
        // physical/commit sample into a failure or manufacture free GPU capacity.
        var gpu = _gpu.Read(ct);
        ct.ThrowIfCancellationRequested();
        return new ResourceSnapshot(at, memory.TotalPhysical, memory.AvailablePhysical,
            Multiply(performance.CommitLimit, performance.PageSize), Multiply(performance.CommitTotal, performance.PageSize), cpu,
            valid ? null : "Windows memory or commit accounting is temporarily unavailable.", gpu);
    }
    /// <summary>One process sample. Callers serialize it (the cadence runs it under the sampling gate).</summary>
    internal DesktopProcessInventory SampleProcesses(CancellationToken ct)
    {
        var at = _clock.GetUtcNow();
        var sessions = new List<SessionObservation>();
        var alive = new HashSet<string>();
        var liveIo = new HashSet<SessionProcessKey>();
        var clients = _clients;
        clients.Clear();
        // One system process snapshot covers every client name. Each client is then read through one query-limited
        // handle (start and processor time, memory, I/O and exit state), not one handle per Process property.
        try { _processes.Snapshot(clients, ct); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            clients.Clear(); _ioRates.Clear();
            throw new InvalidOperationException("Windows process inventory was unavailable.", error);
        }
        // Reaching the scan budget is not proof that all clients were visited.
        bool complete = clients.Count < DesktopSamplingCadence.MaximumProcessSamples;
        // Rows keep the client-name order (RobloxPlayerBeta, RobloxPlayer, Roblox) and the scan budget keeps the earlier
        // names first, as the per-name queries did.
        Span<int> order = stackalloc int[DesktopSamplingCadence.MaximumProcessSamples];
        Span<int> clientIds = stackalloc int[DesktopSamplingCadence.MaximumProcessSamples];
        int attempted = 0;
        for (int name = 0; name < ClientProcessNames.Length && attempted < order.Length; name++)
            for (int index = 0; index < clients.Count && attempted < order.Length; index++)
                if (clients[index].NameIndex == name) { order[attempted] = index; clientIds[attempted++] = clients[index].ProcessId; }
        // Every client's main window comes from one shared top-level window pass. Process.MainWindowHandle would
        // walk every desktop window once per client. SameClient before every window action is unchanged.
        _mainWindows.Build(clientIds[..attempted], ct);
        for (int sampled = 0; sampled < attempted; sampled++)
        {
            ct.ThrowIfCancellationRequested();
            var client = clients[order[sampled]];
            int processId = client.ProcessId;
            SessionProcessKey? ioProcess = null;
            try
            {
                var reading = _processes.Read(processId, ct);
                long start = reading.StartUtcTicks;
                if (reading.Outcome == ClientProcessReadOutcome.Exited)
                {
                    // It ended during this sample: no row, and the scan cannot confirm the other clients' absence either.
                    complete = false;
                    if (start > 0) _ioRates.Unavailable(new(null, processId, start));
                    continue;
                }
                if (reading.Outcome != ClientProcessReadOutcome.Read || start <= 0)
                    throw new InvalidOperationException("Process start time was unavailable.");
                ioProcess = new(null, processId, start);
                nint window = _mainWindows.MainWindow(processId);
                var cpu = reading.ProcessorTime; long ticks = _clock.GetTimestamp();
                var measured = reading.Memory;
                if (measured is null) complete = false;
                string key = $"{processId}:{start}"; alive.Add(key);
                double? percent = null,faultsPerSecond=null;
                if (_processTimes.TryGetValue(key, out var previous) && ticks > previous.At)
                {
                    double seconds = _clock.GetElapsedTime(previous.At, ticks).TotalSeconds;
                    if (seconds > 0 && cpu >= previous.Cpu)
                        percent = Math.Clamp((cpu - previous.Cpu).TotalSeconds / seconds / Environment.ProcessorCount * 100, 0, 100);
                    if(seconds > 0 && measured is not null && previous.Faults is { } faults && measured.PageFaultCount>=faults)
                        faultsPerSecond=(measured.PageFaultCount-faults)/seconds;
                }
                if (_processTimes.Count >= DesktopSamplingCadence.MaximumProcessSamples && !_processTimes.ContainsKey(key))
                {
                    // A churned process may lose one delta baseline; it must not make the bounded cache grow.
                    string? evict = null;
                    foreach (string cached in _processTimes.Keys) { evict = cached; break; }
                    if (evict is not null) _processTimes.Remove(evict);
                }
                _processTimes[key] = (cpu, ticks,measured?.PageFaultCount);
                // Snapshot sizes stand in only when the handle's memory counters were unavailable.
                long working = measured?.WorkingSetBytes ?? client.WorkingSetBytes, commit = measured?.PrivateCommitBytes ?? client.PrivateBytes;
                ProcessIoRates? io = null;
                if (reading.Io is null) _ioRates.Unavailable(ioProcess.Value);
                else { io = _ioRates.Observe(ioProcess.Value, reading.Io, ct); liveIo.Add(ioProcess.Value); }
                bool visible = window != 0 && _windows.IsVisible(window);
                // One read-only user32 query on the already-known main window: no extra window enumeration,
                // no message sent to the client, no process handle. A hidden, closed or missing window is not
                // judged: null (not checked), never false, so it never counts as a responding sample.
                bool? hung = visible ? _windows.IsHung(window) : null;
                sessions.Add(new(processId, start, "Version not sampled", window, "Roblox", window != 0 && Native.IsIconic(window),
                    visible, working, commit, percent, _clock.GetUtcNow(),null,measured?.PrivateWorkingSetBytes,client.VirtualBytes,faultsPerSecond,io,hung));
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
            {
                // Do not infer an account, running state or zero memory from an inaccessible process.
                complete = false;
                if (ioProcess is { } failedIo) _ioRates.Unavailable(failedIo);
                if (processId > 0) sessions.Add(new(processId, 0, "Unavailable", 0, "Roblox", false, false, 0, 0, null, at,
                    "Process identity or access was unavailable during observation."));
            }
        }
        clients.Clear();
        _ioRates.Retain(liveIo, ct);
        foreach (string key in _processTimes.Keys.Where(k => !alive.Contains(k)).ToArray()) _processTimes.Remove(key);
        return new(sessions, complete);
    }

    private double? ReadCpu()
    {
        if (!Native.GetSystemTimes(out ulong idle, out ulong kernel, out ulong user)) return null;
        double? result = null;
        if (_cpuInitialized && kernel >= _kernel && user >= _user && idle >= _idle)
        {
            ulong total = kernel - _kernel + user - _user;
            if (total > 0) result = Math.Clamp((1 - (idle - _idle) / (double)total) * 100, 0, 100);
        }
        _idle = idle; _kernel = kernel; _user = user; _cpuInitialized = true;
        return result;
    }
    private static ulong Multiply(nuint pages, nuint size)
    {
        ulong a = (ulong)pages, b = (ulong)size;
        return b != 0 && a <= ulong.MaxValue / b ? a * b : 0;
    }
    private static bool SameInstance(SessionObservation session)
    {
        return NativeClientWindowAdapter.SameClient(session);
    }
    public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); if (!SameInstance(session)) return Task.FromResult(false);
        Native.ShowWindowAsync(session.Window, 9);
        ct.ThrowIfCancellationRequested(); if (!SameInstance(session)) return Task.FromResult(false);
        bool requested = Native.SetForegroundWindow(session.Window);
        return Task.FromResult(requested && Native.GetForegroundWindow() == session.Window);
    }
    public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); if (!SameInstance(session)) return Task.FromResult(false);
        return Task.FromResult(Native.PostMessage(session.Window, 0x10, 0, 0)); // Normal WM_CLOSE; never force-kill.
    }

    public Task<WindowActionResult> ChangeAsync(SessionObservation session,ClientWindowAction action,CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _windowOperations.ChangeAsync(session, action, ct);
    }
    public Task<ClientWindowArrangementResult> ChangeManyAsync(IReadOnlyList<SessionObservation> sessions, ClientWindowAction action, CancellationToken ct)
    { ObjectDisposedException.ThrowIf(_disposed, this); return _windowOperations.ChangeManyAsync(sessions, action, ct); }

    public Task<ClientDisplayList> GetDisplaysAsync(CancellationToken ct)
    { ObjectDisposedException.ThrowIf(_disposed, this); return _windowOperations.DisplaysAsync(ct); }
    public Task<ClientWindowArrangementResult> ArrangeAsync(IReadOnlyList<SessionObservation> sessions, string displayId, ClientWindowTileOptions options, CancellationToken ct)
    { ObjectDisposedException.ThrowIf(_disposed, this); return _windowOperations.ArrangeAsync(sessions, displayId, options, ct); }
    public Task<ClientWindowArrangementResult> ResizeAsync(IReadOnlyList<SessionObservation> sessions, ClientContentSize size, CancellationToken ct)
    { ObjectDisposedException.ThrowIf(_disposed, this); return _windowOperations.ResizeAsync(sessions, size, ct); }
    public void CancelWindowOperations() => _windowOperations.Cancel();

    // The application cancels/awaits its single sampling loop and window work before disposal.
    public void Dispose()
    {
        if (_disposed) return;
        _windowOperations.Cancel();
        _disposed = true; _cadence.Clear(); _processTimes.Clear(); _ioRates.Clear(); _gpu.Dispose();
        _sampling.Dispose(); _windowControl.Dispose();
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct MemoryStatus
        { public uint Length, MemoryLoad; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, ExtendedVirtual; }
        [StructLayout(LayoutKind.Sequential)] internal struct PerformanceInfo
        { public uint Size; public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize; public uint HandleCount, ProcessCount, ThreadCount; }
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
        [DllImport("psapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetPerformanceInfo(ref PerformanceInfo info, uint size);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint window);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindowAsync(nint window, int command);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint window);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(nint window, uint message, nint w, nint l);
    }
}
