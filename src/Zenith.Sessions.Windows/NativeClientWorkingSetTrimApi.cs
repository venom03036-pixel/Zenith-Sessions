using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

internal sealed record ClientTrimNativeSnapshot(int ProcessId, long StartUtcTicks, bool Running,
    string ImagePath, string UserSid, ClientMemoryReading? Memory);
internal interface IClientTrimNativeApi
{
    string? CurrentUserSid { get; }
    SafeProcessHandle Open(int processId, out int error);
    bool Read(SafeProcessHandle process, out ClientTrimNativeSnapshot? snapshot);
    bool Trim(SafeProcessHandle process, out int error);
}

/// <summary>
/// One exact process handle per client with query-limited, set-quota and synchronize rights only:
/// no process-memory read or write access, and no elevation. Counters come from K32GetProcessMemoryInfo.
/// </summary>
internal sealed class NativeClientWorkingSetTrimApi : IClientTrimNativeApi
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
        var handle = Native.OpenProcess(0x1000 | 0x0100 | 0x00100000, false, processId); // Query limited, set quota, synchronize.
        error = Marshal.GetLastWin32Error();
        return handle;
    }

    public bool Read(SafeProcessHandle process, out ClientTrimNativeSnapshot? snapshot)
    {
        snapshot = null;
        uint pid = Native.GetProcessId(process);
        if (pid == 0 || pid > int.MaxValue || !Native.GetProcessTimes(process, out long created, out _, out _, out _)
            || created < 0 || created > DateTime.MaxValue.Ticks - DateTime.FromFileTimeUtc(0).Ticks)
            return false;
        long start = DateTime.FromFileTimeUtc(created).Ticks;
        uint wait = Native.WaitForSingleObject(process, 0);
        if (wait == 0) { snapshot = new((int)pid, start, false, "", "", null); return true; }
        if (wait != 258) return false;
        var image = new StringBuilder(1024);
        uint length = (uint)image.Capacity;
        if (!Native.QueryFullProcessImageName(process, 0, image, ref length)) return false;
        string? sid = ProcessTokenUser.Read(process);
        if (sid is null || ProcessMemoryReader.ReadCounters(process) is not { } memory) return false;
        // A signalled process is conclusively retired. A failed wait remains unknown.
        wait = Native.WaitForSingleObject(process, 0);
        if (wait is not (0 or 258)) return false;
        snapshot = new((int)pid, start, wait == 258, image.ToString(), sid,
            new(memory.PrivateWorkingSetBytes, memory.PrivateCommitBytes));
        return true;
    }

    public bool Trim(SafeProcessHandle process, out int error)
    {
        bool accepted = Native.K32EmptyWorkingSet(process);
        error = Marshal.GetLastWin32Error();
        return accepted;
    }

    private static class Native
    {
        // https://learn.microsoft.com/windows/win32/api/psapi/nf-psapi-emptyworkingset
        // https://learn.microsoft.com/windows/win32/procthread/process-security-and-access-rights
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetProcessId(SafeProcessHandle process);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool K32EmptyWorkingSet(SafeProcessHandle process);
    }
}
