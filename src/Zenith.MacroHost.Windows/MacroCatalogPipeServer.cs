using System.IO.Pipes;
using Zenith.MacroHost.Contracts;

namespace Zenith.MacroHost.Windows;

/// <summary>One current-user metadata endpoint, one request at a time. Source completion remains owned after cancellation.</summary>
public sealed class MacroCatalogPipeServer : IAsyncDisposable
{
    public const string ProductionPipeName = MacroHostProtocol.PipeName;
    private readonly object _gate = new();
    private readonly IMacroCatalogSource _source;
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private Task? _dispose;
    private int _stopped;
    public Task Completion => _worker;

    public MacroCatalogPipeServer(IMacroCatalogSource source, string? pipeName = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _pipe = new NamedPipeServerStream(MacroCatalogPipeCodec.PipeName(pipeName), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance, 4096, 4096);
        _worker = Task.Run(RunAsync); // No scheduling token: even a pre-start stop must run and own final cleanup.
    }

    public void SignalStop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _stop.Cancel();
        _pipe.Dispose(); // Unblock pipe I/O; an ignoring source is still awaited by the actual worker.
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(MacroCatalogPipeCodec.RequestTimeout);
                try { await HandleAsync(deadline.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or OperationCanceledException or ObjectDisposedException) { }
                // IsConnected is false for Broken too. Disconnect must reset both accepted states before reuse.
                finally { if (!_stop.IsCancellationRequested) _pipe.Disconnect(); }
            }
        }
        catch (Exception ex) when (_stop.IsCancellationRequested && (ex is OperationCanceledException or ObjectDisposedException or IOException)) { }
        finally { _pipe.Dispose(); }
    }

    private async Task HandleAsync(CancellationToken ct)
    {
        var request = await MacroCatalogPipeCodec.ReadRequestAsync(_pipe, ct).ConfigureAwait(false);
        MacroCatalogPage page;
        try
        {
            page = (await _source.GetPageAsync(request.Query, ct).ConfigureAwait(false)).Validated(request.Query);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { page = MacroCatalogPage.Failure(MacroCatalogState.Unavailable); }
        ct.ThrowIfCancellationRequested();
        await MacroCatalogPipeCodec.WriteResponseAsync(_pipe, request.RequestId, page, ct).ConfigureAwait(false);
        // DisconnectNamedPipe discards unread data. A normal client closes only after consuming its response;
        // wait for that EOF within the same deadline, without a blocking WaitForPipeDrain or another worker.
        if (await _pipe.ReadAsync(new byte[1], ct).ConfigureAwait(false) != 0)
            throw new InvalidDataException("The catalog connection permits one request only.");
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            SignalStop();
            return new(_dispose = DisposeCoreAsync());
        }
    }
    private async Task DisposeCoreAsync()
    {
        try { await _worker.ConfigureAwait(false); }
        finally { _stop.Dispose(); }
    }
}
