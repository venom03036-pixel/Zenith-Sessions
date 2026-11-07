using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

/// <summary>
/// The one PID-plus-creation-time rule shared by the IO, memory, window, log-probe, Restart Manager and
/// priority adapters. Only the executing test process is queried; no Roblox client is opened or changed.
/// </summary>
public sealed class ProcessInstanceIdentityTests
{
    private static readonly long LastFileTime = DateTime.MaxValue.Ticks - DateTime.FromFileTimeUtc(0).Ticks;

    [Fact]
    public void StartTicksRejectsOutOfRangeAndConvertsExactFileTime()
    {
        Assert.Null(ProcessInstanceIdentity.StartTicks(-1));
        Assert.Null(ProcessInstanceIdentity.StartTicks(long.MinValue));
        Assert.Null(ProcessInstanceIdentity.StartTicks(LastFileTime + 1));
        Assert.Null(ProcessInstanceIdentity.StartTicks(long.MaxValue));
        Assert.Equal(DateTime.FromFileTimeUtc(0).Ticks, ProcessInstanceIdentity.StartTicks(0));
        Assert.Equal(DateTime.MaxValue.Ticks, ProcessInstanceIdentity.StartTicks(LastFileTime));
        long now = DateTime.UtcNow.ToFileTimeUtc();
        Assert.Equal(DateTime.FromFileTimeUtc(now).Ticks, ProcessInstanceIdentity.StartTicks(now));
    }

    [Fact]
    public void StartTicksCombinesNativeFileTimeHalvesWithTheSameRangeRule()
    {
        long now = DateTime.UtcNow.ToFileTimeUtc();
        uint low = unchecked((uint)now), high = (uint)(now >>> 32);
        Assert.Equal(DateTime.FromFileTimeUtc(now).Ticks, ProcessInstanceIdentity.StartTicks(low, high));
        // A set top bit is a negative FILETIME, which Windows never reports for a creation time.
        Assert.Null(ProcessInstanceIdentity.StartTicks(uint.MaxValue, uint.MaxValue));
        Assert.Null(ProcessInstanceIdentity.StartTicks(0, 0x8000_0000));
        Assert.Null(ProcessInstanceIdentity.StartTicks(unchecked((uint)(LastFileTime + 1)), (uint)((LastFileTime + 1) >>> 32)));
    }

    [Fact]
    public void MatchesStartTiesAQueryLimitedHandleToItsExactCreationTime()
    {
        using var current = Process.GetCurrentProcess();
        long start = current.StartTime.ToUniversalTime().Ticks;
        using var handle = OpenProcess(0x1000, false, current.Id); // QUERY_LIMITED_INFORMATION, as the IO, memory and log readers use.
        Assert.False(handle.IsInvalid);
        Assert.Equal(start, ProcessInstanceIdentity.StartTicks(handle));
        Assert.True(ProcessInstanceIdentity.MatchesStart(handle, start));
        Assert.False(ProcessInstanceIdentity.MatchesStart(handle, start + 1));
        Assert.False(ProcessInstanceIdentity.MatchesStart(handle, start - 1));
        Assert.False(ProcessInstanceIdentity.MatchesStart(handle, 0));
        Assert.False(ProcessInstanceIdentity.MatchesStart(handle, -1));
    }

    [Fact]
    public void NoHandleHasNoStartTime()
    {
        using var none = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);
        Assert.Null(ProcessInstanceIdentity.StartTicks(none));
        Assert.False(ProcessInstanceIdentity.MatchesStart(none, DateTime.UtcNow.Ticks));
    }

    [Fact]
    public void PriorityNativeReadKeepsExactStartIdentityAndItsRobloxPlayerBetaOnlyGuard()
    {
        // Read-only: the adapter opens the test host, reads identity, user and priority, and never sets priority.
        using var current = Process.GetCurrentProcess();
        long start = current.StartTime.ToUniversalTime().Ticks;
        var session = new SessionObservation(current.Id, start, "Synthetic", 0, "Synthetic", false, true, 0, 0, null, DateTimeOffset.UtcNow);
        var adapter = new NativeClientProcessPriorityAdapter();

        var exact = adapter.Open(session);
        using (exact.Handle)
        {
            Assert.NotNull(exact.Handle);
            var read = exact.Handle!.Read();
            // Exact PID/start and same user pass; the test host is not RobloxPlayerBeta.exe, so it is rejected but not retired.
            Assert.Equal(ClientProcessPriorityStatus.InvalidClient, read.Status);
            Assert.False(read.Retired);
        }

        var later = adapter.Open(session with { ProcessStartUtcTicks = start + 1 });
        using (later.Handle)
        {
            Assert.NotNull(later.Handle);
            var read = later.Handle!.Read();
            // Same PID with a different creation time is a different instance: retired, never treated as the saved one.
            Assert.Equal(ClientProcessPriorityStatus.InvalidClient, read.Status);
            Assert.True(read.Retired);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
}
