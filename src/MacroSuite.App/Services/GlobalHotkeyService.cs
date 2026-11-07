using System.Runtime.InteropServices;
using System.Windows.Interop;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.App.Services;

/// <summary>
/// Result status for a global hotkey registration attempt.
/// </summary>
public enum HotkeyRegistrationStatus
{
    Success,
    LocalOnly,
    ConflictAlreadyBoundInApp,
    ConflictRegisteredByAnotherApp,
    Failed
}

/// <summary>
/// Detailed result of a global hotkey registration attempt.
/// </summary>
public sealed record HotkeyRegistrationResult(
    HotkeyRegistrationStatus Status,
    int HotkeyId = 0,
    uint Modifiers = 0,
    uint Vk = 0,
    string? ErrorMessage = null)
{
    public bool IsSuccess => Status is HotkeyRegistrationStatus.Success or HotkeyRegistrationStatus.LocalOnly;
}

/// <summary>
/// Exception thrown when a hotkey cannot be registered due to a conflict.
/// </summary>
public sealed class HotkeyConflictException : Exception
{
    public HotkeyRegistrationResult Result { get; }

    public HotkeyConflictException(HotkeyRegistrationResult result)
        : base(result.ErrorMessage ?? "Hotkey conflict detected.")
    {
        Result = result;
    }
}

/// <summary>
/// Service managing system-wide global hotkeys via the standard Win32
/// <c>RegisterHotKey</c> and <c>UnregisterHotKey</c> APIs using WPF <see cref="HwndSource"/> (Rule 3).
/// Does not use low-level keyboard hooks, providing an efficient, OS-cooperative integration.
/// Invokes lightweight dispatch callbacks in arrival order. Callbacks schedule their own UI work.
/// </summary>
public sealed partial class GlobalHotkeyService : IDisposable
{
    private sealed class HotkeyBinding
    {
        public int Id { get; }
        public uint Modifiers { get; }
        public uint Vk { get; }
        public Action Action { get; }
        public string? Description { get; }
        public bool IsGlobal { get; }

        public HotkeyBinding(int id, uint modifiers, uint vk, Action action, string? description, bool isGlobal)
        {
            Id = id;
            Modifiers = modifiers;
            Vk = vk;
            Action = action;
            Description = description;
            IsGlobal = isGlobal;
        }
    }

    private readonly IntPtr _hWnd;
    private readonly HwndSource? _source;
    private readonly IHotkeyRegistrationApi _registration;
    private readonly Dictionary<int, HotkeyBinding> _hotkeysById = new();
    private readonly Dictionary<(uint Modifiers, uint Vk), int> _idByCombo = new();
    private readonly object _lock = new();

    private int _currentId = 1000;
    private bool _disposed;
    private readonly Dictionary<(uint Modifiers, uint Vk), long> _lastTriggerTicks = new();

    /// <summary>
    /// Event fired whenever a hotkey registration fails due to internal or external conflict.
    /// </summary>
    public event Action<HotkeyRegistrationResult>? HotkeyConflictDetected;

    public GlobalHotkeyService(IntPtr hWnd) : this(hWnd, new WindowsHotkeyRegistrationApi(), true) { }

