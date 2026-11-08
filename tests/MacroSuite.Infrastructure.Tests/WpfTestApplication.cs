using System.Diagnostics;
using System.Runtime.CompilerServices;
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

    /// <summary>
    /// Hang guard for one test body, not a speed budget: it only detects a body that never finishes.
    /// The heaviest bodies (production editor with 10,000 actions) take 2-10 s alone; in loaded
    /// full runs they took up to 1 min 30 s while computing under 1 s per layout pass, and 2 min 8 s
    /// once one layout pass computed for 114 s (QUALITY_STATUS F-4). A test that needs a speed
    /// budget counts it in dispatcher-thread CPU with <see cref="ThreadCpuBudget"/>, as the
    /// UiNavigation tests do, so the budget can stay tight without failing on a busy machine.
    /// </summary>
    internal static readonly TimeSpan LivenessBound = TimeSpan.FromMinutes(2);
    /// <summary>How long an overrunning body may still take to stop before later bodies are refused.</summary>
    private static readonly TimeSpan OverrunDrain = TimeSpan.FromSeconds(30);
    private static readonly object AbandonedGate = new();
    private static (string Test, Task Body)? _abandoned;

    public static Task RunAsync(Func<Task> body, [CallerMemberName] string test = "")
        => RunAsync(body, LivenessBound, OverrunDrain, test);

    /// <summary>
    /// Runs one body on the shared dispatcher. A body cannot be aborted, so a body that overruns
    /// keeps its test waiting until it stops: otherwise it would keep running beside the next
    /// tests, slowing their dispatcher work and leaving its editor, snip or styles in place.
    /// </summary>
    internal static async Task RunAsync(Func<Task> body, TimeSpan liveness, TimeSpan drain, [CallerMemberName] string test = "")
    {
        var dispatcher = await DispatcherTask.Value.WaitAsync(LivenessBound).ConfigureAwait(false);
        lock (AbandonedGate)
        {
            if (_abandoned is { Body.IsCompleted: true }) _abandoned = null;
            if (_abandoned is { } stuck)
                throw new InvalidOperationException($"'{test}' was not started: the shared WPF test dispatcher is still running " +
                    $"'{stuck.Test}', which did not finish within its time limit.");
        }
        Task running = dispatcher.InvokeAsync(body).Task.Unwrap();
        // The hang guard and the drain are timers (Rule 12): a body that finishes first cancels the one
        // still armed, so a finished test leaves nothing pending behind it.
        using var guard = new CancellationTokenSource();
        try
        {
            if (await Task.WhenAny(running, Task.Delay(liveness, guard.Token)).ConfigureAwait(false) == running)
            {
                await running.ConfigureAwait(false);
                return;
            }
            var overrun = Stopwatch.StartNew();
            if (await Task.WhenAny(running, Task.Delay(drain, guard.Token)).ConfigureAwait(false) == running)
                throw new TimeoutException($"'{test}' did not finish within {liveness.TotalSeconds:0.##} s on the shared WPF test dispatcher. " +
                    $"It stopped {overrun.Elapsed.TotalSeconds:0.0} s later, before the next test started, so no later test ran beside it.",
                    running.Exception?.GetBaseException());
            lock (AbandonedGate) _abandoned = (test, running);
            throw new TimeoutException($"'{test}' did not finish within {liveness.TotalSeconds:0.##} s on the shared WPF test dispatcher " +
                $"and was still running {drain.TotalSeconds:0.##} s later. Later WPF tests are refused until it ends.");
        }
        finally { guard.Cancel(); }
    }

    /// <summary>Completes when no abandoned body remains on the shared dispatcher.</summary>
    internal static Task WhenAbandonedBodyEnds()
    {
        lock (AbandonedGate) return _abandoned?.Body.ContinueWith(static _ => { }, TaskScheduler.Default) ?? Task.CompletedTask;
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
