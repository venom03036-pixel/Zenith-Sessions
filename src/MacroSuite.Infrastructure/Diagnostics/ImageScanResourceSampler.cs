using System.Diagnostics;
using System.Runtime.InteropServices;
using MacroSuite.Core.Vision;

namespace MacroSuite.Infrastructure.Diagnostics;

/// <summary>One process-wide sample per second; no timer or extra capture owner.</summary>
internal static class ImageScanResourceSampler
{
    private static readonly object Gate = new();
    private static long _lastSample;
    private static TimeSpan _lastCpu;
    private static bool _initialized;
    public static void Sample(int interval, System.Drawing.Size size)
    {
        lock (Gate)
        {
            long now = Stopwatch.GetTimestamp();
            double seconds = (now - _lastSample) / (double)Stopwatch.Frequency;
            if (_initialized && seconds < 1) return;
            using var process = Process.GetCurrentProcess();
            TimeSpan cpu = process.TotalProcessorTime;
            double? percentage = _initialized ? Math.Clamp((cpu - _lastCpu).TotalSeconds / seconds / Environment.ProcessorCount * 100, 0, 100) : null;
            _lastSample = now; _lastCpu = cpu; _initialized = true;
            ImageScanBudget.Shared.Observe(percentage, GpuUsage.TrySample(), interval, size);
        }
    }

    // PDH needs two samples. A process-wide query is disposed at process exit, and holds only GPU counters.
    private static class GpuUsage
    {
        private static IntPtr _query, _counter;
        private static bool _attempted;
        private const uint MoreData = 0x800007D2;
        private const uint DoubleFormat = 0x200;
        [StructLayout(LayoutKind.Sequential)] private struct Value { public uint Status; public double Number; }
        [StructLayout(LayoutKind.Sequential)] private struct Item { public IntPtr Name; public Value Value; }
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, nuint userData, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, nuint userData, out IntPtr counter);
        [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr buffer);
        [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);

        public static double? TrySample()
        {
            if (!_attempted)
            {
                _attempted = true;
                if (!OperatingSystem.IsWindows() || PdhOpenQueryW(null, 0, out _query) != 0) return null;
                if (PdhAddEnglishCounterW(_query, $"\\GPU Engine(pid_{Environment.ProcessId}_*)\\Utilization Percentage", 0, out _counter) != 0)
                { PdhCloseQuery(_query); _query = IntPtr.Zero; return null; }
                AppDomain.CurrentDomain.ProcessExit += (_, _) => { if (_query != IntPtr.Zero) PdhCloseQuery(_query); _query = IntPtr.Zero; };
                PdhCollectQueryData(_query);
                return null;
            }
            if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return null;
            uint bytes = 0;
            if (PdhGetFormattedCounterArrayW(_counter, DoubleFormat, ref bytes, out _, IntPtr.Zero) != MoreData || bytes > 1024 * 1024) return null;
            IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
            try
            {
                if (PdhGetFormattedCounterArrayW(_counter, DoubleFormat, ref bytes, out uint count, buffer) != 0) return null;
                double total = 0; bool valid = false; int itemSize = Marshal.SizeOf<Item>();
                for (int i = 0; i < Math.Min(count, bytes / itemSize); i++)
                {
                    Item item = Marshal.PtrToStructure<Item>(IntPtr.Add(buffer, i * itemSize));
                    if (item.Value.Status <= 1 && double.IsFinite(item.Value.Number)) { total += item.Value.Number; valid = true; }
                }
                return valid ? Math.Clamp(total, 0, 100) : null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
}
