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
    public bool IsOwner { get; }
    public SingleInstanceCoordinator(string root)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string scope = (identity.User?.Value ?? throw new InvalidOperationException("Windows identity is unavailable.")) + "|" + Path.GetFullPath(root).TrimEnd('\\').ToUpperInvariant();
        _pipe = "ZenithSessions." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..24];
        _mutex = new Mutex(initiallyOwned:true, "Local\\" + _pipe, out bool created); IsOwner = created;
    }
    public void Listen(Func<CancellationToken, Task> restore)
    { if (!IsOwner || !_server.IsCompleted) throw new InvalidOperationException("The instance cannot start another listener."); _server = ListenAsync(restore, _lifetime.Token); }
    private async Task ListenAsync(Func<CancellationToken, Task> restore, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var server = new NamedPipeServerStream(_pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(2));
                byte[] message = new byte[8];
                try
                {
                    int count = await server.ReadAtLeastAsync(message, 8, throwOnEndOfStream:false, deadline.Token).ConfigureAwait(false);
                    if (count == 8 && message.AsSpan().SequenceEqual("RESTORE\n"u8)) await restore(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* An idle local peer gets no indefinite connection. */ }
                catch (IOException) { /* A disconnected restore request makes no state change. */ }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
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
        _lifetime.Cancel();
        if (!_server.IsCompleted) throw new InvalidOperationException("Stop the restore listener before disposing its owner.");
        if (IsOwner) _mutex.ReleaseMutex();
        _mutex.Dispose(); _lifetime.Dispose();
    }
}
