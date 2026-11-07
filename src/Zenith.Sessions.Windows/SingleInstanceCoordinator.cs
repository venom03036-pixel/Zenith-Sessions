using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Zenith.Sessions.Windows;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipe;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _server = Task.CompletedTask;
    // 20 pauses of 250 ms: about 5 s of retries before a persistent fault stops the listener.
    private const int MaxConsecutiveFaults = 20;
    private static readonly TimeSpan FaultRetryDelay = TimeSpan.FromMilliseconds(250);
    private volatile Type? _lastListenerFault;
    private volatile bool _listenerFaulted;
    private bool _disposed;
    public bool IsOwner { get; }
    /// <summary>Type of the latest pipe or restore fault the listener survived or stopped on; never its message (Rule 10).</summary>
    public Type? LastListenerFault => _lastListenerFault;
    /// <summary>True once repeated consecutive faults stopped the listener; later launches then cannot restore this instance.</summary>
    public bool IsListenerFaulted => _listenerFaulted;
    internal string PipeName => _pipe;
    internal string MutexName => "Local\\" + _pipe;
    public SingleInstanceCoordinator(string root)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string scope = (identity.User?.Value ?? throw new InvalidOperationException("Windows identity is unavailable.")) + "|" + Path.GetFullPath(root).TrimEnd('\\').ToUpperInvariant();
        _pipe = "ZenithSessions." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..24];
        // Ownership means acquiring the mutex, not creating it: a lingering second launch (for example its "already
        // starting" box) keeps the named object alive after the owner exits, and "created" would then refuse every launch.
        // Ownership is held by the constructing thread, which must also run Dispose (EntryPoint's Main thread does both).
        _mutex = new Mutex(initiallyOwned:false, MutexName);
        try { IsOwner = _mutex.WaitOne(0); }
        // The previous owner ended without releasing (a crash); Windows hands ownership to this thread, so this launch opens.
        catch (AbandonedMutexException) { IsOwner = true; }
    }
    public void Listen(Func<CancellationToken, Task> restore)
    { if (!IsOwner || !_server.IsCompleted) throw new InvalidOperationException("The instance cannot start another listener."); _lastListenerFault = null; _listenerFaulted = false; _server = ListenAsync(restore, _lifetime.Token); }
    private async Task ListenAsync(Func<CancellationToken, Task> restore, CancellationToken ct)
    {
        int faults = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Each launch request is one protected unit: its fault ends that request, never the listener every later launch needs.
                try { await ServeOneAsync(restore, ct).ConfigureAwait(false); faults = 0; }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Type only (Rule 10). A refused pipe creation or accept usually clears after a short pause; a fault
                    // that repeats past the cap stops the listener with IsListenerFaulted set instead of spinning.
                    _lastListenerFault = ex.GetType();
                    if (++faults >= MaxConsecutiveFaults) { _listenerFaulted = true; return; }
                    await Task.Delay(FaultRetryDelay, ct).ConfigureAwait(false);
                }
            }
        }
        // Stopping ends the loop normally, so StopAsync never rethrows into Exit or EntryPoint's teardown.
        // A non-cancellation fault that raced with Stop is still kept by type.
        catch (Exception ex) when (ct.IsCancellationRequested) { if (ex is not OperationCanceledException) _lastListenerFault = ex.GetType(); }
    }
    private async Task ServeOneAsync(Func<CancellationToken, Task> restore, CancellationToken ct)
    {
        using var server = new NamedPipeServerStream(_pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(2));
        byte[] message = new byte[8];
        int count;
        try { count = await server.ReadAtLeastAsync(message, 8, throwOnEndOfStream:false, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return; /* An idle local peer gets no indefinite connection. */ }
        catch (IOException) { return; /* A disconnected restore request makes no state change. */ }
        // A restore failure (for example a window that is already closing) reaches ListenAsync, which records it and keeps listening.
        if (count == 8 && message.AsSpan().SequenceEqual("RESTORE\n"u8)) await restore(ct).ConfigureAwait(false);
    }
    public async Task<bool> RestoreExistingAsync(CancellationToken ct)
    {
        if (IsOwner) return false;
        using var client = new NamedPipeClientStream(".", _pipe, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await client.ConnectAsync(1500, ct).ConfigureAwait(false); await client.WriteAsync("RESTORE\n"u8.ToArray(),ct).ConfigureAwait(false); await client.FlushAsync(ct).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
        catch (IOException) { return false; }
    }
    public async Task StopAsync() { _lifetime.Cancel(); await _server.ConfigureAwait(false); }
    public void Dispose()
    {
        if (_disposed) return;
        _lifetime.Cancel();
        if (!_server.IsCompleted) throw new InvalidOperationException("Stop the restore listener before disposing its owner.");
        _disposed = true;
        try { if (IsOwner) _mutex.ReleaseMutex(); }
        finally { _mutex.Dispose(); _lifetime.Dispose(); }
    }
}
