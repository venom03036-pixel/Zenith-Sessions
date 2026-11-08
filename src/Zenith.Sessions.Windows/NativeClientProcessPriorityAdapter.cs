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
/// </summary>
internal readonly record struct ClientProcessSettings(uint Priority, ClientPowerThrottling? Throttling, uint? MemoryPriority, ulong? Affinity)
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
}

/// <summary>Each operation uses one exact process handle, with no elevation or process-memory access.</summary>
internal sealed class NativeClientProcessPriorityAdapter : IClientPriorityAdapter
{
    private readonly IClientPriorityNativeApi _native;
    private readonly string _clientImage;
    public NativeClientProcessPriorityAdapter() : this(new NativeApi()) { }
    /// <summary>The production Windows calls for a test-owned helper whose executable is <paramref name="clientImage"/>.</summary>
    internal NativeClientProcessPriorityAdapter(string clientImage) : this(new NativeApi(), clientImage) { }
    /// <param name="clientImage">The only executable name accepted; tests name their own helper.</param>
    internal NativeClientProcessPriorityAdapter(IClientPriorityNativeApi native, string clientImage = "RobloxPlayerBeta.exe")
    {
        _native = native;
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
            return error == 87 ? new(ClientProcessPriorityStatus.InvalidClient, Retired: true) : new(ErrorStatus(error));
        }
        return new(ClientProcessPriorityStatus.Unchanged, new Handle(_native, process, session, user, _clientImage));
    }

    private static ClientProcessPriorityStatus ErrorStatus(int error) => error == 5
        ? ClientProcessPriorityStatus.AccessDenied : ClientProcessPriorityStatus.NotConfirmed;

    private sealed class Handle(IClientPriorityNativeApi native, SafeProcessHandle process,
        SessionObservation expected, string user, string clientImage) : IClientPriorityHandle
    {
        public ClientPriorityRead Read()
        {
            if (!native.Read(process, out var current) || current is null)
                return new(ClientProcessPriorityStatus.NotConfirmed);
            if (!current.Running || current.ProcessId != expected.ProcessId || current.StartUtcTicks != expected.ProcessStartUtcTicks)
                return new(ClientProcessPriorityStatus.InvalidClient, Retired: current.ProcessId == expected.ProcessId
                    && (!current.Running || current.StartUtcTicks > 0 && current.StartUtcTicks != expected.ProcessStartUtcTicks));
            if (!string.Equals(current.UserSid, user, StringComparison.Ordinal))
                return new(ClientProcessPriorityStatus.AccessDenied);
            if (!string.Equals(Path.GetFileName(current.ImagePath), clientImage, StringComparison.OrdinalIgnoreCase))
                return new(ClientProcessPriorityStatus.InvalidClient);
            var settings = new ClientProcessSettings(current.Priority, current.Throttling, current.MemoryPriority, current.Affinity);
            return new(ClientProcessPriorityStatus.Unchanged, settings.Class, Settings: settings, SystemAffinity: current.SystemAffinity);
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
            if (desired.Class is not (ClientProcessPriorityClass.Normal or ClientProcessPriorityClass.BelowNormal) || desired.Affinity == 0)
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
                if (!native.SetAffinity(process, affinity, out error)) return new(accepted, ErrorStatus(error));
                accepted = true;
            }
            if (desired.MemoryPriority is { } memory && memory != now.MemoryPriority)
            {
                if (!native.SetMemoryPriority(process, memory, out error)) return new(accepted, ErrorStatus(error));
                accepted = true;
            }
            if (desired.Throttling is { } throttling && throttling != now.Throttling)
            {
                if (!native.SetThrottling(process, throttling, out error)) return new(accepted, ErrorStatus(error));
                accepted = true;
            }
            if (desired.Priority != now.Priority)
            {
                if (!native.Set(process, desired.Priority, out error)) return new(accepted, ErrorStatus(error));
                accepted = true;
            }
            return new(accepted, ClientProcessPriorityStatus.Applied);
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
    }

    private const int ProcessMemoryPriority = 0, ProcessPowerThrottling = 4; // PROCESS_INFORMATION_CLASS.
    private const uint PowerThrottlingVersion = 1; // PROCESS_POWER_THROTTLING_CURRENT_VERSION.

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryPriorityInformation { public uint MemoryPriority; }

    private static class Native
    {
        // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setpriorityclass
        // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getpriorityclass
        // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation
        // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocessinformation
        // https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-setprocessaffinitymask
        // https://learn.microsoft.com/windows/win32/procthread/process-security-and-access-rights
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
    }
}
