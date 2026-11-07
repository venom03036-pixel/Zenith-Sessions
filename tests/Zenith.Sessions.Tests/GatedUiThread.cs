using System.IO;
using System.Windows.Threading;

namespace Zenith.Sessions.Tests;

/// <summary>A real WPF dispatcher on its own STA thread whose first queued operation is a gate. A browser owner
/// admits its private folder and then waits behind the gate, so tests can cancel, expire or hold files before any
/// window, WebView2 environment or network request exists.</summary>
internal sealed class GatedUiThread : IAsyncDisposable
{
    private readonly ManualResetEventSlim _release = new(false);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Dispatcher Dispatcher { get; private set; } = null!;
    private GatedUiThread() { }

    public static async Task<GatedUiThread> StartAsync()
    {
        var ui = new GatedUiThread();
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                // Bounded, so a failed assertion cannot hold the suite behind a closed gate.
                dispatcher.BeginInvoke(() => ui._release.Wait(TimeSpan.FromSeconds(30)));
                ready.TrySetResult(dispatcher);
                Dispatcher.Run();
            }
            catch (Exception error) { ready.TrySetException(error); }
            finally { ui._stopped.TrySetResult(); }
        }) { IsBackground = true, Name = "Gated test UI thread" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        ui.Dispatcher = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return ui;
    }

    public void Release() => _release.Set();

    /// <summary>Waits until the owner's single private folder holds its lease file, which Create opens last.</summary>
    public static async Task<string> WaitForOwnedFolderAsync(string dataRoot)
    {
        string browserRoot = Path.Combine(dataRoot, "signin-browser-data");
        var limit = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < limit)
        {
            if (Directory.Exists(browserRoot) && Directory.GetDirectories(browserRoot) is [var folder] && File.Exists(Path.Combine(folder, "owner.lock")))
                return folder;
            await Task.Delay(10);
        }
        throw new TimeoutException("The browser owner never admitted its private folder.");
    }

    public async ValueTask DisposeAsync()
    {
        _release.Set();
        Dispatcher.InvokeShutdown();
        await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _release.Dispose();
    }
}

/// <summary>Records the deadline an owner arms and fires it on demand, so no test waits real minutes.</summary>
internal sealed class ManualDeadline : TimeProvider
{
    private readonly TaskCompletionSource<(TimerCallback Callback, object? State)> _armed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TimeSpan? Requested { get; private set; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Requested = dueTime;
        if (!_armed.TrySetResult((callback, state))) throw new InvalidOperationException("Only one owner deadline is expected per test.");
        return new Unused();
    }

    public async Task FireAsync()
    {
        var (callback, state) = await _armed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        callback(state);
    }

    private sealed class Unused : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
