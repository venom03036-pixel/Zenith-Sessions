using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

/// <summary>
/// Real Windows calls on two test-owned cmd.exe helpers, never a Roblox client. Only the accepted executable name is the
/// helper's; rights, identity checks, readback, restore and Exit restoration are the shipped path. Each step is
/// confirmed by an independent readback through System.Diagnostics and the test's own GetProcessInformation call.
/// </summary>
public sealed class ClientProcessEfficiencyNativeTests
{
    [Fact]
    public async Task EfficiencyAndSpreadChangeTestOwnedHelpersAndRestoreAndExitReturnTheirOwnValues()
    {
        await using var first = await Helper.StartAsync();
        await using var second = await Helper.StartAsync();
        Helper[] helpers = [first, second];
        var originals = helpers.Select(helper => helper.ReadIndependently()).ToArray();
        SessionObservation[] sessions = helpers.Select(helper => helper.Session).ToArray();
        var control = new WindowsClientProcessPriorityControl(new NativeClientProcessPriorityAdapter(new InMemoryClientCpuCapLedger(), "cmd.exe"));
        try
        {
            var efficient = await control.ChangeAsync(sessions, ClientProcessPriorityAction.Efficiency, default);
            Assert.All(efficient.Results, row => Assert.Equal(ClientProcessPriorityStatus.Applied, row.Status));
            for (int index = 0; index < helpers.Length; index++)
            {
                var now = helpers[index].ReadIndependently();
                Assert.Equal(ProcessPriorityClass.BelowNormal, now.Priority);
                Assert.Equal(originals[index].ThrottleControl | 1, now.ThrottleControl);
                Assert.Equal(originals[index].ThrottleState | 1, now.ThrottleState);
                Assert.Equal(1u, now.MemoryPriority);
                Assert.Equal(originals[index].Affinity, now.Affinity);
            }

            var spread = await control.ChangeAsync(sessions, ClientProcessPriorityAction.SpreadCores, default);
            ulong system = (ulong)originals[0].SystemAffinity;
            if (BitOperations.PopCount(system) < 4)
                Assert.All(spread.Results, row => Assert.Equal(ClientProcessPriorityStatus.NotEnoughCores, row.Status));
            else
            {
                Assert.All(spread.Results, row => Assert.Equal(ClientProcessPriorityStatus.Applied, row.Status));
                ulong a = (ulong)first.ReadIndependently().Affinity, b = (ulong)second.ReadIndependently().Affinity;
                Assert.Equal(0ul, a & b); // Each helper has its own cores...
                Assert.Equal(system, a | b); // ...and together they cover every core.
                Assert.InRange(Math.Abs(BitOperations.PopCount(a) - BitOperations.PopCount(b)), 0, 1);
            }
            Assert.Equal(2, control.PendingRestoreCount);

            var restored = await control.RestoreAllAsync(default);
            Assert.All(restored.Results, row => Assert.Equal(ClientProcessPriorityStatus.Restored, row.Status));
            Assert.Equal(0, restored.PendingRestoreCount);
            Assert.Equal(originals, helpers.Select(helper => helper.ReadIndependently()));

            // Exit restoration: apply again, then dispose the control without an explicit restore.
            var again = await control.ChangeAsync(sessions, ClientProcessPriorityAction.Efficiency, default);
            Assert.All(again.Results, row => Assert.Equal(ClientProcessPriorityStatus.Applied, row.Status));
            Assert.NotEqual(originals[0], first.ReadIndependently());
        }
        finally { await control.DisposeAsync(); }
        Assert.Equal(0, control.PendingRestoreCount);
        Assert.Equal(originals, helpers.Select(helper => helper.ReadIndependently()));
    }

    private sealed record Observed(ProcessPriorityClass Priority, uint ThrottleControl, uint ThrottleState, uint MemoryPriority,
        nint Affinity, nint SystemAffinity);

    private sealed class Helper : IAsyncDisposable
    {
        private readonly Process _process;
        private Helper(Process process)
        {
            _process = process;
            Session = new(process.Id, process.StartTime.ToUniversalTime().Ticks, "Synthetic", 0, "", false, false, 0, 0, null,
                DateTimeOffset.UtcNow);
        }
        public SessionObservation Session { get; }

        public static Task<Helper> StartAsync()
        {
            // An idle command interpreter waiting on its redirected input: a few MB, no window, exits on "exit".
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
            start.ArgumentList.Add("/d"); start.ArgumentList.Add("/q"); start.ArgumentList.Add("/k");
            var process = Process.Start(start) ?? throw new InvalidOperationException("The test-owned helper did not start.");
            return Task.FromResult(new Helper(process));
        }

        public Observed ReadIndependently()
        {
            _process.Refresh();
            using var handle = OpenProcess(0x1000, false, _process.Id); // Query limited information only.
            Assert.False(handle.IsInvalid, $"The test could not open its own helper (error {Marshal.GetLastWin32Error()}).");
            var power = new PowerThrottlingState { Version = 1 };
            Assert.True(GetProcessInformation(handle, 4, ref power, (uint)Marshal.SizeOf<PowerThrottlingState>()),
                $"Power throttling query failed (error {Marshal.GetLastWin32Error()}).");
            var memory = new MemoryPriorityInformation();
            Assert.True(GetProcessInformation(handle, 0, ref memory, (uint)Marshal.SizeOf<MemoryPriorityInformation>()),
                $"Memory priority query failed (error {Marshal.GetLastWin32Error()}).");
            Assert.True(GetProcessAffinityMask(handle, out nint own, out nint system));
            Assert.Equal(_process.ProcessorAffinity, own);
            return new(_process.PriorityClass, power.ControlMask, power.StateMask, memory.MemoryPriority, own, system);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    await _process.StandardInput.WriteLineAsync("exit");
                    _process.StandardInput.Close();
                    try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (TimeoutException) { _process.Kill(); await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                }
            }
            finally { _process.Dispose(); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryPriorityInformation { public uint MemoryPriority; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(SafeProcessHandle process, int informationClass, ref PowerThrottlingState information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(SafeProcessHandle process, int informationClass, ref MemoryPriorityInformation information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessAffinityMask(SafeProcessHandle process, out nint processMask, out nint systemMask);
}
