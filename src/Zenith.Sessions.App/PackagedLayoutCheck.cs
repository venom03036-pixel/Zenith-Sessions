using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using MacroSuite.App.Themes;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

// Only --self-check uses this isolated STA. Resolve the actual shipped BAML,
// application resources and bindings, without showing a native window or input.
internal static class PackagedLayoutCheck
{
    public static Task VerifyAsync(string root, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            App? app = null;
            Exception? failure = null;
            try
            {
                app = new App { Options = new(root, false, true), ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var operation = dispatcher.InvokeAsync(() => VerifyOnDispatcherAsync(app, root, ct)).Task.Unwrap();
                var shutdown = operation.ContinueWith(_ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                Dispatcher.Run();
                shutdown.GetAwaiter().GetResult(); operation.GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                // Shutdown only after the tracked operation completes. Shutting down
                // inside its callback can retire the dispatcher before its Task completes.
                try { app?.Shutdown(); }
                catch (Exception ex) { failure ??= ex; }
                finally { if (!dispatcher.HasShutdownStarted) dispatcher.InvokeShutdown(); }
            }
            if (failure is null) done.TrySetResult(); else done.TrySetException(failure);
        }) { IsBackground = true, Name = "Zenith packaged layout check" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return done.Task;
    }

    private static async Task VerifyOnDispatcherAsync(App app, string root, CancellationToken ct)
    {
        ShellViewModel? vm = null; MainWindow? window = null; Exception? failure = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            app.InitializeComponent();
            ThemeManager.Apply(app.Resources, ThemeCatalog.DefaultId);
            vm = new ShellViewModel(root, true, Dispatcher.CurrentDispatcher);
            window = MainWindow.CreateForLayoutCheck(vm);
            window.Measure(new Size(1320, 840));
            window.Arrange(new Rect(0, 0, 1320, 840)); window.UpdateLayout();
            vm.Navigate.Execute("JoinGame");
            window.UpdateLayout();
            if (!vm.IsJoinGame || !vm.IsSessionWorkspace || vm.IsSessions || vm.IsAutomation
                || window.FindName("JoinGamePage") is not FrameworkElement { Visibility: Visibility.Visible }
                || window.FindName("DestinationInput") is not FrameworkElement
                || window.FindName("DestinationSearchPanel") is not FrameworkElement
                || window.FindName("NativeLaunchStages") is not System.Windows.Controls.Expander)
                throw new InvalidOperationException("The packaged Sessions join route lost its production controls or workspace state.");
            var diagnosticId = Guid.NewGuid(); var at = DateTimeOffset.UtcNow;
            var diagnostic = new NativeLaunchDiagnosticSnapshot(diagnosticId, 1, at, at,
                NativeLaunchDiagnosticStage.Finished, NativeLaunchCleanupStatus.Complete,
                TrustStatus: ClientExecutableTrustStatus.Rejected, TrustReason: ClientExecutableTrustReason.Unsigned,
                ExecutableObservedUtc: at, CleanupObservedUtc: at).Validated();
            var presentation = NativeLaunchDiagnosticPresentation.Project(diagnosticId, diagnostic);
            if (!presentation.Available || presentation.Snapshot?.OperationId != diagnosticId
                || presentation.AccountVerified || presentation.ArrivalVerified || presentation.InputAuthorized
                || !presentation.Trust.Contains("signature", StringComparison.OrdinalIgnoreCase)
                || !NativeLaunchDiagnosticPresentation.Unavailable(diagnosticId).Summary.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                || NativeLaunchDiagnosticPresentation.Project(Guid.NewGuid(), diagnostic).Available)
                throw new InvalidOperationException("The packaged fixed diagnostic presentation lost its exact-operation or unavailable boundary.");
            vm.Navigate.Execute("Automation"); window.UpdateLayout();
            if (!vm.IsAutomation || vm.IsSessionWorkspace || vm.IsJoinGame
                || window.FindName("JoinGamePage") is not FrameworkElement { Visibility: Visibility.Collapsed })
                throw new InvalidOperationException("The packaged macro workspace did not hide joining controls.");
            vm.Navigate.Execute("Sessions"); window.UpdateLayout();
            if (!vm.IsSessions || !vm.IsSessionWorkspace || vm.IsJoinGame)
                throw new InvalidOperationException("The packaged client-session route did not retain its distinct meaning.");
            if (window.FindName("ClientPriorityTools") is not System.Windows.Controls.Expander priorityTools)
                throw new InvalidOperationException("The packaged priority tools are missing.");
            priorityTools.IsExpanded = true;
            if (window.FindName("ClientMemoryTrimTools") is not System.Windows.Controls.Expander trimTools)
                throw new InvalidOperationException("The packaged memory trim tools are missing.");
            trimTools.IsExpanded = true;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            // A never-shown Window can retain its disconnected layout. Measure the
            // production content itself to exercise the expanded panel offscreen.
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1320, 840));
            content.Arrange(new Rect(0, 0, 1320, 840));
            content.UpdateLayout();
            if (window.FindName("SetClientPriorityBelowNormal") is not System.Windows.Controls.Button priorityButton
                || priorityButton.Command != vm.SetClientsBelowNormalPriority || priorityButton.IsEnabled
                || vm.IsChangingClientPriority || vm.PendingPriorityRestoreCount != 0)
                throw new InvalidOperationException("The packaged priority controls lost their command binding or sample-mode boundary.");
            if (priorityTools.ActualHeight <= 0)
                throw new InvalidOperationException("The packaged priority controls could not be laid out.");
            if (window.FindName("TrimClientsNowButton") is not System.Windows.Controls.Button trimButton
                || trimButton.Command != vm.TrimClientsNow || trimButton.IsEnabled || vm.IsTrimmingClients || vm.CanTrimClients)
                throw new InvalidOperationException("The packaged memory trim controls lost their command binding or sample-mode boundary.");
            if (trimTools.ActualHeight <= 0)
                throw new InvalidOperationException("The packaged memory trim controls could not be laid out.");
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            try { if (vm is not null) await vm.DisposeAsync(); }
            catch (Exception ex) { failure ??= ex; }
            try { window?.Close(); }
            catch (Exception ex) { failure ??= ex; }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
