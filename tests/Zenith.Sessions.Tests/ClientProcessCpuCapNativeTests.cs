using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

/// <summary>
/// Real Windows job calls on one test-owned cmd.exe helper, never a Roblox client. Only the accepted executable name is the
/// helper's; the job, its rate control, the held handle, the record and the restart check are the shipped path. Each step is
/// confirmed by the test's own IsProcessInJob and QueryInformationJobObject calls through the handle the adapter holds, and
/// through the test's own duplicate of it after the adapter is gone. The test host may itself run in a job (terminals and
/// test hosts often do), so the helper is started with CREATE_BREAKAWAY_FROM_JOB or, when the host's job forbids that, as a
/// child of the user's own explorer.exe (PROC_THREAD_ATTRIBUTE_PARENT_PROCESS), which is in no job. Only when neither works
/// does the helper inherit the job, and the test then checks the owner's rule instead: already in a job means unsupported.
/// </summary>
public sealed class ClientProcessCpuCapNativeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CapPutsATestOwnedHelperInAJobOnlyThisRunCanReachAndARestartNamesWhatACrashLeaves()
    {
        await using var helper = Helper.Start();
        output.WriteLine(helper.Route);
        string folder = Path.Combine(Path.GetTempPath(), "zenith-cpu-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string recordPath = Path.Combine(folder, WindowsClientProcessPriorityControl.CpuCapRecordFileName);
            var firstAdapter = new NativeClientProcessPriorityAdapter(new ClientCpuCapLedgerFile(recordPath), "cmd.exe");
            var first = new WindowsClientProcessPriorityControl(firstAdapter);
            var capped = await first.CapCpuAsync([helper.Session], 25, default);
            var row = Assert.Single(capped.Results);
            if (helper.InheritedAJob)
            {
                output.WriteLine("The helper inherited the host's job, so only the owner's already-in-a-job rule was checked live.");
                Assert.Equal(ClientProcessPriorityStatus.AlreadyInJob, row.Status);
                Assert.False(File.Exists(recordPath));
                Assert.Equal(0, first.PendingRestoreCount);
                await first.DisposeAsync();
                return;
            }
            Assert.Equal(ClientProcessPriorityStatus.Applied, row.Status);
            Assert.Equal(1, first.PendingRestoreCount);
            Assert.Equal([helper.Session.ProcessId], ReadRecordedPids(recordPath));
            var job = firstAdapter.HeldJob(helper.Session.ProcessId, helper.Session.ProcessStartUtcTicks);
            Assert.NotNull(job);
            var seen = helper.QueryIndependently(job);
            Assert.True(seen.InJob);
            Assert.Equal(0x5u, seen.ControlFlags); // JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | HARD_CAP.
            Assert.Equal(2500u, seen.CpuRate);
            Assert.Equal(ProcessPriorityClass.Normal, helper.PriorityClass); // The cap changes nothing else.
            output.WriteLine($"Capped helper PID {helper.Session.ProcessId} at 25% through the job this run holds.");

            // A restarted Zenith (the first control is still alive here, standing in for the crashed run's leftover) names the
            // client from its record, cannot reach that run's job, and so cannot cap or clear it: the cap is untouched.
            var second = new WindowsClientProcessPriorityControl(new NativeClientProcessPriorityAdapter(new ClientCpuCapLedgerFile(recordPath), "cmd.exe"));
            try
            {
                Assert.Equal(new ClientCpuCapLeftovers(1, 0, false, false), await second.CheckLeftoverCpuCapsAsync(default));
                Assert.Equal(ClientProcessPriorityStatus.AlreadyInJob, Assert.Single((await second.CapCpuAsync([helper.Session], 10, default)).Results).Status);
                Assert.Equal(0, second.PendingRestoreCount);
                Assert.Equal(2500u, helper.QueryIndependently(job).CpuRate);
                Assert.Equal([helper.Session.ProcessId], ReadRecordedPids(recordPath));
            }
            finally { await second.DisposeAsync(); }

            // The run that holds the job clears the cap, and the client stays in the now inert job; a new cap reuses that job.
            var restored = await first.RestoreAllAsync(default);
            Assert.Equal(ClientProcessPriorityStatus.Restored, Assert.Single(restored.Results).Status);
            Assert.Equal(0, restored.PendingRestoreCount);
            seen = helper.QueryIndependently(job);
            Assert.True(seen.InJob);
            Assert.Equal(0u, seen.ControlFlags);
            Assert.Empty(ReadRecordedPids(recordPath));
            var again = await first.CapCpuAsync([helper.Session], 10, default);
            Assert.Equal(ClientProcessPriorityStatus.Applied, Assert.Single(again.Results).Status);
            Assert.Same(job, firstAdapter.HeldJob(helper.Session.ProcessId, helper.Session.ProcessStartUtcTicks));
            seen = helper.QueryIndependently(job);
            Assert.Equal(0x5u, seen.ControlFlags);
            Assert.Equal(1000u, seen.CpuRate);
            Assert.Equal([helper.Session.ProcessId], ReadRecordedPids(recordPath));

            // "Crash" with the cap on: the run never restores. The test keeps its own duplicate of the handle so it can still
            // look at the job afterwards; nothing else can. Once the helper exits, the next start drops the record.
            using var duplicate = Duplicate(job);
            await helper.DisposeAsync();
            var third = new WindowsClientProcessPriorityControl(new NativeClientProcessPriorityAdapter(new ClientCpuCapLedgerFile(recordPath), "cmd.exe"));
            try
            {
                // Windows reaps a terminated process a moment after its last handle closes; until then the check still sees it.
                // The capability is that the record is dropped once the client is gone, so the test waits for that, not an instant.
                ClientCpuCapLeftovers leftovers = default!;
                for (int attempt = 0; attempt < 50; attempt++)
                {
                    leftovers = await third.CheckLeftoverCpuCapsAsync(default);
                    if (ReadRecordedPids(recordPath).Count == 0) break;
                    await Task.Delay(100);
                }
                Assert.Equal(new ClientCpuCapLeftovers(0, 0, false, false), leftovers);
                Assert.Empty(ReadRecordedPids(recordPath));
            }
            finally { await third.DisposeAsync(); }
            // The crashed run's own Exit finds the client gone and retires its ownership; its job keeps the cap it could not clear.
            await first.DisposeAsync();
            Assert.Equal(0, first.PendingRestoreCount);
            Assert.Null(firstAdapter.HeldJob(helper.Session.ProcessId, helper.Session.ProcessStartUtcTicks));
            Assert.Equal(1000u, QueryRate(duplicate).CpuRate);
            output.WriteLine("The restart named the capped client and could not reach its job; Restore and a re-cap went through the held handle; the record was dropped once the helper exited.");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static IReadOnlyList<int> ReadRecordedPids(string recordPath)
    {
        if (!File.Exists(recordPath)) return [];
        using var document = JsonDocument.Parse(File.ReadAllBytes(recordPath));
        Assert.Equal(1, document.RootElement.GetProperty("schema").GetInt32());
        return document.RootElement.GetProperty("caps").EnumerateArray().Select(row => row.GetProperty("processId").GetInt32()).ToArray();
    }

    private static SafeJobObjectHandle Duplicate(SafeJobObjectHandle job)
    {
        using var self = Process.GetCurrentProcess();
        Assert.True(DuplicateHandle(self.Handle, job.DangerousGetHandle(), self.Handle, out var duplicate, 0, false, 0x2 /* DUPLICATE_SAME_ACCESS */),
            $"The test could not duplicate the job handle (error {Marshal.GetLastWin32Error()}).");
        return duplicate;
    }

    private static (uint ControlFlags, uint CpuRate) QueryRate(SafeJobObjectHandle job)
    {
        var rate = new CpuRateControlInformation();
        Assert.True(QueryInformationJobObject(job, 15, ref rate, (uint)Marshal.SizeOf<CpuRateControlInformation>(), out _),
            $"CPU rate query failed (error {Marshal.GetLastWin32Error()}).");
        return (rate.ControlFlags, rate.CpuRate);
    }

    private sealed record Seen(bool InJob, uint ControlFlags, uint CpuRate);

    private sealed class Helper : IAsyncDisposable
    {
        private readonly Process _process;
        private bool _disposed;
        private Helper(Process process, bool inheritedAJob, string route)
        {
            _process = process;
            InheritedAJob = inheritedAJob;
            Route = route;
            Session = new(process.Id, process.StartTime.ToUniversalTime().Ticks, "Synthetic", 0, "", false, false, 0, 0, null,
                DateTimeOffset.UtcNow);
        }
        public SessionObservation Session { get; }
        /// <summary>True when no route out of the host's job worked, so the helper is in a job Zenith did not create.</summary>
        public bool InheritedAJob { get; }
        public string Route { get; }
        public ProcessPriorityClass PriorityClass { get { _process.Refresh(); return _process.PriorityClass; } }

        public static Helper Start()
        {
            // An idle command interpreter with a hidden console and nothing to read: a few MB, no window, ended by Kill.
            // CREATE_BREAKAWAY_FROM_JOB has no effect when the host is in no job, and is refused (error 5) by a job that
            // forbids breakaway; then the helper is created with the user's own explorer.exe as its parent, so it inherits
            // explorer's job membership (none) instead of the host's. Only if that fails too does it inherit the host's job.
            string route = "Started the helper with CREATE_BREAKAWAY_FROM_JOB.";
            var started = Create(0x01000000 | 0x08000000, 0, out int error);
            if (started is null)
            {
                Assert.Equal(5, error);
                using var explorer = OpenExplorerOutsideAnyJob();
                if (explorer is not null)
                {
                    started = Create(0x08000000, explorer.DangerousGetHandle(), out error);
                    route = started is null ? $"Breakaway refused and reparenting to explorer.exe failed (error {error})."
                        : "Started the helper as a child of explorer.exe, outside the host's job.";
                }
                else route = "Breakaway refused and no explorer.exe outside a job was found.";
                started ??= Create(0x08000000, 0, out error);
            }
            Assert.NotNull(started);
            var process = Process.GetProcessById((int)started.Value.dwProcessId);
            using var query = OpenProcess(0x1000, false, process.Id);
            Assert.True(IsProcessInJob(query, 0, out bool inAJob));
            return new(process, inAJob, route);
        }

        /// <summary>The current user's explorer.exe in this session, opened for PROCESS_CREATE_PROCESS, when it is in no job.</summary>
        private static SafeProcessHandle? OpenExplorerOutsideAnyJob()
        {
            using var self = Process.GetCurrentProcess();
            int session = self.SessionId;
            foreach (var candidate in Process.GetProcessesByName("explorer"))
            {
                using (candidate)
                {
                    if (candidate.SessionId != session) continue;
                    var handle = OpenProcess(0x0080 | 0x1000, false, candidate.Id);
                    if (handle.IsInvalid) { handle.Dispose(); continue; }
                    if (IsProcessInJob(handle, 0, out bool inAJob) && !inAJob) return handle;
                    handle.Dispose();
                }
            }
            return null;
        }

        private static ProcessInformation? Create(uint flags, nint parent, out int error)
        {
            var startup = new StartupInfoEx { StartupInfo = { cb = (uint)Marshal.SizeOf<StartupInfoEx>() } };
            var command = new StringBuilder("\"" + Path.Combine(Environment.SystemDirectory, "cmd.exe") + "\" /d /q /k");
            nint attributes = 0, parentValue = 0;
            try
            {
                if (parent != 0)
                {
                    nuint size = 0;
                    InitializeProcThreadAttributeList(0, 1, 0, ref size);
                    attributes = Marshal.AllocHGlobal((nint)size);
                    parentValue = Marshal.AllocHGlobal(nint.Size);
                    Marshal.WriteIntPtr(parentValue, parent);
                    if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)
                        || !UpdateProcThreadAttribute(attributes, 0, 0x00020000, parentValue, (nuint)nint.Size, 0, 0)) // PROC_THREAD_ATTRIBUTE_PARENT_PROCESS.
                    {
                        error = Marshal.GetLastWin32Error();
                        return null;
                    }
                    startup.AttributeList = attributes;
                    flags |= 0x00080000; // EXTENDED_STARTUPINFO_PRESENT.
                }
                if (!CreateProcessW(null, command, 0, 0, false, flags, 0, null, ref startup, out var information))
                {
                    error = Marshal.GetLastWin32Error();
                    return null;
                }
                error = 0;
                CloseHandle(information.hThread);
                CloseHandle(information.hProcess);
                return information;
            }
            finally
            {
                if (attributes != 0) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
                if (parentValue != 0) Marshal.FreeHGlobal(parentValue);
            }
        }

        public Seen QueryIndependently(SafeJobObjectHandle job)
        {
            using var process = OpenProcess(0x1000, false, _process.Id);
            Assert.False(process.IsInvalid, $"The test could not open its own helper (error {Marshal.GetLastWin32Error()}).");
            Assert.True(IsProcessInJob(process, job.DangerousGetHandle(), out bool inJob));
            var (flags, rate) = QueryRate(job);
            return new(inJob, flags, rate);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            finally { _process.Dispose(); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CpuRateControlInformation { public uint ControlFlags, CpuRate; }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo StartupInfo; public nint AttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public nint hProcess, hThread; public uint dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? applicationName, StringBuilder commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment, string? currentDirectory,
        ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint attributeList, int attributeCount, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint attributeList, uint flags, nuint attribute, nint value, nuint size, nint previousValue, nint returnSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint attributeList);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeJobObjectHandle job, int informationClass, ref CpuRateControlInformation information, uint size, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out SafeJobObjectHandle target, uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
}
