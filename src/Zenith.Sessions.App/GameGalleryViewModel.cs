using System.Windows.Media.Imaging;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

/// <summary>Latest operation/outcome; a deferred operation may leave its existing image displayed.</summary>
public enum GameGalleryPresentationState
{
    NotLoaded, Loading, Empty, Unavailable, Deferred, Pending, Blocked, Video, Unsupported,
    DecodeFailed, Cancelled, Cleared, Displayed
}

/// <summary>Borrowed public transport, one owned request, two retained images, no timer or credential access.</summary>
public sealed class GameGalleryViewModel : Observable, IAsyncDisposable
{
    private readonly IPublicRobloxGameGallery? _directory;
    private readonly Func<bool> _canUse;
    private readonly Action<Exception> _failed;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly LinkedList<(long Id, BitmapSource Pixels)> _cache = new();
    private PublicRobloxGameGallery? _gallery;
    private CancellationTokenSource? _stop;
    private Task _active = Task.CompletedTask;
    private Task? _dispose;
    private BitmapSource? _image;
    private long _generation;
    private int _index;
    private bool _closed, _visible = true;
    private DateTimeOffset _notBefore;
    private string _status = "Load an experience to browse its public pictures.";
    private GameGalleryPresentationState _presentationState = GameGalleryPresentationState.NotLoaded;

    public BitmapSource? Image => _image;
    public bool HasImage => _image is not null;
    public bool HasGallery => _gallery?.Items.Count > 0;
    public bool IsBusy => _stop is not null;
    public bool CanBrowse => Allowed && !IsBusy && _gallery?.Items.Count > 1;
    public string Position => _gallery is { Items.Count: > 0 } gallery ? $"{_index + 1} / {gallery.Items.Count}" : "No pictures";
    public string Status => _status;
    public GameGalleryPresentationState PresentationState => _presentationState;
    public bool ShowPlaceholder => _image is null;
    public string EmptyDescription => _status;
    public string EmptyTitle => _presentationState switch
    {
        GameGalleryPresentationState.NotLoaded => "Experience pictures",
        GameGalleryPresentationState.Loading => "Loading pictures",
        GameGalleryPresentationState.Empty => "No public pictures",
        GameGalleryPresentationState.Deferred => "Pictures deferred",
        GameGalleryPresentationState.Pending => "Picture not ready",
        GameGalleryPresentationState.Blocked => "Picture not approved",
        GameGalleryPresentationState.Video => "Video preview unavailable",
        GameGalleryPresentationState.Unsupported => "Unsupported gallery item",
        GameGalleryPresentationState.DecodeFailed => "Picture could not be displayed",
        GameGalleryPresentationState.Cancelled => "Picture loading stopped",
        GameGalleryPresentationState.Cleared => "Pictures cleared",
        GameGalleryPresentationState.Displayed => "",
        _ => "Pictures unavailable"
    };
    public long RetainedPixelBytes => _cache.Count * (long)PublicRobloxGameGalleryLimits.Width * PublicRobloxGameGalleryLimits.Height * 4;
    public Task ActiveOperation => _active;
    public AsyncCommand Previous { get; }
    public AsyncCommand Next { get; }
    private bool Allowed => !_closed && _visible && !_lifetime.IsCancellationRequested && _canUse();

    public GameGalleryViewModel(IPublicRobloxGameGallery? directory, Func<bool> canUse, Action<Exception>? failed = null, TimeProvider? clock = null)
    {
        _directory = directory; _canUse = canUse; _failed = failed ?? (_ => { }); _clock = clock ?? TimeProvider.System;
        if (directory is null) SetOutcome(GameGalleryPresentationState.Unavailable, "Experience galleries are unavailable from this public directory.");
        Previous = new(_ => MoveAsync(-1), ReportFailure); Next = new(_ => MoveAsync(1), ReportFailure);
    }

