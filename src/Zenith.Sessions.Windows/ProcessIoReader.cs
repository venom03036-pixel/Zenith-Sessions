using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// Short-lived, query-limited process accounting only. No process-memory contents, VM_READ,
/// elevation, file paths, window reads or background work. Failure returns no invented counters.
/// </summary>
public static class ProcessIoReader
{
    public static ProcessIoCounters? Read(int processId, long expectedStartUtcTicks, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (processId <= 0 || expectedStartUtcTicks <= 0 || expectedStartUtcTicks > DateTime.MaxValue.Ticks) return null;
        // Microsoft documents PROCESS_QUERY_LIMITED_INFORMATION for all four query APIs below.
        // https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-getprocessiocounters
        // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes
        // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocessid
        // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getexitcodeprocess
        using var handle = Native.OpenProcess(0x1000, false, processId);
        ct.ThrowIfCancellationRequested();
        if (handle.IsInvalid || !SameInstance(handle, processId, expectedStartUtcTicks)) return null;
        ct.ThrowIfCancellationRequested();
        bool read = Native.GetProcessIoCounters(handle, out var counters);
        ct.ThrowIfCancellationRequested();
        if (!read || !SameInstance(handle, processId, expectedStartUtcTicks)) return null;
        ct.ThrowIfCancellationRequested();
        return new(counters.ReadTransferCount, counters.WriteTransferCount, counters.OtherTransferCount,
            counters.ReadOperationCount, counters.WriteOperationCount, counters.OtherOperationCount);
    }

    private static bool SameInstance(SafeProcessHandle handle, int processId, long expectedStartUtcTicks)
    {
        if (Native.GetProcessId(handle) != (uint)processId || !Native.GetExitCodeProcess(handle, out uint exitCode)
            || exitCode != 259 || !Native.GetProcessTimes(handle, out long start, out _, out _, out _)) return false;
        // FILETIME starts in1601. Range-check before converting untrusted native integer output.
        // Exit FILETIME is undefined for a running process, so it is not used as a live/exit test.
        return start >= 0 && start <= DateTime.MaxValue.Ticks - DateTime.FromFileTimeUtc(0).Ticks
            && DateTime.FromFileTimeUtc(start).Ticks == expectedStartUtcTicks;
    }

    private static class Native
    {
        // https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-io_counters
        [StructLayout(LayoutKind.Sequential)] internal struct IoCounters
        {
            internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            internal ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint GetProcessId(SafeProcessHandle process);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    }
}
