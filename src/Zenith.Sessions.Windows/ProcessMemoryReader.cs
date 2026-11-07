using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Zenith.Sessions.Windows;

public sealed record ProcessMemoryReading(long WorkingSetBytes,long PrivateCommitBytes,long? PrivateWorkingSetBytes,uint PageFaultCount);

/// <summary>Short-lived query-only handles; never reads process memory contents.</summary>
public static class ProcessMemoryReader
{
    public static ProcessMemoryReading? Read(int processId,long expectedStartUtcTicks)
    {
        if(processId<=0 || expectedStartUtcTicks<=0) return null;
        using var handle=Native.OpenProcess(0x1000,false,processId);
        if(handle.IsInvalid || !SameStart(handle,expectedStartUtcTicks)) return null;
        var measured=ReadCounters(handle);
        return measured is not null && SameStart(handle,expectedStartUtcTicks) ? measured : null;
    }
    /// <summary>Counters for the process an already-open handle refers to. The caller owns identity checks.</summary>
    internal static ProcessMemoryReading? ReadCounters(SafeProcessHandle handle)
    {
        uint full=(uint)Marshal.SizeOf<Native.MemoryCounters>();
        var memory=new Native.MemoryCounters { Size=full };
        bool extended=Native.K32GetProcessMemoryInfo(handle,ref memory,full);
        if(!extended)
        {
            memory=new() { Size=(uint)(8+9*IntPtr.Size) };
            if(!Native.K32GetProcessMemoryInfo(handle,ref memory,memory.Size)) return null;
        }
        if((ulong)memory.WorkingSet>long.MaxValue || (ulong)memory.PrivateUsage>long.MaxValue
            || extended && (ulong)memory.PrivateWorkingSet>long.MaxValue) return null;
        return new((long)memory.WorkingSet,(long)memory.PrivateUsage,extended?(long)memory.PrivateWorkingSet:null,memory.PageFaultCount);
    }
    // GetProcessTimes' exit FILETIME is undefined while running; do not require it to be zero.
    private static bool SameStart(SafeProcessHandle handle,long expected)=>Native.GetExitCodeProcess(handle,out uint code) && code==259
        && Native.GetProcessTimes(handle,out long start,out _,out _,out _)
        && start>=0 && start<=DateTime.MaxValue.Ticks-DateTime.FromFileTimeUtc(0).Ticks
        && DateTime.FromFileTimeUtc(start).Ticks==expected;
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct MemoryCounters
        {
            internal uint Size,PageFaultCount;
            internal nuint PeakWorkingSet,WorkingSet,PeakPagedPool,PagedPool,PeakNonpagedPool,NonpagedPool,PagefileUsage,PeakPagefileUsage,PrivateUsage,PrivateWorkingSet;
            internal ulong SharedCommit;
        }
        [DllImport("kernel32.dll",SetLastError=true)] internal static extern SafeProcessHandle OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,int id);
        [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessTimes(SafeProcessHandle handle,out long created,out long exited,out long kernel,out long user);
        [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(SafeProcessHandle handle,out uint code);
        [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool K32GetProcessMemoryInfo(SafeProcessHandle handle,ref MemoryCounters counters,uint size);
    }
}
