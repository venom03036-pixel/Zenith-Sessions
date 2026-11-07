using System.Threading;
using System.Windows;
using System.Windows.Threading;
using MacroSuite.Core.Input;

namespace MacroSuite.App.Overlays;

/// <summary>
/// Hides the Macro Engine chrome while the user is looking at the desktop
/// (region snip, color pick, coordinate pick). Nested calls are ref-counted
/// so a snip that also captures a frame can stay hidden until capture finishes.
/// </summary>
internal static class ScreenCapturePrivacy
{
    private static readonly PrivacyLifecycle Lifecycle = new();

    /// <summary>
    /// Collapse the main window (and mini-mode) so the desktop is visible,
    /// then restore it when the returned token is disposed.
    /// </summary>
    public static IDisposable HideEngine(TimeSpan? settleDelay = null)
    {
        var app = Application.Current;
        if (app == null)
            return Noop.Instance;

        // Verify before changing depth or remembered windows. Async callers marshal
        // here and join restoration rather than synchronously blocking another UI.
        app.Dispatcher.VerifyAccess();
        return new RestoreToken(app.Dispatcher, Lifecycle.Enter(() =>
        {
            HideVisibleChrome(app);
            PumpUi(settleDelay ?? TimeSpan.FromMilliseconds(250));
        }));
    }

    private static void HideVisibleChrome(Application app)
    {
        Window? main = app.MainWindow;
        foreach (Window window in app.Windows)
        {
            if (!ShouldHide(window, main))
                continue;

            var remembered = new HiddenWindow(
                window,
                window.Visibility,
                window.WindowState,
                window.Topmost);
            // Remember before the first mutation, so a setup exception rolls back
            // every possibly changed window through the same actual restore path.
            Lifecycle.Remember(() => RestoreHiddenWindow(remembered));
            window.Topmost = false;
            window.Visibility = Visibility.Collapsed;
        }
    }

    private static bool ShouldHide(Window window, Window? main)
    {
        if (window is null || !window.IsVisible || window.Visibility != Visibility.Visible)
            return false;
        if (window.WindowState == WindowState.Minimized)
            return false;
        if (window is RegionSelectorWindow or ColorPickerOverlay or CoordinatePickOverlay or SearchRegionPreviewWindow)
            return false;
        if (ReferenceEquals(window, main) || window is MiniModeOverlay)
            return true;
        return false;
    }

    private static void RestoreHiddenWindow(HiddenWindow state)
    {
        Window window = state.Window;
        if (!window.IsLoaded) return;
        window.Visibility = state.Visibility;
        window.WindowState = state.State;
        window.Topmost = state.Topmost;
        if (window.Visibility == Visibility.Visible && window.WindowState != WindowState.Minimized)
            window.Activate();
    }

    internal static void PumpUi(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return;

        var dispatcher = Dispatcher.FromThread(Thread.CurrentThread) ?? Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private sealed class RestoreToken(Dispatcher dispatcher, IDisposable scope) : IDisposable
    {
        public void Dispose()
        {
            dispatcher.VerifyAccess();
            scope.Dispose();
        }
    }

    /// <summary>
    /// The actual bounded privacy lifecycle, with restore actions separated only
    /// so failure/rollback mechanics can be tested without showing native windows.
    /// Callers remain dispatcher-owned; this is not another operation owner.
    /// </summary>
    internal sealed class PrivacyLifecycle
    {
        internal const int MaximumRememberedWindows = 16;
        private readonly List<Action> _pending = new();
        private bool _restoring;
        internal int Depth { get; private set; }
        internal int PendingCount => _pending.Count;

        internal IDisposable Enter(Action setup)
        {
            if (Depth == 0 && _pending.Count != 0) throw new HostInputOperationCleanupException();
            Depth++;
            if (Depth == 1)
            {
                try { setup(); }
                catch
                {
                    Leave(); // If rollback fails, retain its entries and report cleanup uncertainty.
                    throw;
                }
            }
            return new Scope(this);
        }

        internal void Remember(Action restore)
        {
            if (Depth <= 0 || _pending.Count == MaximumRememberedWindows)
                throw new HostInputOperationCleanupException();
            _pending.Add(restore);
        }

        private void Leave()
        {
            if (Depth <= 0) throw new HostInputOperationCleanupException();
            if (--Depth == 0) RestorePending();
        }

        private void RestorePending()
        {
            if (Depth != 0 || _restoring) throw new HostInputOperationCleanupException();
            _restoring = true;
            try
            {
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    try { _pending[i](); _pending.RemoveAt(i); }
                    catch { /* Retain only the unresolved entry; still attempt every other window. */ }
                }
            }
            finally { _restoring = false; }
            if (_pending.Count != 0) throw new HostInputOperationCleanupException();
        }

        private sealed class Scope(PrivacyLifecycle owner) : IDisposable
        {
            private bool _released, _completed;
            public void Dispose()
            {
                if (_completed) return;
                if (_released) owner.RestorePending(); // Deliberate retry of this captured failed scope only.
                else { _released = true; owner.Leave(); }
                _completed = true;
            }
        }
    }

    private sealed class Noop : IDisposable
    {
        public static readonly Noop Instance = new();
        public void Dispose() { }
    }

    private readonly record struct HiddenWindow(
        Window Window,
        Visibility Visibility,
        WindowState State,
        bool Topmost);
}
