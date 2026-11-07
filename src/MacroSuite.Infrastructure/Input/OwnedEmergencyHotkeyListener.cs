using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.Infrastructure.Input;

/// <summary>One emergency hook and one owned message pump, independent of WPF rendering.
/// The hook's factory, callbacks and removal all execute on this dedicated thread.</summary>
public sealed class OwnedEmergencyHotkeyListener : IDisposable
{
    private readonly Thread _thread;
    private readonly IEmergencyMessagePump _pump;
    private readonly Func<IDisposable> _createListener;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _stopRequested;
    private Exception? _failure;
    public Task Completion => _completed.Task;
    public bool IsRunning => _ready.Task.IsCompletedSuccessfully && !_completed.Task.IsCompleted;
    public event Action? UnexpectedlyStopped;

    public OwnedEmergencyHotkeyListener(uint modifiers, uint virtualKey, Action onPressed, Func<bool>? suppress = null)
        : this(() => new EmergencyHotkeyListener(modifiers, virtualKey, onPressed, suppress), new WindowsEmergencyMessagePump(), TimeSpan.FromSeconds(2)) { }

    internal OwnedEmergencyHotkeyListener(Func<IDisposable> createListener, IEmergencyMessagePump pump, TimeSpan startupTimeout)
    {
        _createListener = createListener; _pump = pump;
        _thread = new Thread(Run) { IsBackground = true, Name = "Zenith emergency shortcut" };
        _thread.Start();
        try { _ready.Task.WaitAsync(startupTimeout).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            RequestStop();
            _thread.Join(TimeSpan.FromSeconds(2));
            if (ex is TimeoutException) throw new Win32Exception(258, "The emergency shortcut did not start within two seconds.");
            throw;
        }
    }
    private void Run()
    {
        IDisposable? listener = null;
        try
        {
            _pump.Prepare();
            if (Volatile.Read(ref _stopRequested) != 0) { _ready.TrySetCanceled(); return; }
            listener = _createListener();
            _ready.TrySetResult();
            while (Volatile.Read(ref _stopRequested) == 0 && _pump.ReadAndDispatch()) { }
        }
        catch (Exception ex)
        {
            _failure = ex;
            if (!_ready.TrySetException(ex)) Trace.TraceError($"Emergency message pump failed: {ex.GetType().Name}");
        }
        finally
        {
            try { listener?.Dispose(); }
            catch (Exception ex) { _failure = ex; Trace.TraceError($"Emergency hook removal failed: {ex.GetType().Name}"); }
            _completed.TrySetResult();
            if (Volatile.Read(ref _stopRequested) == 0 && UnexpectedlyStopped is { } handlers)
                foreach (Action handler in handlers.GetInvocationList())
                    try { handler(); }
                    catch (Exception ex) { Trace.TraceError($"Emergency owner failure notification failed: {ex.GetType().Name}"); }
        }
    }
    private void RequestStop()
    {
        Interlocked.Exchange(ref _stopRequested, 1);
        _pump.RequestStop();
    }
    public void Dispose()
    {
        RequestStop();
        if (Thread.CurrentThread == _thread) return;
        if (!_thread.Join(TimeSpan.FromSeconds(2)))
            throw new Win32Exception(258, "The emergency shortcut owner is still finishing. Its shutdown remains tracked.");
        if (_failure is { } failure) throw new Win32Exception("The emergency shortcut owner reported a failure: " + failure.Message);
    }
}

internal interface IEmergencyMessagePump
{
    void Prepare();
    bool ReadAndDispatch();
    void RequestStop();
}

internal sealed class WindowsEmergencyMessagePump : IEmergencyMessagePump
{
    private uint _threadId;
    public void Prepare()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // create the queue before a shutdown can be posted
    }
    public bool ReadAndDispatch()
    {
        int result = NativeMethods.GetMessageW(out var message, IntPtr.Zero, 0, 0);
        if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "The emergency message pump could not read its queue.");
        if (result == 0) return false;
        NativeMethods.TranslateMessage(ref message); NativeMethods.DispatchMessageW(ref message);
        return true;
    }
    public void RequestStop() { uint thread = Volatile.Read(ref _threadId); if (thread != 0) NativeMethods.PostThreadMessageW(thread, 0x12, UIntPtr.Zero, IntPtr.Zero); }
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMethods.MSG message, IntPtr window, uint first, uint last, uint remove);
}
