using System.ComponentModel;
using System.Diagnostics;
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

    public WindowsDesktopMonitor(TimeProvider? timeProvider = null, WindowsGpuTelemetryReader? gpuReader = null)
    {
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
    private DesktopProcessInventory SampleProcesses(CancellationToken ct)
    {
        var at = _clock.GetUtcNow();
        var sessions = new List<SessionObservation>();
        var alive = new HashSet<string>();
        var liveIo = new HashSet<SessionProcessKey>();
        int attempted = 0, successfulQueries = 0;
        bool complete = true;
        foreach (string name in ClientProcessNames)
        {
            ct.ThrowIfCancellationRequested();
            if (attempted >= DesktopSamplingCadence.MaximumProcessSamples) { complete = false; break; }
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); successfulQueries++; }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
            { complete = false; continue; }
            try
            {
                if (processes.Length >= DesktopSamplingCadence.MaximumProcessSamples - attempted) complete = false;
                foreach (var process in processes)
                {
                    ct.ThrowIfCancellationRequested(); if (attempted >= DesktopSamplingCadence.MaximumProcessSamples) { complete = false; break; }
                    attempted++;
                    int processId = 0;
                    SessionProcessKey? ioProcess = null;
                    try
                    {
                        processId = process.Id;
                        long start = process.StartTime.ToUniversalTime().Ticks;
                        if (start <= 0) throw new InvalidOperationException("Process start time was unavailable.");
                        ioProcess = new(null, processId, start);
                        nint window = process.MainWindowHandle;
                        var cpu = process.TotalProcessorTime; long ticks = _clock.GetTimestamp();
                        var measured=ProcessMemoryReader.Read(processId,start);
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
                        long working = measured?.WorkingSetBytes??process.WorkingSet64, commit = measured?.PrivateCommitBytes??process.PrivateMemorySize64;
                        long virtualAddress=process.VirtualMemorySize64;
                        process.Refresh();
                        if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != start)
                        { complete = false; _ioRates.Unavailable(ioProcess.Value); continue; }
                        var ioCounters = ProcessIoReader.Read(processId, start, ct);
                        ProcessIoRates? io = null;
                        if (ioCounters is null) _ioRates.Unavailable(ioProcess.Value);
                        else { io = _ioRates.Observe(ioProcess.Value, ioCounters, ct); liveIo.Add(ioProcess.Value); }
                        bool visible = window != 0 && Native.IsWindowVisible(window);
                        // One read-only user32 query on the already-known main window: no extra window enumeration,
                        // no message sent to the client, no process handle. Hidden windows are not judged.
                        bool? hung = visible ? IsHung(window) : null;
                        sessions.Add(new(processId, start, "Version not sampled", window, "Roblox", window != 0 && Native.IsIconic(window),
                            visible, working, commit, percent, _clock.GetUtcNow(),null,measured?.PrivateWorkingSetBytes,virtualAddress,faultsPerSecond,io,hung));
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
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        if (successfulQueries == 0) { _ioRates.Clear(); throw new InvalidOperationException("Windows process inventory was unavailable."); }
        _ioRates.Retain(liveIo, ct);
        foreach (string key in _processTimes.Keys.Where(k => !alive.Contains(k)).ToArray()) _processTimes.Remove(key);
        return new(sessions, complete && attempted < DesktopSamplingCadence.MaximumProcessSamples);
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
    /// <summary>Windows' own "Not responding" test: the window's thread has not processed messages for about 5 s.</summary>
    internal static bool IsHung(nint window) => window != 0 && Native.IsHungAppWindow(window);
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
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsHungAppWindow(nint window);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindowAsync(nint window, int command);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint window);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(nint window, uint message, nint w, nint l);
    }
}
