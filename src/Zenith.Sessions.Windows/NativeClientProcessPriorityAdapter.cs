using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>PROCESS_POWER_THROTTLING_STATE masks: which policies the process controls, and which of those are on.</summary>
internal readonly record struct ClientPowerThrottling(uint Control, uint State);

/// <summary>
/// The process values this control may change, exactly as Windows reported them. A null value is one Windows did not
/// report, for example power throttling on a Windows version that cannot query it; such a value is never written.
/// <see cref="CpuCap"/> is the hard cap of the Windows job this run created for the client, in Windows' units (hundredths of
/// a percent of all CPU time on the PC): 0 when the client is in no job or this run's job has no cap, null when the client is
/// in a job this run did not create or Windows did not report it.
/// </summary>
internal readonly record struct ClientProcessSettings(uint Priority, ClientPowerThrottling? Throttling, uint? MemoryPriority, ulong? Affinity,
    uint? CpuCap = null)
{
    internal const uint NormalClass = 0x20, BelowNormalClass = 0x4000;
    internal const uint ExecutionSpeedThrottling = 0x1; // PROCESS_POWER_THROTTLING_EXECUTION_SPEED (EcoQoS).
    internal const uint VeryLowMemoryPriority = 1; // MEMORY_PRIORITY_VERY_LOW.
    internal ClientProcessPriorityClass Class => Priority switch
    {
        NormalClass => ClientProcessPriorityClass.Normal,
        BelowNormalClass => ClientProcessPriorityClass.BelowNormal,
        _ => ClientProcessPriorityClass.Unsupported
    };
}

/// <summary>JOBOBJECT_CPU_RATE_CONTROL_INFORMATION: the control flags and, for a hard cap, the rate in hundredths of a percent of all CPU time.</summary>
internal readonly record struct ClientJobCpuRate(uint ControlFlags, uint CpuRate)
{
    internal const uint Enable = 0x1, HardCap = 0x4; // JOB_OBJECT_CPU_RATE_CONTROL_ENABLE, JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP.
    internal static readonly ClientJobCpuRate None = new(0, 0);
    internal static ClientJobCpuRate Cap(uint rate) => new(Enable | HardCap, rate);
    /// <summary>The cap as <see cref="ClientProcessSettings.CpuCap"/> reports it: 0 for no control, the rate for a hard cap, null for any other control Zenith never sets.</summary>
    internal uint? AsCap => ControlFlags == 0 ? 0u : ControlFlags == (Enable | HardCap) ? CpuRate : null;
}

