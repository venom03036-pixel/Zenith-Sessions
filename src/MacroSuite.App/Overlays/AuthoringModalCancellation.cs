using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using MacroSuite.Core.Input;

namespace MacroSuite.App.Overlays;

/// <summary>Cancellation posts a close for one captured modal window on its own dispatcher.</summary>
internal sealed class AuthoringModalCancellation : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action _close;
    private readonly Func<bool>? _canClose;
    private readonly CancellationTokenRegistration _registration;
    private DispatcherOperation? _posted;
    private bool _disposed;
    private bool _closeFailed;

    internal AuthoringModalCancellation(Dispatcher dispatcher, Action close, CancellationToken ct, Func<bool>? canClose = null)
    {
        dispatcher.VerifyAccess();
        _dispatcher = dispatcher;
        _close = close;
        _canClose = canClose;
        _registration = ct.UnsafeRegister(static owner => ((AuthoringModalCancellation)owner!).PostClose(), this);
    }

    internal static bool? ShowDialog(Window window, CancellationToken ct, Func<bool>? canClose = null)
    {
        window.Dispatcher.VerifyAccess();
        bool closed = false;
        void OnClosed(object? sender, EventArgs args) => closed = true;
        window.Closed += OnClosed;
        using var stop = new AuthoringModalCancellation(window.Dispatcher, () =>
        {
            if (!closed) window.Close();
        }, ct, canClose);
        try
        {
            ct.ThrowIfCancellationRequested();
            return window.ShowDialog();
        }
        finally
        {
            try
            {
                if (!closed) window.Close();
                if (!closed) throw new HostInputOperationCleanupException();
            }
            catch (Exception error) when (error is not HostInputOperationCleanupException)
            {
                Trace.TraceWarning("Authoring window close failed: {0}", error.GetType().Name);
                throw new HostInputOperationCleanupException();
            }
            finally { window.Closed -= OnClosed; }
        }
    }

    private void PostClose()
    {
        try { _posted = _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(CloseIfCurrent)); }
        catch (InvalidOperationException) when (_dispatcher.HasShutdownStarted)
        { /* The owning modal call still handles its actual window cleanup. */ }
    }

    private void CloseIfCurrent()
    {
        if (_disposed) return;
        try { if (_canClose?.Invoke() != false) _close(); }
        catch (Exception error)
        {
            _closeFailed = true;
            Trace.TraceWarning("Authoring cancellation close failed: {0}", error.GetType().Name);
        }
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        // The registration only posts. It never waits for this dispatcher or its modal owner.
        _registration.Dispose();
        _posted?.Abort();
        if (_closeFailed) throw new HostInputOperationCleanupException();
    }
}
