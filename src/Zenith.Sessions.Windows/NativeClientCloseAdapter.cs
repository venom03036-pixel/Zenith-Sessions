using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// The Windows calls behind <see cref="ClientCloseOperations"/>. A handle is opened with the least rights the operation
/// needs (query limited and synchronize; plus terminate for the force path only), and is kept only when the process is
/// alive, its creation time equals the observed start exactly and its executable is an accepted client. The close post goes
/// through the same window identity check as every other window action. Exit waits are registered waits on a duplicate of
/// the process handle, not polls. Only the executable allow-list is a parameter, so a test can name its own helper;
/// production uses the Roblox list.
/// </summary>
internal sealed class NativeClientCloseAdapter(IReadOnlyList<string> allowedExecutables) : IClientCloseAdapter
{
    // https://learn.microsoft.com/windows/win32/procthread/process-security-and-access-rights
    private const uint QueryLimited = 0x1000, Synchronize = 0x00100000, Terminate = 0x0001;
    private const uint WaitTimeout = 258, WaitSignaled = 0;
    private readonly IReadOnlyList<string> _allowedExecutables = allowedExecutables ?? throw new ArgumentNullException(nameof(allowedExecutables));

    public NativeClientCloseAdapter() : this(NativeClientWindowAdapter.ClientExecutables) { }

    public IClientCloseHandle? Open(SessionObservation session, bool forTermination)
    {
        if (session.ProcessId <= 0 || session.ProcessStartUtcTicks <= 0) return null;
        var process = Native.OpenProcess(QueryLimited | Synchronize | (forTermination ? Terminate : 0), false, session.ProcessId);
        if (process.IsInvalid) { process.Dispose(); return null; }
        if (Native.WaitForSingleObject(process, 0) != WaitTimeout || !ProcessInstanceIdentity.MatchesStart(process, session.ProcessStartUtcTicks)
            || !AcceptedExecutable(process))
        { process.Dispose(); return null; }
        try { return new Handle(process, session.ProcessId, _allowedExecutables); }
        catch (Win32Exception) { process.Dispose(); return null; }
    }

    private bool AcceptedExecutable(SafeProcessHandle process)
    {
        var image = new StringBuilder(1024); uint length = (uint)image.Capacity;
        if (!Native.QueryFullProcessImageName(process, 0, image, ref length)) return false;
        string name = Path.GetFileName(image.ToString());
        return _allowedExecutables.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Handle : IClientCloseHandle
    {
        private readonly SafeProcessHandle _process;
        private readonly ProcessWaitHandle _wait;
        private readonly int _processId;
        private readonly IReadOnlyList<string> _allowedExecutables;

        public Handle(SafeProcessHandle process, int processId, IReadOnlyList<string> allowedExecutables)
        {
            _process = process; _processId = processId; _allowedExecutables = allowedExecutables;
            _wait = new ProcessWaitHandle(process);
        }

        public bool PostClose(SessionObservation session)
        {
            // The window must still belong to this exact instance (PID, start time, top-level, owner thread, executable).
            if (session.ProcessId != _processId || !NativeClientWindowAdapter.SameClient(session, _allowedExecutables)) return false;
            return Native.PostMessage(session.Window, 0x10, 0, 0); // WM_CLOSE
        }

        public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (timeout < TimeSpan.Zero) timeout = TimeSpan.Zero;
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ThreadPool.RegisterWaitForSingleObject(_wait,
                static (state, timedOut) => ((TaskCompletionSource<bool>)state!).TrySetResult(!timedOut), exited, timeout, executeOnlyOnce: true);
            try
            {
                using (ct.Register(() => exited.TrySetCanceled(ct)))
                    return await exited.Task.ConfigureAwait(false);
            }
            finally { registration.Unregister(null); }
        }

        public ClientTerminateStatus Terminate()
        {
            if (Native.WaitForSingleObject(_process, 0) == WaitSignaled) return ClientTerminateStatus.AlreadyExited;
            if (Native.TerminateProcess(_process, 1)) return ClientTerminateStatus.Accepted;
            // TerminateProcess refuses a process that exited between the two calls with the same error as a rights problem;
            // the handle's own state tells them apart.
            return Native.WaitForSingleObject(_process, 0) == WaitSignaled ? ClientTerminateStatus.AlreadyExited : ClientTerminateStatus.Denied;
        }

        public void Dispose() { _wait.Dispose(); _process.Dispose(); }
    }

    /// <summary>A WaitHandle over a SYNCHRONIZE-only duplicate of the process handle, so the exit can be awaited through the thread pool's registered waits.</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process)
        {
            nint self = Native.GetCurrentProcess();
            if (!Native.DuplicateHandle(self, process, self, out SafeWaitHandle duplicate, Synchronize, false, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            SafeWaitHandle = duplicate;
        }
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint rights, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
        [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
        [DllImport("kernel32.dll")] internal static extern nint GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateHandle(nint sourceProcess, SafeProcessHandle source, nint targetProcess, out SafeWaitHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
        [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(nint window, uint message, nint w, nint l);
    }
}
