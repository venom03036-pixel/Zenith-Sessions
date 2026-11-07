using System.Windows;
using System.Windows.Threading;

namespace MacroSuite.Infrastructure.Tests;

/// <summary>
/// Owns the process-wide test Application and pumps its dispatcher for its whole
/// lifetime. A temporary STA thread must not leave Application.Current orphaned.
/// Shows no windows and never invokes the production application startup path.
/// </summary>
internal static class WpfTestApplication
{
    private static readonly Lazy<Task<Dispatcher>> DispatcherTask = new(Start);
    private static Thread? _thread;

    public static async Task RunAsync(Func<Task> body)
    {
        var dispatcher = await DispatcherTask.Value.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await dispatcher.InvokeAsync(body).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
    }

    private static Task<Dispatcher> Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is not null)
                    throw new InvalidOperationException("Create the test Application through WpfTestApplication so its dispatcher stays active.");
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    if (!app.Dispatcher.HasShutdownStarted)
                        app.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                    if (_thread is { IsAlive: true } thread && thread != Thread.CurrentThread)
                        thread.Join(TimeSpan.FromSeconds(2));
                };
                ready.TrySetResult(app.Dispatcher);
                Dispatcher.Run();
            }
            catch (Exception error)
            {
                if (!ready.TrySetException(error)) throw;
            }
        }) { IsBackground = true, Name = "Zenith test Application dispatcher" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        return ready.Task;
    }
}