    internal GlobalHotkeyService(IntPtr hWnd, IHotkeyRegistrationApi registration, bool attachWindowHook = false)
    {
        _hWnd = hWnd;
        _registration = registration;
        if (attachWindowHook)
        {
            _source = HwndSource.FromHwnd(hWnd) ?? throw new InvalidOperationException("Cannot acquire HwndSource for window handle.");
            _source.AddHook(HwndHook);
        }
    }
    /// <summary>
    /// Attempts to register a global hotkey with Win32 <c>RegisterHotKey</c>.
    /// Returns a detailed result including status and error description.
    /// </summary>
    public HotkeyRegistrationResult TryRegister(uint modifiers, uint vk, Action action, string? description = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(action);

        lock (_lock)
        {
            // 1. Check for internal duplicate combo within this application
            if (_idByCombo.TryGetValue((modifiers, vk), out int existingId))
            {
                var existing = _hotkeysById[existingId];
                var conflictResult = new HotkeyRegistrationResult(
                    HotkeyRegistrationStatus.ConflictAlreadyBoundInApp,
                    0,
                    modifiers,
                    vk,
                    $"Hotkey combo '{FormatHotkeyName(modifiers, vk)}' is already bound to '{existing.Description ?? "another action"}' in this application.");

                HotkeyConflictDetected?.Invoke(conflictResult);
                return conflictResult;
            }

            int id = Interlocked.Increment(ref _currentId);

            // 2. Attempt Win32 RegisterHotKey with MOD_NOREPEAT flag
            bool success = _registration.Register(_hWnd, id, modifiers | NativeMethods.MOD_NOREPEAT, vk);
            if (!success)
            {
                // Fall back to registration without MOD_NOREPEAT if older Windows version or driver requires it
                success = _registration.Register(_hWnd, id, modifiers, vk);
            }

            if (!success)
            {
                int err = Marshal.GetLastWin32Error();
                // Still keep a local binding so in-window PreviewKeyDown can fire the
                // command even when another app already owns the global combo.
                var localBinding = new HotkeyBinding(id, modifiers, vk, action, description, isGlobal: false);
                _hotkeysById[id] = localBinding;
                _idByCombo[(modifiers, vk)] = id;

                HotkeyRegistrationStatus status = (err == NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED)
                    ? HotkeyRegistrationStatus.ConflictRegisteredByAnotherApp
                    : HotkeyRegistrationStatus.Failed;

                string msg = (status == HotkeyRegistrationStatus.ConflictRegisteredByAnotherApp)
                    ? $"Hotkey combo '{FormatHotkeyName(modifiers, vk)}' is already in use by another application — it will still work while Macro Engine is focused."
                    : $"Failed to register hotkey '{FormatHotkeyName(modifiers, vk)}' globally (Error code {err}). It will still work while Macro Engine is focused.";

                HotkeyConflictDetected?.Invoke(new HotkeyRegistrationResult(status, id, modifiers, vk, msg));
                return new HotkeyRegistrationResult(HotkeyRegistrationStatus.LocalOnly, id, modifiers, vk, msg);
            }

            var binding = new HotkeyBinding(id, modifiers, vk, action, description, isGlobal: true);
            _hotkeysById[id] = binding;
            _idByCombo[(modifiers, vk)] = id;

            return new HotkeyRegistrationResult(HotkeyRegistrationStatus.Success, id, modifiers, vk);
        }
    }

    /// <summary>
    /// Virtual keys of registered commands that use no modifier. Only these are unambiguous
    /// enough to suppress during recording — reserving the key of "Ctrl+1" would also stop
    /// a plain "1" in the target application from being recorded.
    /// </summary>
    public int[] SnapshotVirtualKeys()
    {
        lock (_lock)
        {
            var keys = new List<int>(_hotkeysById.Count);
            foreach (var binding in _hotkeysById.Values)
            {
                if (binding.Modifiers == 0)
                    keys.Add((int)binding.Vk);
            }
            return keys.ToArray();
        }
    }

    /// <summary>
    /// Registers a hotkey, returning the allocated hotkey ID on success or 0 on conflict.
    /// Invokes <see cref="HotkeyConflictDetected"/> if registration fails.
    /// </summary>
    public int Register(uint modifiers, uint vk, Action action, string? description = null)
    {
        var result = TryRegister(modifiers, vk, action, description);
        return result.IsSuccess ? result.HotkeyId : 0;
    }

    /// <summary>
    /// Unregisters a previously registered hotkey with Win32 <c>UnregisterHotKey</c>.
    /// </summary>
    public bool Unregister(int hotkeyId)
    {
        lock (_lock)
        {
            if (_hotkeysById.Remove(hotkeyId, out var binding))
            {
                _idByCombo.Remove((binding.Modifiers, binding.Vk));
                _lastTriggerTicks.Remove((binding.Modifiers, binding.Vk));
                return _registration.Unregister(_hWnd, hotkeyId);
            }
            return false;
        }
    }

