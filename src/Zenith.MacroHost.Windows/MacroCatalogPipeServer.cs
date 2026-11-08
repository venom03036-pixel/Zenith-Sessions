using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Zenith.MacroHost.Contracts;

namespace Zenith.MacroHost.Windows;

/// <summary>One current-user metadata endpoint, one request at a time. Source completion remains owned after cancellation.</summary>
public sealed class MacroCatalogPipeServer : IAsyncDisposable
{
    public const string ProductionPipeName = MacroHostProtocol.PipeName;
    /// <summary>Back-to-back failed accepts tolerated before the endpoint gives up and faults <see cref="Completion"/>.</summary>
    internal const int MaximumConsecutiveAcceptFailures = 3;
    private readonly object _gate = new();
    private readonly IMacroCatalogSource _source;
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _beforeFirstAccept;
    private readonly Func<NamedPipeServerStream, CancellationToken, Task> _accept;
    private readonly Task _worker;
    private Task? _dispose;
    private int _stopped;
    public Task Completion => _worker;

    public MacroCatalogPipeServer(IMacroCatalogSource source, string? pipeName = null) : this(source, pipeName, Task.CompletedTask) { }

    /// <summary>Test seams: the worker waits for <paramref name="beforeFirstAccept"/> (or stop) before its first accept, and
    /// <paramref name="accept"/> replaces the native accept so a test can force failures no client can cause.</summary>
    internal MacroCatalogPipeServer(IMacroCatalogSource source, string? pipeName, Task beforeFirstAccept,
        Func<NamedPipeServerStream, CancellationToken, Task>? accept = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _beforeFirstAccept = beforeFirstAccept ?? throw new ArgumentNullException(nameof(beforeFirstAccept));
        _accept = accept ?? (static (pipe, ct) => pipe.WaitForConnectionAsync(ct));
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
            await _beforeFirstAccept.WaitAsync(_stop.Token).ConfigureAwait(false);
            int acceptFailures = 0;
            while (!_stop.IsCancellationRequested)
            {
                try { await _accept(_pipe, _stop.Token).ConfigureAwait(false); acceptFailures = 0; }
                catch (IOException) when (!_stop.IsCancellationRequested && acceptFailures < MaximumConsecutiveAcceptFailures)
                {
                    // A client that connected and closed before ConnectNamedPipe leaves the instance closing, so the
                    // accept fails (ERROR_NO_DATA). Reset the same owned instance; the name is never left unowned.
                    // Only the window before the first accept allows that: a reset or disconnected instance refuses
                    // clients until the next accept waits, so clients cannot drive this count to the limit.
                    acceptFailures++;
                    ResetUnacceptedInstance();
                    continue;
                }
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

    private void ResetUnacceptedInstance()
    {
        // NamedPipeServerStream.Disconnect rejects a stream that never connected, so reset the native instance directly.
        if (!DisconnectNamedPipe(_pipe.SafePipeHandle))
            throw new IOException("The macro metadata endpoint could not reset after a failed accept.", Marshal.GetHRForLastWin32Error());
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DisconnectNamedPipe(SafePipeHandle pipe);

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
