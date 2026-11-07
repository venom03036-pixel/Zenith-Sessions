using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.Infrastructure.Input;

// A controlled seam for lifecycle tests. Production still has exactly one short-lived hook thread.
internal interface IOneShotLocationPickerSession
{
    bool IsArmed { get; }
    Task Ready { get; }
    Task<ScreenPoint?> Selection { get; }
    Task Exited { get; }
    void Start();
    void RequestStop();
}

/// <summary>
/// One explicit location-pick operation. Cancellation signals its captured arm; completion joins the
/// actual hook thread. Dispose fences immediately; DisposeAsync additionally joins owned work.
/// </summary>
public sealed class OneShotLocationPicker : IHostInputOperationBoundPicker, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<IOneShotLocationPickerSession> _sessions;
    private readonly bool _ownsArbiter;
    private Arm? _active;
    private Task<ScreenPoint?>? _operation;
    private Task? _dispose;
    private bool _disposed;
    public HostInputOperationArbiter InputOperations { get; }
    public bool IsArmed { get { lock (_gate) return _active?.Session?.IsArmed == true; } }
    public Task Completion { get { lock (_gate) return (Task?)_operation ?? Task.CompletedTask; } }

    public OneShotLocationPicker() : this(null) { }

    public OneShotLocationPicker(HostInputOperationArbiter? inputOperations)
        : this(inputOperations ?? new HostInputOperationArbiter(), static () => new NativeSession(), inputOperations is null) { }

    internal OneShotLocationPicker(HostInputOperationArbiter inputOperations, Func<IOneShotLocationPickerSession> sessions)
        : this(inputOperations, sessions, false) { }

    private OneShotLocationPicker(HostInputOperationArbiter inputOperations, Func<IOneShotLocationPickerSession> sessions, bool ownsArbiter)
    {
        InputOperations = inputOperations ?? throw new ArgumentNullException(nameof(inputOperations));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions)); _ownsArbiter = ownsArbiter;
    }

    public Task<ScreenPoint?> ArmAndWaitAsync(CancellationToken ct = default) => Begin(null, ct);
    public Task<ScreenPoint?> ArmAndWaitAsync(HostInputOperationLease parent, CancellationToken ct = default)
    { ArgumentNullException.ThrowIfNull(parent); return Begin(parent, ct); }

    private Task<ScreenPoint?> Begin(HostInputOperationLease? parent, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            if (_operation is { IsCompleted: false })
                throw new InvalidOperationException("A location picker is still starting, armed or finishing.");
            bool own = parent is null;
            if (own)
            {
                if (!InputOperations.TryAcquire(HostInputOperationKind.Picker, out parent, out string reason))
                    throw new InvalidOperationException(reason);
            }
            else if (!InputOperations.Owns(parent!) || !parent!.CanContinue
                || parent.Kind is not (HostInputOperationKind.Picker or HostInputOperationKind.CaptureTest))
                throw new InvalidOperationException("The location picker requires its exact current interactive parent.");
            var arm = new Arm(parent!, own, ct);
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _active = arm; _operation = RunAsync(arm, published.Task);
            published.SetResult();
            return _operation;
        }
    }

    private async Task<ScreenPoint?> RunAsync(Arm arm, Task published)
    {
        await published.ConfigureAwait(false);
        CancellationTokenRegistration stopRegistration = default;
        bool clean = true;
        ScreenPoint? point = null;
        try
        {
            arm.Stop.Token.ThrowIfCancellationRequested();
            if (!arm.Lease.CanContinue) throw new OperationCanceledException(arm.Token);
            var session = _sessions();
            Volatile.Write(ref arm.Session, session);
            stopRegistration = arm.Stop.Token.Register(session.RequestStop);
            session.Start();
            await session.Ready.WaitAsync(arm.Stop.Token).ConfigureAwait(false);
            point = await session.Selection.WaitAsync(arm.Stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (arm.Token.IsCancellationRequested || !arm.Lease.CanContinue) { }
        finally
        {
            try { arm.Session?.RequestStop(); }
            catch (Exception) { clean = false; }
            try { if (arm.Session is { } session) await session.Exited.ConfigureAwait(false); }
            catch (Exception) { clean = false; }
            try { await stopRegistration.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { clean = false; }
            try { await arm.CloseStopAsync().ConfigureAwait(false); }
            catch (Exception) { clean = false; }
            try
            {
                if (arm.OwnsLease) await arm.Lease.CompleteAsync(clean).ConfigureAwait(false);
            }
            finally { lock (_gate) if (ReferenceEquals(_active, arm)) _active = null; }
            if (!clean) throw new HostInputOperationCleanupException();
        }
        // Selection is not publication: a caller/host stop while the native thread is exiting
        // still wins. A normally completed owned root is retired here, so check its stop
        // incarnation rather than CanContinue (which correctly becomes false on retirement).
        return !arm.Token.IsCancellationRequested && !arm.Caller.IsCancellationRequested
            && !arm.Lease.StopToken.IsCancellationRequested && arm.Lease.Generation == InputOperations.StopGeneration
            && (arm.OwnsLease || arm.Lease.CanContinue) ? point : null;
    }

    public void Cancel()
    { Arm? arm; lock (_gate) arm = _active; arm?.SignalStop(); }
    public void Cancel(HostInputOperationLease parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        Arm? arm; lock (_gate) arm = ReferenceEquals(_active?.Lease, parent) ? _active : null;
        arm?.SignalStop();
    }
    public void Dispose()
    {
        Arm? arm;
        lock (_gate) { if (_disposed) return; _disposed = true; arm = _active; }
        if (_ownsArbiter) InputOperations.Close();
        arm?.SignalStop();
    }
    public ValueTask DisposeAsync()
    {
        Dispose();
        lock (_gate) return new(_dispose ??= JoinAsync(_operation));
    }
    private async Task JoinAsync(Task? operation)
    {
        if (operation is not null) await operation.ConfigureAwait(false);
        if (_ownsArbiter) await InputOperations.DrainAsync().ConfigureAwait(false);
    }

    private sealed class Arm
    {
        private readonly object _stopGate = new();
        private Task _cancellation = Task.CompletedTask;
        private bool _closing;
        public HostInputOperationLease Lease { get; }
        public bool OwnsLease { get; }
        public CancellationTokenSource Stop { get; }
        public CancellationToken Token { get; }
        public CancellationToken Caller { get; }
        public IOneShotLocationPickerSession? Session;
        public Arm(HostInputOperationLease lease, bool own, CancellationToken caller)
        {
            Lease = lease; OwnsLease = own; Caller = caller;
            Stop = CancellationTokenSource.CreateLinkedTokenSource(caller, lease.StopToken); Token = Stop.Token;
        }
        public void SignalStop()
        {
            lock (_stopGate)
                if (!_closing && !Stop.IsCancellationRequested) _cancellation = Stop.CancelAsync();
        }
        public async Task CloseStopAsync()
        {
            Task cancellation;
            lock (_stopGate) { _closing = true; cancellation = _cancellation; }
            try { await cancellation.ConfigureAwait(false); }
            finally { Stop.Dispose(); }
        }
    }

    private sealed class NativeSession : IOneShotLocationPickerSession
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ScreenPoint?> _selection = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;
        private NativeMethods.HookProc? _callback;
        private nint _hook;
        private uint _threadId;
        private bool _started, _queueReady, _cleanupFailed;
        private int _stopping, _armed;
        private Task? _exited;
        public NativeSession() => _thread = new Thread(Run) { Name = "MacroSuite.LocationPicker", IsBackground = true };
        public bool IsArmed => Volatile.Read(ref _armed) != 0;
        public Task Ready => _ready.Task;
        public Task<ScreenPoint?> Selection => _selection.Task;
        public Task Exited
        {
            get
            {
                lock (_gate)
                    return !_started ? Task.CompletedTask : _exited ??= Task.Run(() =>
                    {
                        _thread.Join(); // No ignored deadline: this handle represents the actual owned thread exit.
                        if (_cleanupFailed) throw new HostInputOperationCleanupException();
                    });
            }
        }
        public void Start()
        {
            lock (_gate) { _thread.Start(); _started = true; }
        }
        public void RequestStop()
        {
            lock (_gate)
            {
                Volatile.Write(ref _stopping, 1); Volatile.Write(ref _armed, 0);
                // The thread clears this identity under the same gate before exiting, so an old
                // cancellation cannot post to a later unrelated thread which reused its numeric ID.
                if (_threadId != 0 && _queueReady) NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT, 0, 0);
            }
        }
        private void Run()
        {
            try
            {
                // Microsoft documents PeekMessage as the way to create a thread queue before
                // PostThreadMessage can reliably signal it; readiness follows queue creation.
                PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);
                lock (_gate) { _threadId = NativeMethods.GetCurrentThreadId(); _queueReady = true; }
                if (Volatile.Read(ref _stopping) != 0) return;
                _callback = Hook;
                _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _callback, NativeMethods.GetModuleHandleW(null), 0);
                if (_hook == 0) throw new Win32Exception("The location picker could not install its keyboard hook.");
                lock (_gate) if (_stopping == 0) Volatile.Write(ref _armed, 1);
                _ready.TrySetResult();
                while (Volatile.Read(ref _stopping) == 0)
                {
                    int result = NativeMethods.GetMessageW(out var message, 0, 0, 0);
                    if (result == 0) break;
                    if (result < 0) throw new Win32Exception("The location picker message owner failed.");
                    NativeMethods.TranslateMessage(ref message); NativeMethods.DispatchMessageW(ref message);
                }
            }
            catch (Exception error)
            {
                if (!_ready.TrySetException(error)) _selection.TrySetException(error);
            }
            finally
            {
                Volatile.Write(ref _armed, 0);
                if (_hook != 0 && !NativeMethods.UnhookWindowsHookEx(_hook)) _cleanupFailed = true;
                _hook = 0;
                lock (_gate) { _threadId = 0; _queueReady = false; }
                _ready.TrySetResult(); _selection.TrySetResult(null);
            }
        }
        private unsafe nint Hook(int code, nint message, nint data)
        {
            if (data != 0 && code >= 0 && IsArmed && (uint)message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
            {
                try
                {
                    ref readonly var key = ref Unsafe.AsRef<NativeMethods.KBDLLHOOKSTRUCT>((void*)data);
                    if (key.vkCode == NativeMethods.VK_TAB)
                    {
                        ScreenPoint? point = NativeMethods.GetCursorPos(out var cursor) ? new(cursor.x, cursor.y) : null;
                        _selection.TrySetResult(point); StopFromHook(); return 1;
                    }
                    if (key.vkCode == NativeMethods.VK_ESCAPE)
                    { _selection.TrySetResult(null); StopFromHook(); return 1; }
                }
                catch { _selection.TrySetResult(null); StopFromHook(); }
            }
            return NativeMethods.CallNextHookEx(_hook, code, message, data);
        }
        private void StopFromHook()
        {
            Volatile.Write(ref _stopping, 1);
            Volatile.Write(ref _armed, 0);
            // Runs on this exact hook thread: no lock, logging, allocation or UI callback.
            NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT, 0, 0);
        }
        [DllImport("user32.dll", EntryPoint = "PeekMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessageW(out NativeMethods.MSG message, nint window, uint minimum, uint maximum, uint remove);
    }
}
