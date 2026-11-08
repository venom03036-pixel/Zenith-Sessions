using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

/// <summary>One page-owned read, no timer or disk cache. Navigation, Lock, Panic and shutdown fence late results.</summary>
public sealed class PublicNewsViewModel : Observable, IAsyncDisposable
{
    private readonly IPublicRobloxNewsFeed _feed;
    private readonly Func<bool> _available;
    private CancellationTokenSource? _request;
    private long _generation;
    private bool _active, _busy, _disposed;
    private Task? _disposeTask;
    private IReadOnlyList<PublicNewsItem> _items = [];
    private string _status = "Open News to load public announcements.";
    public IReadOnlyList<PublicNewsItem> Items { get => _items; private set => Set(ref _items, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool CanRefresh => _active && !_busy && !_disposed && _available();
    public AsyncCommand Refresh { get; }

    public PublicNewsViewModel(IPublicRobloxNewsFeed feed, Func<bool> available, Action<Exception> failed)
    {
        _feed = feed; _available = available;
        Refresh = new(_ => RefreshAsync(), failed);
    }
    public void SetActive(bool active)
    {
        if (_active == active || _disposed) return;
        _active = active; Raise(nameof(CanRefresh));
        if (active) Refresh.Execute(null); else Stop();
    }
    public void Stop()
    {
        _generation++;
        _request?.Cancel();
        Items = [];
        Status = "News loading stopped. Open News or press Refresh when ready.";
        Raise(nameof(CanRefresh));
    }
    public void RefreshAvailability() => Raise(nameof(CanRefresh));
    private async Task RefreshAsync()
    {
        if (!CanRefresh) return;
        long generation = ++_generation;
        using var request = new CancellationTokenSource(); _request = request;
        _busy = true; Raise(nameof(CanRefresh)); Status = "Loading public DevForum announcements…";
        try
        {
            var result = await _feed.ReadAsync(request.Token);
            if (_disposed || !_active || !_available() || generation != _generation || request.IsCancellationRequested) return;
            Items = result.Items; Status = result.Message;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch
        {
            if (!_disposed && generation == _generation) { Items = []; Status = "News could not be loaded. Refresh to try again."; }
            throw;
        }
        finally
        {
            if (ReferenceEquals(_request, request)) _request = null;
            _busy = false; Raise(nameof(CanRefresh));
        }
    }
    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        _disposed = true; Stop();
        await Refresh.Execution;
    }
}