/// <summary>A Windows job object handle. Zenith never sets KILL_ON_JOB_CLOSE, so closing it never ends a client.</summary>
internal sealed class SafeJobObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeJobObjectHandle() : base(ownsHandle: true) { }
    public SafeJobObjectHandle(nint handle, bool ownsHandle) : base(ownsHandle) => SetHandle(handle);
    protected override bool ReleaseHandle() => CloseHandle(handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

internal sealed record ClientPriorityNativeSnapshot(int ProcessId, long StartUtcTicks, bool Running,
    string ImagePath, string UserSid, uint Priority, ClientPowerThrottling? Throttling = null, uint? MemoryPriority = null,
    ulong? Affinity = null, ulong? SystemAffinity = null);
internal interface IClientPriorityNativeApi
{
    string? CurrentUserSid { get; }
    SafeProcessHandle Open(int processId, out int error);
    bool Read(SafeProcessHandle handle, out ClientPriorityNativeSnapshot? snapshot);
    bool Set(SafeProcessHandle handle, uint priority, out int error);
    bool SetThrottling(SafeProcessHandle handle, ClientPowerThrottling throttling, out int error);
    bool SetMemoryPriority(SafeProcessHandle handle, uint memoryPriority, out int error);
    bool SetAffinity(SafeProcessHandle handle, ulong affinity, out int error);
    /// <summary>Whether the process is in <paramref name="job"/>, or in any job when <paramref name="job"/> is null.</summary>
    bool IsInJob(SafeProcessHandle handle, SafeJobObjectHandle? job, out bool inJob);
    /// <summary>A new unnamed job with no limits; reachable only through this handle, which the caller keeps for the client's lifetime.</summary>
    SafeJobObjectHandle CreateJob(out int error);
    bool AssignToJob(SafeProcessHandle handle, SafeJobObjectHandle job, out int error);
    bool ReadCpuRate(SafeJobObjectHandle job, out ClientJobCpuRate rate);
    bool SetCpuRate(SafeJobObjectHandle job, ClientJobCpuRate rate, out int error);
}

/// <summary>
/// Each operation uses one exact process handle, with no elevation or process-memory access. The adapter also owns one job
/// handle per client this run capped (at most 128), kept until that client is seen to have exited or the adapter is disposed:
/// a job can be reached only through a handle (its name, if any, is unlinked when the last handle closes), so the handle is
/// what lets a later cap, Restore or Exit find this run's own job and clear its cap.
/// </summary>
internal sealed class NativeClientProcessPriorityAdapter : IClientPriorityAdapter
{
    private readonly IClientPriorityNativeApi _native;
    private readonly IClientCpuCapLedger _ledger;
    private readonly string _clientImage;
    private readonly object _gate = new();
    private readonly Dictionary<ClientCpuCapRecord, SafeJobObjectHandle> _jobs = [];
    private bool _disposed;
    /// <summary>The production Windows calls with the record of capped clients kept only for this run.</summary>
    public NativeClientProcessPriorityAdapter() : this(new InMemoryClientCpuCapLedger()) { }
    /// <summary>The production Windows calls, with the record of capped clients in <paramref name="ledger"/>.</summary>
    public NativeClientProcessPriorityAdapter(IClientCpuCapLedger ledger) : this(new NativeApi(), ledger) { }
    /// <summary>The production Windows calls for a test-owned helper whose executable is <paramref name="clientImage"/>.</summary>
    internal NativeClientProcessPriorityAdapter(IClientCpuCapLedger ledger, string clientImage) : this(new NativeApi(), ledger, clientImage) { }
    internal NativeClientProcessPriorityAdapter(IClientPriorityNativeApi native, string clientImage = "RobloxPlayerBeta.exe")
        : this(native, new InMemoryClientCpuCapLedger(), clientImage) { }
    /// <param name="clientImage">The only executable name accepted; tests name their own helper.</param>
    internal NativeClientProcessPriorityAdapter(IClientPriorityNativeApi native, IClientCpuCapLedger ledger, string clientImage = "RobloxPlayerBeta.exe")
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _clientImage = clientImage;
    }

    public ClientPriorityOpen Open(SessionObservation session)
    {
        string? user = _native.CurrentUserSid;
        if (string.IsNullOrEmpty(user)) return new(ClientProcessPriorityStatus.AccessDenied);
        var process = _native.Open(session.ProcessId, out int error);
        if (process.IsInvalid)
        {
            process.Dispose();
            // The caller has validated a positive PID. ERROR_INVALID_PARAMETER means no
            // process object could be opened for that PID; denied/unknown queries prove no exit.
            if (error == 87) { Retire(session.ProcessId, session.ProcessStartUtcTicks); return new(ClientProcessPriorityStatus.InvalidClient, Retired: true); }
            return new(ErrorStatus(error));
        }
        return new(ClientProcessPriorityStatus.Unchanged, new Handle(this, process, session, user));
    }

    /// <summary>
    /// Once per start: for each recorded client, asks Windows whether that exact instance still runs and is still in a job.
    /// Such a client still carries the cap an earlier run set, and no run can lift it. A client that has exited, or whose PID
    /// now belongs to another process, is dropped from the record. Nothing is changed.
    /// </summary>
    public ClientCpuCapLeftovers CheckLeftoverCaps(Func<bool> stopped)
    {
        ArgumentNullException.ThrowIfNull(stopped);
        int stillCapped = 0, notChecked = 0;
        bool unreadable = _ledger.Unreadable, stoppedEarly = false;
        foreach (var record in _ledger.Records)
        {
            if (stopped()) { stoppedEarly = true; break; }
            lock (_gate) if (_jobs.ContainsKey(record)) continue; // Capped by this run: not a leftover.
            using var process = _native.Open(record.ProcessId, out int error);
            if (process.IsInvalid)
            {
                if (error == 87) ForgetQuietly(record); else notChecked++;
                continue;
            }
            if (!_native.Read(process, out var current) || current is null) { notChecked++; continue; }
            if (!current.Running || current.StartUtcTicks != record.StartUtcTicks) { ForgetQuietly(record); continue; }
            if (!_native.IsInJob(process, null, out bool inJob)) { notChecked++; continue; }
            if (inJob) stillCapped++; else ForgetQuietly(record);
        }
        return new(stillCapped, notChecked, unreadable, stoppedEarly);
    }

    /// <summary>The job handle this run holds for the exact client, for tests' independent readback; null when it holds none.</summary>
    internal SafeJobObjectHandle? HeldJob(int processId, long startUtcTicks)
    {
        lock (_gate) return _jobs.GetValueOrDefault(new ClientCpuCapRecord(processId, startUtcTicks));
    }

    /// <summary>The client is gone: its record and this run's job handle are no longer needed. Closing the handle never ends a client.</summary>
    private void Retire(int processId, long startUtcTicks)
    {
        var record = new ClientCpuCapRecord(processId, startUtcTicks);
        SafeJobObjectHandle? job;
        lock (_gate) if (_jobs.Remove(record, out job)) { } else job = null;
        job?.Dispose();
        ForgetQuietly(record);
    }

    /// <summary>A record that cannot be dropped now is harmless: a later start drops it when its client is gone.</summary>
    private void ForgetQuietly(ClientCpuCapRecord record)
    {
        try { _ledger.Forget(record.ProcessId, record.StartUtcTicks); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        SafeJobObjectHandle[] jobs;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            jobs = _jobs.Values.ToArray();
            _jobs.Clear();
        }
        foreach (var job in jobs) job.Dispose();
    }

    private static ClientProcessPriorityStatus ErrorStatus(int error) => error == 5
        ? ClientProcessPriorityStatus.AccessDenied : ClientProcessPriorityStatus.NotConfirmed;

    private sealed class Handle(NativeClientProcessPriorityAdapter owner, SafeProcessHandle process,
        SessionObservation expected, string user) : IClientPriorityHandle
    {
        private readonly IClientPriorityNativeApi _native = owner._native;
        private readonly ClientCpuCapRecord _record = new(expected.ProcessId, expected.ProcessStartUtcTicks);

        public ClientPriorityRead Read()
        {
            if (!_native.Read(process, out var current) || current is null)
                return new(ClientProcessPriorityStatus.NotConfirmed);
            if (!current.Running || current.ProcessId != expected.ProcessId || current.StartUtcTicks != expected.ProcessStartUtcTicks)
            {
                bool retired = current.ProcessId == expected.ProcessId
                    && (!current.Running || current.StartUtcTicks > 0 && current.StartUtcTicks != expected.ProcessStartUtcTicks);
                if (retired) owner.Retire(expected.ProcessId, expected.ProcessStartUtcTicks);
                return new(ClientProcessPriorityStatus.InvalidClient, Retired: retired);
            }
            if (!string.Equals(current.UserSid, user, StringComparison.Ordinal))
                return new(ClientProcessPriorityStatus.AccessDenied);
            if (!string.Equals(Path.GetFileName(current.ImagePath), owner._clientImage, StringComparison.OrdinalIgnoreCase))
                return new(ClientProcessPriorityStatus.InvalidClient);
            var (cap, otherJob) = ReadCap();
            var settings = new ClientProcessSettings(current.Priority, current.Throttling, current.MemoryPriority, current.Affinity, cap);
            return new(ClientProcessPriorityStatus.Unchanged, settings.Class, Settings: settings, SystemAffinity: current.SystemAffinity, InForeignJob: otherJob);
        }

        /// <summary>
        /// The client's cap: 0 when it is in no job, or in this run's own job (the held handle, confirmed by membership) with no
        /// control; the rate when that job has a hard cap; otherwise null, with <c>otherJob</c> true when the client is in a job
        /// this run did not create (another program's, or one left by an earlier run of Zenith).
        /// </summary>
        private (uint? Cap, bool OtherJob) ReadCap()
        {
            if (!_native.IsInJob(process, null, out bool inAnyJob)) return (null, false);
            if (!inAnyJob) return (0, false);
            SafeJobObjectHandle? job;
            lock (owner._gate) job = owner._jobs.GetValueOrDefault(_record);
            if (job is null || !_native.IsInJob(process, job, out bool inOwnJob) || !inOwnJob) return (null, true);
            return _native.ReadCpuRate(job, out var rate) ? (rate.AsCap, false) : (null, false);
        }

        public ClientPriorityWrite Change(ClientProcessSettings expectedSettings, ClientProcessSettings desired, Func<bool> stopped)
        {
            var current = Read();
            if (current.Status != ClientProcessPriorityStatus.Unchanged || current.Settings is not { } now)
                return new(false, current.Status, current.Current, current.Retired);
            if (now.Class is not (ClientProcessPriorityClass.Normal or ClientProcessPriorityClass.BelowNormal))
                return new(false, ClientProcessPriorityStatus.UnsupportedPriority, now.Class, Settings: now);
            if (now != expectedSettings)
                return new(false, ClientProcessPriorityStatus.Conflict, now.Class, Settings: now);
            if (desired.Class is not (ClientProcessPriorityClass.Normal or ClientProcessPriorityClass.BelowNormal) || desired.Affinity == 0
                || desired.CpuCap is > 10000)
                return new(false, ClientProcessPriorityStatus.UnsupportedPriority, now.Class, Settings: now);
            if (stopped()) return new(false, ClientProcessPriorityStatus.Cancelled, now.Class, Settings: now);
            // Windows has no compare-and-set for these values. This refuses an OBSERVED conflict.
            // Same-handle identity, settings and cancellation were checked immediately before the first set.
            // Every value for this client is then written, so a stop cannot leave it half changed; a refusal
            // after an accepted write still reports Accepted so the caller keeps ownership and reads back.
            bool accepted = false;
            int error = 0;
            if (desired.Affinity is { } affinity && affinity != now.Affinity)
            {
                if (!_native.SetAffinity(process, affinity, out error)) return new(accepted, ErrorStatus(error));
                accepted = true;
            }
            if (desired.MemoryPriority is { } memory && memory != now.MemoryPriority)
            {
                if (!_native.SetMemoryPriority(process, memory, out error)) return new(accepted, ErrorStatus(error));
                accepted = true;
            }
            if (desired.Throttling is { } throttling && throttling != now.Throttling)
            {
                if (!_native.SetThrottling(process, throttling, out error)) return new(accepted, ErrorStatus(error));
                accepted = true;
            }
            if (desired.Priority != now.Priority)
            {
                if (!_native.Set(process, desired.Priority, out error)) return new(accepted, ErrorStatus(error));
                accepted = true;
            }
            if (desired.CpuCap is { } cap && cap != now.CpuCap)
            {
                if (!WriteCap(cap, out var refusal)) return new(accepted, refusal);
                accepted = true;
            }
            return new(accepted, ClientProcessPriorityStatus.Applied);
        }

        /// <summary>
        /// Sets or clears the hard cap of this run's own job for the client. The first cap records the client, then creates
        /// the job, keeps its handle and puts the client in it; a crash after that leaves the record for the next start to
        /// name the client. A refusal before the rate is set leaves at most an inert job (no limits) that the next cap reuses.
        /// A cleared cap drops the record; the handle stays until the client is seen to have exited.
        /// </summary>
        private bool WriteCap(uint cap, out ClientProcessPriorityStatus refusal)
        {
            refusal = ClientProcessPriorityStatus.NotConfirmed;
            SafeJobObjectHandle? job;
            lock (owner._gate) job = owner._jobs.GetValueOrDefault(_record);
            if (job is not null)
            {
                if (!_native.IsInJob(process, job, out bool inOwnJob) || !inOwnJob) { refusal = ClientProcessPriorityStatus.Conflict; return false; }
                return SetRate(job, cap, out refusal);
            }
            if (cap == 0) return true; // Not in any job: there is no cap to clear.
            try { owner._ledger.Remember(_record); }
            catch (InvalidOperationException) { refusal = ClientProcessPriorityStatus.CapacityReached; return false; }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return false; }
            var created = _native.CreateJob(out int error);
            if (created.IsInvalid)
            {
                created.Dispose();
                owner.ForgetQuietly(_record);
                refusal = ErrorStatus(error);
                return false;
            }
            if (!_native.AssignToJob(process, created, out error))
            {
                created.Dispose();
                owner.ForgetQuietly(_record);
                refusal = ErrorStatus(error);
                return false;
            }
            // The client is in the job for life now; keep the only handle that can reach it, within the bounded table.
            lock (owner._gate)
            {
                if (owner._disposed || owner._jobs.Count >= IClientCpuCapLedger.Capacity)
                {
                    created.Dispose();
                    owner.ForgetQuietly(_record);
                    refusal = ClientProcessPriorityStatus.CapacityReached;
                    return false;
                }
                owner._jobs[_record] = created;
            }
            return SetRate(created, cap, out refusal);
        }

        private bool SetRate(SafeJobObjectHandle job, uint cap, out ClientProcessPriorityStatus refusal)
        {
            refusal = ClientProcessPriorityStatus.NotConfirmed;
            if (!_native.SetCpuRate(job, cap == 0 ? ClientJobCpuRate.None : ClientJobCpuRate.Cap(cap), out int error))
            {
                refusal = ErrorStatus(error);
                if (cap != 0) owner.ForgetQuietly(_record); // No cap is on the client, so the record would mislead the next start.
                return false;
            }
            if (cap == 0) owner.ForgetQuietly(_record);
            else
            {
                try { owner._ledger.Remember(_record); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            }
            return true;
        }

        public void Dispose() => process.Dispose();
    }

    private sealed class NativeApi : IClientPriorityNativeApi
    {
        public string? CurrentUserSid
        {
            get
            {
                using var identity = WindowsIdentity.GetCurrent();
                return identity.User?.Value;
            }
        }
        public SafeProcessHandle Open(int processId, out int error)
        {
            // Query limited, set information, synchronize. SetProcessInformation and SetProcessAffinityMask
            // need no right beyond PROCESS_SET_INFORMATION.
            var handle = Native.OpenProcess(0x1000 | 0x0200 | 0x00100000, false, processId);
            error = Marshal.GetLastWin32Error();
            return handle;
        }

        public bool Read(SafeProcessHandle process, out ClientPriorityNativeSnapshot? snapshot)
        {
            snapshot = null;
            uint pid = Native.GetProcessId(process);
            if (pid == 0 || pid > int.MaxValue || ProcessInstanceIdentity.StartTicks(process) is not { } start) return false;
            uint wait = Native.WaitForSingleObject(process, 0);
            if (wait == 0) { snapshot = new((int)pid, start, false, "", "", 0); return true; }
            if (wait != 258) return false;
            var image = new StringBuilder(1024);
            uint length = (uint)image.Capacity;
            if (!Native.QueryFullProcessImageName(process, 0, image, ref length)) return false;
            string? sid = ProcessTokenUser.Read(process);
            if (sid is null) return false;
            uint priority = Native.GetPriorityClass(process);
            if (priority == 0) return false;
            // Optional values: one Windows does not report stays unknown, so it is never written or restored.
            ClientPowerThrottling? throttling = null;
            var power = new PowerThrottlingState { Version = PowerThrottlingVersion };
            if (Native.GetProcessInformation(process, ProcessPowerThrottling, ref power, (uint)Marshal.SizeOf<PowerThrottlingState>()))
                throttling = new(power.ControlMask, power.StateMask);
            uint? memory = null;
            var memoryInfo = new MemoryPriorityInformation();
            if (Native.GetProcessInformation(process, ProcessMemoryPriority, ref memoryInfo, (uint)Marshal.SizeOf<MemoryPriorityInformation>()))
                memory = memoryInfo.MemoryPriority;
            // Both masks are zero when the process has threads in more than one processor group.
            ulong? affinity = null, system = null;
            if (Native.GetProcessAffinityMask(process, out nuint own, out nuint all) && own != 0 && all != 0)
            {
                affinity = own;
                system = all;
            }
            // A signalled process is conclusively retired. A failed wait remains unknown.
            wait = Native.WaitForSingleObject(process, 0);
            if (wait is not (0 or 258)) return false;
            snapshot = new((int)pid, start, wait == 258, image.ToString(), sid, priority, throttling, memory, affinity, system);
            return true;
        }
        public bool Set(SafeProcessHandle handle, uint priority, out int error)
        {
            bool accepted = Native.SetPriorityClass(handle, priority);
            error = Marshal.GetLastWin32Error();
            return accepted;
        }
        public bool SetThrottling(SafeProcessHandle handle, ClientPowerThrottling throttling, out int error)
        {
            var state = new PowerThrottlingState { Version = PowerThrottlingVersion, ControlMask = throttling.Control, StateMask = throttling.State };
            bool accepted = Native.SetProcessInformation(handle, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>());
            error = Marshal.GetLastWin32Error();
            return accepted;
        }
        public bool SetMemoryPriority(SafeProcessHandle handle, uint memoryPriority, out int error)
        {
            var info = new MemoryPriorityInformation { MemoryPriority = memoryPriority };
            bool accepted = Native.SetProcessInformation(handle, ProcessMemoryPriority, ref info, (uint)Marshal.SizeOf<MemoryPriorityInformation>());
            error = Marshal.GetLastWin32Error();
            return accepted;
        }
        public bool SetAffinity(SafeProcessHandle handle, ulong affinity, out int error)
        {
            bool accepted = Native.SetProcessAffinityMask(handle, (nuint)affinity);
            error = Marshal.GetLastWin32Error();
            return accepted;
        }
        public bool IsInJob(SafeProcessHandle handle, SafeJobObjectHandle? job, out bool inJob)
            => Native.IsProcessInJob(handle, job?.DangerousGetHandle() ?? 0, out inJob);
        public SafeJobObjectHandle CreateJob(out int error)
        {
            // Unnamed and with no limits: only a handle can reach it, and closing Zenith's handle never ends a client
            // because KILL_ON_JOB_CLOSE is never set. The rate control is the only attribute Zenith ever sets on it.
            var job = Native.CreateJobObject(0, null);
            error = Marshal.GetLastWin32Error();
            return job;
        }
        public bool AssignToJob(SafeProcessHandle handle, SafeJobObjectHandle job, out int error)
        {
            // AssignProcessToJobObject needs PROCESS_SET_QUOTA and PROCESS_TERMINATE, which the operation's own handle does
            // not carry. While that handle is open Windows cannot give the PID to another process, so a second handle to the
            // same PID is the same process; it is used for this one call and closed. Zenith never terminates a client.
            uint pid = Native.GetProcessId(handle);
            if (pid == 0) { error = Marshal.GetLastWin32Error(); return false; }
            using var assignable = Native.OpenProcess(0x0100 | 0x0001, false, (int)pid);
            error = Marshal.GetLastWin32Error();
            if (assignable.IsInvalid) return false;
            bool accepted = Native.AssignProcessToJobObject(job, assignable);
            error = Marshal.GetLastWin32Error();
            return accepted;
        }
        public bool ReadCpuRate(SafeJobObjectHandle job, out ClientJobCpuRate rate)
        {
            var info = new CpuRateControlInformation();
            bool read = Native.QueryInformationJobObject(job, JobObjectCpuRateControlInformation, ref info, (uint)Marshal.SizeOf<CpuRateControlInformation>(), out _);
            rate = read ? new(info.ControlFlags, info.CpuRate) : ClientJobCpuRate.None;
            return read;
        }
        public bool SetCpuRate(SafeJobObjectHandle job, ClientJobCpuRate rate, out int error)
        {
            var info = new CpuRateControlInformation { ControlFlags = rate.ControlFlags, CpuRate = rate.CpuRate };
            bool accepted = Native.SetInformationJobObject(job, JobObjectCpuRateControlInformation, ref info, (uint)Marshal.SizeOf<CpuRateControlInformation>());
            error = Marshal.GetLastWin32Error();
            return accepted;
        }
    }

    private const int ProcessMemoryPriority = 0, ProcessPowerThrottling = 4; // PROCESS_INFORMATION_CLASS.
    private const uint PowerThrottlingVersion = 1; // PROCESS_POWER_THROTTLING_CURRENT_VERSION.
    private const int JobObjectCpuRateControlInformation = 15; // JOBOBJECTINFOCLASS.

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryPriorityInformation { public uint MemoryPriority; }
    /// <summary>JOBOBJECT_CPU_RATE_CONTROL_INFORMATION; the second field is the union, read as the hard-cap rate.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CpuRateControlInformation { public uint ControlFlags, CpuRate; }

    private static class Native
    {
        // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setpriorityclass
        // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getpriorityclass
        // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation
        // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocessinformation
        // https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-setprocessaffinitymask
        // https://learn.microsoft.com/windows/win32/procthread/process-security-and-access-rights
        // https://learn.microsoft.com/windows/win32/api/jobapi2/nf-jobapi2-createjobobjectw
        // https://learn.microsoft.com/windows/win32/api/jobapi2/nf-jobapi2-assignprocesstojobobject
        // https://learn.microsoft.com/windows/win32/api/jobapi2/nf-jobapi2-setinformationjobobject
        // https://learn.microsoft.com/windows/win32/api/jobapi/nf-jobapi-isprocessinjob
        // https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-jobobject_cpu_rate_control_information
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetProcessId(SafeProcessHandle process);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint length);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetPriorityClass(SafeProcessHandle process);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetPriorityClass(SafeProcessHandle process, uint priority);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessInformation(SafeProcessHandle process, int informationClass, ref PowerThrottlingState information, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessInformation(SafeProcessHandle process, int informationClass, ref MemoryPriorityInformation information, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetProcessInformation(SafeProcessHandle process, int informationClass, ref PowerThrottlingState information, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetProcessInformation(SafeProcessHandle process, int informationClass, ref MemoryPriorityInformation information, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessAffinityMask(SafeProcessHandle process, out nuint processMask, out nuint systemMask);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetProcessAffinityMask(SafeProcessHandle process, nuint processMask);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsProcessInJob(SafeProcessHandle process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeJobObjectHandle CreateJobObject(nint attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(SafeJobObjectHandle job, SafeProcessHandle process);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObject(SafeJobObjectHandle job, int informationClass, ref CpuRateControlInformation information, uint size, out uint returned);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(SafeJobObjectHandle job, int informationClass, ref CpuRateControlInformation information, uint size);
    }
}