    public Task LoadAsync(PublicRobloxGame game, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Allowed || IsBusy || _directory is null) return Task.CompletedTask;
        if (game.RequestedPlaceId <= 0 || game.UniverseId <= 0 || !Fresh(game.RetrievedUtc))
            throw new ArgumentException("A current public experience is required.", nameof(game));
        return StartAsync(async (generation, stop) =>
        {
            ClearPixels(); _gallery = null; _index = 0;
            SetOutcome(GameGalleryPresentationState.Loading, "Loading this experience's picture gallery…"); Publish();
            Ensure(generation, stop);
            var result = await _directory.GetGameGalleryAsync(game.RequestedPlaceId, game.UniverseId, stop.Token);
            Ensure(generation, stop); ObserveRetry(result.RetryAfter);
            if (result.State != PublicDirectoryState.Success || result.Value is not { } gallery)
            {
                SetOutcome(result.State == PublicDirectoryState.Deferred ? GameGalleryPresentationState.Deferred : GameGalleryPresentationState.Unavailable,
                    result.State == PublicDirectoryState.Deferred ? "The picture gallery is deferred. Retry game details after the service cooldown." : "Public pictures are unavailable for this experience.");
                Publish(); return;
            }
            gallery = gallery.Validated();
            if (gallery.RequestedPlaceId != game.RequestedPlaceId || gallery.UniverseId != game.UniverseId || !Fresh(gallery.RetrievedUtc)
                || gallery.Items.Any(item => !Fresh(item.RetrievedUtc))) throw new InvalidDataException("Gallery context did not match the selected experience.");
            _gallery = gallery;
            if (gallery.Items.Count == 0) { SetOutcome(GameGalleryPresentationState.Empty, "This experience has no public gallery pictures."); Publish(); return; }
            await ReadSlideAsync(generation, stop);
        }, ct, "Loading this experience's picture gallery…");
    }

    public Task MoveAsync(int direction, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (!CanBrowse || _gallery is null) return Task.CompletedTask;
        if (!Fresh(_gallery.RetrievedUtc))
        { CancelWork("These public pictures expired. Load game details again to refresh the gallery."); return Task.CompletedTask; }
        int next = (_index + direction + _gallery.Items.Count) % _gallery.Items.Count;
        return StartAsync(async (generation, stop) =>
        {
            _index = next; _image = null; SetOutcome(GameGalleryPresentationState.Loading, "Loading picture…"); Publish();
            Ensure(generation, stop); await ReadSlideAsync(generation, stop);
        }, ct, "Loading picture…");
    }

    private Task StartAsync(Func<long, CancellationTokenSource, Task> body, CancellationToken ct, string loadingStatus)
    {
        if (_clock.GetUtcNow() < _notBefore)
        { SetOutcome(GameGalleryPresentationState.Deferred, "Pictures are cooling down at the public service. Wait before trying again."); Publish(); return Task.CompletedTask; }
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ct);
        _stop = stop; long generation = ++_generation;
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task active = _active = RunAsync(generation, stop, body, published.Task);
        SetOutcome(GameGalleryPresentationState.Loading, loadingStatus);
        try { Publish(); } finally { published.TrySetResult(); }
        return active;
    }

    private async Task RunAsync(long generation, CancellationTokenSource stop, Func<long, CancellationTokenSource, Task> body, Task published)
    {
        await published;
        try { Ensure(generation, stop); await body(generation, stop); }
        catch (OperationCanceledException)
        {
            if (!_closed && generation == _generation) { _image = null; SetOutcome(GameGalleryPresentationState.Cancelled, "Picture loading stopped."); Publish(); }
        }
        catch (Exception error)
        {
            if (!_closed && generation == _generation && Allowed)
            { _image = null; SetOutcome(GameGalleryPresentationState.Unavailable, "This picture could not be displayed. Other experience details were kept."); Publish(); ReportFailure(error); }
        }
        finally
        {
            if (ReferenceEquals(_stop, stop)) _stop = null;
            stop.Dispose(); Publish();
        }
    }

    private async Task ReadSlideAsync(long generation, CancellationTokenSource stop)
    {
        Ensure(generation, stop);
        var item = _gallery!.Items[_index];
        if (item.State != PublicRobloxGameGalleryState.Ready || item.Kind != PublicRobloxGameGalleryKind.Image)
        {
            _image = null;
            var state = item.Kind == PublicRobloxGameGalleryKind.Video ? GameGalleryPresentationState.Video
                : item.Kind != PublicRobloxGameGalleryKind.Image ? GameGalleryPresentationState.Unsupported
                : item.State switch
                {
                    PublicRobloxGameGalleryState.Pending => GameGalleryPresentationState.Pending,
                    PublicRobloxGameGalleryState.Blocked => GameGalleryPresentationState.Blocked,
                    PublicRobloxGameGalleryState.Unsupported => GameGalleryPresentationState.Unsupported,
                    _ => GameGalleryPresentationState.Unavailable
                };
            string status = state switch
            {
                GameGalleryPresentationState.Pending => "This picture is still being prepared by Roblox. Try loading the gallery later.",
                GameGalleryPresentationState.Blocked => "This picture is not approved for public display.",
                GameGalleryPresentationState.Video => "This gallery item is a video. Use the arrows to browse pictures.",
                GameGalleryPresentationState.Unsupported => "This gallery item uses unsupported media. Use the arrows to browse pictures.",
                _ => "This gallery picture is currently unavailable."
            };
            SetOutcome(state, status); Publish(); return;
        }
        var node = _cache.First;
        while (node is not null && node.Value.Id != item.ThumbnailId) node = node.Next;
        BitmapSource? pixels;
        var outcome = GameGalleryPresentationState.Unavailable;
        if (node is not null) { pixels = node.Value.Pixels; _cache.Remove(node); _cache.AddFirst(node); }
        else
        {
            var result = await _directory!.FetchGameGalleryImageAsync(item, stop.Token);
            Ensure(generation, stop); ObserveRetry(result.RetryAfter);
            pixels = null;
            if (result.State == PublicDirectoryState.Deferred) outcome = GameGalleryPresentationState.Deferred;
            else if (result.State == PublicDirectoryState.Success && result.Value is { } encoded && Fresh(encoded.RetrievedUtc))
            {
                outcome = GameGalleryPresentationState.DecodeFailed;
                try { pixels = await Task.Run(() => PublicAccountPresentation.DecodeGalleryPng(encoded.Bytes, encoded.MediaType, stop.Token), CancellationToken.None); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    Ensure(generation, stop);
                    _image = null; SetOutcome(GameGalleryPresentationState.DecodeFailed, "This picture could not be displayed. Other experience details were kept.");
                    Publish(); ReportFailure(error); return;
                }
            }
            Ensure(generation, stop);
            if (pixels is not null)
            {
                while (_cache.Count >= 2) _cache.RemoveLast();
                _cache.AddFirst((item.ThumbnailId!.Value, pixels));
            }
        }
        // Set all state before notification; a reentrant hide/cancel must be the last writer.
        _image = pixels;
        SetOutcome(pixels is not null ? GameGalleryPresentationState.Displayed : outcome,
            pixels is not null ? "Official experience picture · not a live client preview."
            : outcome == GameGalleryPresentationState.Deferred ? "This public picture is deferred. Wait for the service cooldown before retrying."
            : "This public picture is unavailable or failed image validation. Use the arrows for other pictures.");
        Publish();
    }

    private void Ensure(long generation, CancellationTokenSource stop)
    { stop.Token.ThrowIfCancellationRequested(); if (!Allowed || generation != _generation) throw new OperationCanceledException(); }
    private bool Fresh(DateTimeOffset time) => time != default && time <= _clock.GetUtcNow().AddMinutes(5) && _clock.GetUtcNow() - time <= TimeSpan.FromMinutes(30);
    private void ObserveRetry(TimeSpan? retry)
    {
        if (retry is not { } delay || delay <= TimeSpan.Zero) return;
        var now = _clock.GetUtcNow(); var until = delay > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;
        if (until > _notBefore) _notBefore = until;
    }
    private void ReportFailure(Exception _) => _failed(new InvalidOperationException("Public gallery media could not be displayed."));
    private void SetOutcome(GameGalleryPresentationState state, string status) { _presentationState = state; _status = status; }
    private void ClearPixels() { _image = null; _cache.Clear(); }
    private void Publish()
    {
        foreach (string name in new[] { nameof(Image), nameof(HasImage), nameof(HasGallery), nameof(IsBusy), nameof(CanBrowse), nameof(Position), nameof(Status), nameof(RetainedPixelBytes),
            nameof(PresentationState), nameof(EmptyTitle), nameof(EmptyDescription), nameof(ShowPlaceholder) }) Raise(name);
    }
    public void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        if (!visible) CancelWork(); else Publish();
    }
    public void CancelWork() => CancelWork(_directory is null ? "Experience galleries are unavailable from this public directory." : "Pictures cleared. Load game details when ready.");
    private void CancelWork(string status)
    {
        ++_generation; _stop?.Cancel(); _gallery = null; _index = 0; ClearPixels();
        SetOutcome(_directory is null ? GameGalleryPresentationState.Unavailable : GameGalleryPresentationState.Cleared, status); Publish();
    }
    public ValueTask DisposeAsync()
    {
        if (_dispose is not null) return new(_dispose);
        _closed = true;
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispose = StopAsync(_active, published.Task);
        try { _lifetime.Cancel(); CancelWork(); } finally { published.TrySetResult(); }
        return new(_dispose);
    }
    private async Task StopAsync(Task active, Task published)
    { await published; try { await active; } finally { _lifetime.Dispose(); } }
}
