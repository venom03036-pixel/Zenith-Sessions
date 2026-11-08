using System.IO.Pipes;
using System.Security.Principal;
using Zenith.MacroHost.Contracts;

namespace Zenith.MacroHost.Windows;

/// <summary>Explicit metadata requests only. One owned call; competitors receive Busy without waiting.
/// A connected request that misses the deadline is also Busy. Absence, early closure or local disposal is Disconnected;
/// the wire cannot distinguish remote shutdown from a timeout after the client's budget has elapsed.</summary>
public sealed class MacroCatalogPipeClient : IZenithMacroCatalogClient, IAsyncDisposable
{
    public const string ProductionPipeName = MacroHostProtocol.PipeName;
    private readonly object _gate = new();
    private readonly string _pipeName;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private Task<MacroCatalogPage>? _active;
    private Task? _dispose;
    private bool _busy, _disposed;
    public MacroCatalogPipeClient(string? pipeName = null) : this(pipeName, TimeProvider.System) { }
    internal MacroCatalogPipeClient(string? pipeName, TimeProvider clock)
    {
        _pipeName = MacroCatalogPipeCodec.PipeName(pipeName);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public Task<MacroCatalogPage> GetPageAsync(MacroCatalogQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query); query = query.Validated(); ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(MacroCatalogPage.Failure(MacroCatalogState.Disconnected));
            if (_busy) return Task.FromResult(MacroCatalogPage.Failure(MacroCatalogState.Busy));
            _busy = true;
            return _active = Task.Run(() => ReadOwnedAsync(query, ct));
        }
    }

    private async Task<MacroCatalogPage> ReadOwnedAsync(MacroCatalogQuery query, CancellationToken ct)
    {
        long started = _clock.GetTimestamp();
        using var timeout = new CancellationTokenSource(MacroCatalogPipeCodec.RequestTimeout, _clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token, timeout.Token);
        bool Expired() => timeout.IsCancellationRequested || _clock.GetElapsedTime(started) >= MacroCatalogPipeCodec.RequestTimeout;
        bool connected = false;
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Anonymous);
            await pipe.ConnectAsync(1000, deadline.Token).ConfigureAwait(false);
            connected = true;
            Guid requestId = Guid.NewGuid();
            await MacroCatalogPipeCodec.WriteRequestAsync(pipe, requestId, query, deadline.Token).ConfigureAwait(false);
            var page = await MacroCatalogPipeCodec.ReadResponseAsync(pipe, requestId, query, deadline.Token).ConfigureAwait(false);
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                if (_disposed) return MacroCatalogPage.Failure(MacroCatalogState.Disconnected);
                return Expired() ? MacroCatalogPage.Failure(MacroCatalogState.Busy) : page;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // Due timer callbacks need not run in deadline order. The host can close at its deadline before this client's
        // callback runs, so measure the actual monotonic budget too. An early EOF, local disposal or absent host stays
        // Disconnected; Busy after expiry says only that this connected request missed its deadline, not that the host lives.
        catch (Exception ex) when ((ex is OperationCanceledException or IOException) && connected
            && Expired() && !ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        { return MacroCatalogPage.Failure(MacroCatalogState.Busy); }
        catch (OperationCanceledException) { return MacroCatalogPage.Failure(MacroCatalogState.Disconnected); }
        catch (TimeoutException) { return MacroCatalogPage.Failure(MacroCatalogState.Disconnected); }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        { return MacroCatalogPage.Failure(MacroCatalogState.Incompatible); }
        catch (UnauthorizedAccessException) { return MacroCatalogPage.Failure(MacroCatalogState.Unavailable); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        { return MacroCatalogPage.Failure(MacroCatalogState.Disconnected); }
        finally { lock (_gate) _busy = false; }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            _disposed = true; _lifetime.Cancel();
            return new(_dispose = DisposeCoreAsync(_active));
        }
    }
    private async Task DisposeCoreAsync(Task<MacroCatalogPage>? active)
    {
        try { if (active is not null) { try { await active.ConfigureAwait(false); } catch (OperationCanceledException) { } } }
        finally { _lifetime.Dispose(); }
    }
}
