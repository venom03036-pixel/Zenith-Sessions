using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Zenith.Sessions.Windows;

/// <summary>
/// The one rule that ties an open process handle to an observed process instance: its creation FILETIME, converted
/// exactly to UTC ticks, must equal the observed start. A reused PID has a different creation time, so it never matches.
/// Callers keep their own deliberate choices: the OpenProcess access mask (least privilege), the liveness test
/// (GetExitCodeProcess for query-only handles, WaitForSingleObject where SYNCHRONIZE is held) and the executable
/// allow-list (priority changes stay RobloxPlayerBeta.exe only, per SELECTED-PRIORITY-DESIGN.md).
/// </summary>
internal static class ProcessInstanceIdentity
{
    // Largest FILETIME that DateTime can represent; FromFileTimeUtc throws above it and below zero.
    private static readonly long LastFileTime = DateTime.MaxValue.Ticks - DateTime.FromFileTimeUtc(0).Ticks;

    /// <summary>UTC ticks for a creation FILETIME, or null when Windows returned a value DateTime cannot represent.</summary>
    internal static long? StartTicks(long fileTime) =>
        fileTime >= 0 && fileTime <= LastFileTime ? DateTime.FromFileTimeUtc(fileTime).Ticks : null;

    /// <summary>The same conversion for a FILETIME read as two DWORD halves (for example inside RM_UNIQUE_PROCESS).</summary>
    internal static long? StartTicks(uint low, uint high) => StartTicks(unchecked((long)(((ulong)high << 32) | low)));

    /// <summary>
    /// The handle's creation time in UTC ticks, or null when Windows cannot report it. Needs only
    /// PROCESS_QUERY_LIMITED_INFORMATION and says nothing about whether the process is still running.
    /// https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes
    /// </summary>
    internal static long? StartTicks(SafeProcessHandle process) =>
        GetProcessTimes(process, out long created, out _, out _, out _) ? StartTicks(created) : null;

    /// <summary>True only when the handle's creation time equals the observed start exactly.</summary>
    internal static bool MatchesStart(SafeProcessHandle process, long expectedStartUtcTicks) =>
        expectedStartUtcTicks > 0 && StartTicks(process) == expectedStartUtcTicks;

    // The exit FILETIME is undefined for a running process, so it is never used as a liveness test.
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
}
