using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

internal sealed record ClientPriorityNativeSnapshot(int ProcessId, long StartUtcTicks, bool Running,
    string ImagePath, string UserSid, uint Priority);
internal interface IClientPriorityNativeApi
{
    string? CurrentUserSid { get; }
    SafeProcessHandle Open(int processId, out int error);
    bool Read(SafeProcessHandle handle, out ClientPriorityNativeSnapshot? snapshot);
    bool Set(SafeProcessHandle handle, uint priority, out int error);
}

/// <summary>Each operation uses one exact process handle, with no elevation or process-memory access.</summary>
internal sealed class NativeClientProcessPriorityAdapter : IClientPriorityAdapter
{
    private readonly IClientPriorityNativeApi _native;
    public NativeClientProcessPriorityAdapter() : this(new NativeApi()) { }
    internal NativeClientProcessPriorityAdapter(IClientPriorityNativeApi native) => _native = native;

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
        return new(ClientProcessPriorityStatus.Unchanged, new Handle(_native, process, session, user));
    }

    private static ClientProcessPriorityStatus ErrorStatus(int error) => error == 5
        ? ClientProcessPriorityStatus.AccessDenied : ClientProcessPriorityStatus.NotConfirmed;

    private sealed class Handle(IClientPriorityNativeApi native, SafeProcessHandle process,
        SessionObservation expected, string user) : IClientPriorityHandle
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
            if (!string.Equals(Path.GetFileName(current.ImagePath), "RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase))
                return new(ClientProcessPriorityStatus.InvalidClient);
            return new(ClientProcessPriorityStatus.Unchanged, current.Priority switch
            {
                0x20 => ClientProcessPriorityClass.Normal,
                0x4000 => ClientProcessPriorityClass.BelowNormal,
                _ => ClientProcessPriorityClass.Unsupported
            });
        }

        public ClientPriorityWrite Change(ClientProcessPriorityClass expectedPriority, ClientProcessPriorityClass desired, Func<bool> stopped)
        {
            var current = Read();
            if (current.Status != ClientProcessPriorityStatus.Unchanged || current.Current is null)
                return new(false, current.Status, current.Current, current.Retired);
            if (current.Current is not (ClientProcessPriorityClass.Normal or ClientProcessPriorityClass.BelowNormal))
                return new(false, ClientProcessPriorityStatus.UnsupportedPriority, current.Current);
            if (current.Current != expectedPriority)
                return new(false, ClientProcessPriorityStatus.Conflict, current.Current);
            if (desired is not (ClientProcessPriorityClass.Normal or ClientProcessPriorityClass.BelowNormal))
                return new(false, ClientProcessPriorityStatus.UnsupportedPriority, current.Current);
            if (stopped()) return new(false, ClientProcessPriorityStatus.Cancelled, current.Current);
            // Windows has no compare-and-set for priority. This refuses an OBSERVED conflict.
            // Same-handle identity, priority and cancellation were checked immediately before set.
            bool accepted = native.Set(process, desired == ClientProcessPriorityClass.Normal ? 0x20u : 0x4000u, out int error);
            return new(accepted, accepted ? ClientProcessPriorityStatus.Applied : ErrorStatus(error));
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
            var handle = Native.OpenProcess(0x1000 | 0x0200 | 0x00100000, false, processId); // Query limited, set information, synchronize.
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
            string? sid = ReadUserSid(process);
            if (sid is null) return false;
            uint priority = Native.GetPriorityClass(process);
            if (priority == 0) return false;
            // A signalled process is conclusively retired. A failed wait remains unknown.
            wait = Native.WaitForSingleObject(process, 0);
            if (wait is not (0 or 258)) return false;
            snapshot = new((int)pid, start, wait == 258, image.ToString(), sid, priority);
            return true;
        }
        public bool Set(SafeProcessHandle handle, uint priority, out int error)
        {
            bool accepted = Native.SetPriorityClass(handle, priority);
            error = Marshal.GetLastWin32Error();
            return accepted;
        }

        private static string? ReadUserSid(SafeProcessHandle process)
        {
            if (!Native.OpenProcessToken(process, 0x0008, out var token)) return null; // TOKEN_QUERY only.
            using (token)
            {
                Native.GetTokenInformation(token, 1, 0, 0, out uint bytes); // TokenUser.
                if (bytes is 0 or > 65536) return null;
                nint buffer = Marshal.AllocHGlobal((int)bytes);
                try
                {
                    if (!Native.GetTokenInformation(token, 1, buffer, bytes, out uint written) || written > bytes) return null;
                    nint sid = Marshal.ReadIntPtr(buffer);
                    return sid == 0 ? null : new SecurityIdentifier(sid).Value;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
    }

    private static class Native
    {
        // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setpriorityclass
        // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getpriorityclass
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
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, nint buffer, uint bytes, out uint written);
    }
}