    /// <summary>
    /// Unregisters all currently registered hotkeys.
    /// </summary>
    public void UnregisterAll()
    {
        lock (_lock)
        {
            foreach (var id in _hotkeysById.Keys)
            {
                _registration.Unregister(_hWnd, id);
            }
            _hotkeysById.Clear();
            _idByCombo.Clear();
            _lastTriggerTicks.Clear();
        }
    }

    /// <summary>
    /// When this returns true, <c>WM_HOTKEY</c> is consumed and the bound action is not run.
    /// Used while the user is choosing a new keybind so macros do not steal the keystroke.
    /// </summary>
    public Func<uint, uint, bool>? CaptureInterceptor { get; set; }

    /// <summary>When true, WM_HOTKEY and in-window Trigger skip transport/script dispatch.</summary>
    public Func<bool>? SuppressDispatch { get; set; }

    /// <summary>
    /// Triggers the action associated with the specified modifier and virtual key code.
    /// Used when the application window intercepts the keystroke directly.
    /// </summary>
    public bool Trigger(uint modifiers, uint vk) => Trigger(modifiers, vk, localOnly: false);

    /// <summary>
    /// In-window dispatch. When <paramref name="localOnly"/> is true, globally registered
    /// combos are skipped so <c>WM_HOTKEY</c> remains the single fire path (avoids Play
    /// starting and immediately stopping).
    /// </summary>
    public bool Trigger(uint modifiers, uint vk, bool localOnly)
    {
        if (_disposed) return false;
        if (SuppressDispatch?.Invoke() == true)
            return false;
        Action? action = null;
        lock (_lock)
        {
            if (_idByCombo.TryGetValue((modifiers, vk), out int id) && _hotkeysById.TryGetValue(id, out var binding))
            {
                if (localOnly && binding.IsGlobal)
                    return false;
                action = binding.Action;
            }
        }

        if (action != null)
        {
            DispatchAction(action, modifiers, vk);
            return true;
        }

        return false;
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            Action? action = null;
            uint vk = 0;
            uint modifiers = 0;

            lock (_lock)
            {
                if (_hotkeysById.TryGetValue(id, out var binding))
                {
                    action = binding.Action;
                    vk = binding.Vk;
                    modifiers = binding.Modifiers;
                }
            }

            var interceptor = CaptureInterceptor;
            if (interceptor != null && interceptor(modifiers, vk))
            {
                handled = true;
                return IntPtr.Zero;
            }

            if (SuppressDispatch?.Invoke() == true)
            {
                handled = true;
                return IntPtr.Zero;
            }

            if (action != null)
            {
                DispatchAction(action, modifiers, vk);
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    private void DispatchAction(Action action, uint modifiers, uint vk)
    {
        long now = Environment.TickCount64;
        lock (_lock)
        {
            if (_disposed) return;
            var combination = (modifiers, vk);
            if (_lastTriggerTicks.TryGetValue(combination, out long previous) && now - previous <= 250)
                return;
            _lastTriggerTicks[combination] = now;
            // These callbacks only enqueue WPF operations. An independent task per key could
            // enqueue Play after a later Stop and outlive service disposal.
            try
            {
                action();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Hotkey dispatch failed for {FormatHotkeyName(modifiers, vk)}: {ex}");
            }
        }
    }

    public static string FormatHotkeyName(uint modifiers, uint vk)
    {
        var parts = new List<string>();
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("Win");

        string keyName = vk switch
        {
            >= 0x70 and <= 0x87 => $"F{vk - 0x70 + 1}",
            >= (uint)'A' and <= (uint)'Z' => ((char)vk).ToString(),
            >= (uint)'0' and <= (uint)'9' => ((char)vk).ToString(),
            0x1B => "ESC",
            0x20 => "Space",
            0x0D => "Enter",
            _ => $"0x{vk:X2}"
        };
        parts.Add(keyName);

        return string.Join("+", parts);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterAll();
        _source?.RemoveHook(HwndHook);
    }
}
