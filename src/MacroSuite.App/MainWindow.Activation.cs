using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MacroSuite.Infrastructure.Windows;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Input;
using MacroSuite.App.Common;

namespace MacroSuite.App;

public partial class MainWindow
{
    private CancellationTokenSource? _restoreFocusCancellation;
    private Task _restoreTask = Task.CompletedTask;

    private void CancelPendingRestoreFocus()
    {
        _restoreFocusCancellation?.Cancel();
    }

    private async Task RestoreExistingWindowAsync()
    {
        if (_shutdownPending || _restoringMainWindow) return;
        _restoringMainWindow = true;
        CancelPendingRestoreFocus();
        using var cancellation = new CancellationTokenSource();
        _restoreFocusCancellation = cancellation;
        TaskCompletionSource readiness = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void SignalReadiness(object? sender, EventArgs args) => readiness.TrySetResult();
        void SignalLoaded(object sender, RoutedEventArgs args) => readiness.TrySetResult();
        SourceInitialized += SignalReadiness;
        ContentRendered += SignalReadiness;
        Loaded += SignalLoaded;
        async Task WaitForReadiness(CancellationToken ct)
        {
            var pulse = readiness;
            try { await pulse.Task.WaitAsync(TimeSpan.FromMilliseconds(75), ct); }
            catch (TimeoutException) { /* native readiness can change without a managed event */ }
            finally { if (ReferenceEquals(readiness, pulse)) readiness = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        }
        try
        {
            if (DataContext is MainViewModel vm) await vm.PauseForEditorFocusAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            if (_shutdownPending || InputModeGate.IsResourceCapture) return;
            cancellation.CancelAfter(TimeSpan.FromSeconds(1));
            // Reuse the existing editor; no library reload or control recreation belongs here.
            WindowState = _windowStateBeforeMini == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            Show();
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsVisible || WindowState == WindowState.Minimized || InputModeGate.IsResourceCapture) return;
            nint handle = new WindowInteropHelper(this).Handle;
            bool ready = Win32WindowActivation.RestoreToState(handle, _windowStateBeforeMini == WindowState.Maximized);
            bool activated = ready && Win32WindowActivation.TryActivate(handle);
            _miniMode?.Hide();
            // Hiding an owned foreground window may change activation again. The old
            // return value is not proof that the editor still owns the foreground.
            nint shownHandle = new WindowInteropHelper(this).Handle;
            activated = Win32WindowActivation.IsReady(shownHandle) && Win32WindowActivation.IsForeground(shownHandle);
            if (!activated)
            {
                activated = await RestoreFocusRetry.RunAsync(
                    () => !_shutdownPending && IsVisible && WindowState != WindowState.Minimized && !InputModeGate.IsResourceCapture,
                    () => Win32WindowActivation.TryRequestForeground(new WindowInteropHelper(this).Handle),
                    WaitForReadiness, cancellation.Token);
            }
            if (activated) Focus();
            else if (DataContext is MainViewModel statusVm)
                statusVm.StatusMessage = "The editor is restored. Windows kept another application in front; select Zenith on the taskbar to focus it.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_shutdownPending && IsVisible && WindowState != WindowState.Minimized && !InputModeGate.IsResourceCapture && ReferenceEquals(_restoreFocusCancellation, cancellation)
                && DataContext is MainViewModel vm)
                vm.StatusMessage = "The editor is restored. Windows kept another application in front; select Zenith on the taskbar to focus it.";
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Restoring the existing editor failed: {ex.GetType().Name}");
            if (DataContext is MainViewModel vm) vm.StatusMessage = "Could not restore the editor: " + ex.Message;
        }
        finally
        {
            SourceInitialized -= SignalReadiness;
            ContentRendered -= SignalReadiness;
            Loaded -= SignalLoaded;
            if (ReferenceEquals(_restoreFocusCancellation, cancellation)) _restoreFocusCancellation = null;
            _restoringMainWindow = false;
        }
    }
}
