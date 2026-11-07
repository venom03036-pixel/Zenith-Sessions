using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows.Media.Imaging;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

/// <summary>One explicit public lookup and one retained 150px icon. No launch, vault or target mutation.</summary>
public sealed class GameDetailsViewModel : Observable, IAsyncDisposable
{
    private readonly IPublicRobloxGameDirectory? _directory;
    private readonly ServerBrowserViewModel _browser;
    private readonly Func<bool> _canUse;
    private readonly Action<Exception> _failed;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _stop;
    private Task _active = Task.CompletedTask;
    private Task? _dispose;
    private long _generation;
    private long? _place;
    private DateTimeOffset _notBefore;
    private bool _busy, _disposed;
    private PublicRobloxGame? _game;
    private BitmapSource? _icon;
    private string _status;
    public PublicRobloxGame? Game => _game;
    public BitmapSource? Icon { get => _icon; private set { if (Set(ref _icon, value)) Raise(nameof(HasIcon)); } }
    public bool HasIcon => Icon is not null;
    public bool HasGame => Game is not null;
    public string Title => Game?.Name ?? "Experience details";
    public string Creator => Game is { } game ? $"By {game.CreatorName} · {game.CreatorKind}" : "Public name and creator have not been loaded.";
    public string Identifiers => Game is { } game ? $"Requested place {game.RequestedPlaceId} · universe {game.UniverseId} · root place {game.RootPlaceId}" : "Your requested place and server remain unchanged.";
    // Visits and the private-server flag appear only when Roblox listed them; unknown is omitted, never shown as 0 or "not allowed".
    public string Activity => Game is { } game ? $"{game.Playing:N0} players across the experience · listing server limit {game.MaxPlayers:N0}"
        + (game.Visits is { } visits ? $" · {visits:N0} visits" : "")
        + (game.PrivateServersAllowed is { } allowed ? (allowed ? " · private servers allowed" : " · private servers not allowed") : "") : "Activity unavailable";
    public string Observed => Game is { } game ? "Public metadata retrieved " + game.RetrievedUtc.ToUniversalTime().ToString("u") : "";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) RefreshAvailability(); } }
    public bool CanLoad => !_disposed && !IsBusy && _directory is not null && _browser.Destination is not null && _canUse();
    public Task ActiveOperation => _active;
    public GameGalleryViewModel Gallery { get; }
    public AsyncCommand Load { get; }
    public Command Cancel { get; }

    public GameDetailsViewModel(IPublicRobloxGameDirectory? directory, ServerBrowserViewModel browser, Func<bool> canUse,
        Action<Exception>? failed = null, TimeProvider? clock = null)
    {
        _directory = directory; _browser = browser; _canUse = canUse; _failed = failed ?? (_ => { }); _clock = clock ?? TimeProvider.System;
        Gallery = new(directory as IPublicRobloxGameGallery, canUse, _failed, _clock);
        _place = browser.Destination?.PlaceId;
        _status = directory is null ? "This public directory does not support game details." : "Choose a destination, then load its public experience details.";
        Load = new(_ => LoadAsync(), ReportFailure); Cancel = new(_ => CancelWork(clear: true));
        browser.PropertyChanged += DestinationChanged;
    }
    public void RefreshAvailability() => Raise(nameof(CanLoad));
    private void DestinationChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ServerBrowserViewModel.Destination)) return;
        long? place = _browser.Destination?.PlaceId;
        if (place != _place) { _place = place; CancelWork(clear: true); }
        RefreshAvailability();
    }
    public Task LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!CanLoad || _browser.Destination is not { } destination) return Task.CompletedTask;
        if (_clock.GetUtcNow() < _notBefore) { Status = "The public service requested a cooldown. Wait before trying again; no request was sent."; return Task.CompletedTask; }
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ct);
        _stop = stop; long generation = _generation; _busy = true;
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task active = _active = ReadAsync(destination.PlaceId, generation, stop, published.Task);
        try
        {
            _game = null; _icon = null; _status = "Loading public experience details…";
            Gallery.CancelWork();
            RaiseGame(); Raise(nameof(Icon)); Raise(nameof(HasIcon)); Raise(nameof(Status)); Raise(nameof(IsBusy)); RefreshAvailability();
        }
        finally { published.TrySetResult(); }
        return active;
    }
    private async Task ReadAsync(long placeId, long generation, CancellationTokenSource stop, Task published)
    {
        bool Current() => !_disposed && !stop.IsCancellationRequested && generation == _generation
            && _canUse() && _browser.Destination?.PlaceId == placeId;
        try
        {
            await published;
            await Gallery.ActiveOperation;
            stop.Token.ThrowIfCancellationRequested();
            if (!Current()) return;
            var result = await _directory!.LookupGameAsync(placeId, stop.Token);
            if (!Current()) return;
            ObserveRetry(result.RetryAfter);
            if (result.State != PublicDirectoryState.Success || result.Value is not { } game)
            { Status = result.State == PublicDirectoryState.Deferred ? "Public game details are deferred. Try again after the service cooldown." : "Public game details are unavailable. The destination request was preserved."; return; }
            if (!ValidGame(game, placeId)) throw new InvalidDataException("The public game response did not match the requested place.");
            _game = game; _status = "Public details loaded. Loading the optional icon…";
            RaiseGame(); Raise(nameof(Status));
            if (!Current()) return;
            BitmapSource? pixels = null;
            try { pixels = await ReadIconAsync(game, Current, stop.Token); }
            catch (Exception error) when (error is not OperationCanceledException)
            { _failed(new InvalidOperationException("The optional public game icon could not be displayed.")); }
            if (!Current()) return;
            _icon = pixels;
            _status = pixels is null ? "Public details loaded; the icon was unavailable or failed image validation. No account or server was verified."
                : "Public experience details and icon loaded. This does not verify access, a native account, server capacity or arrival.";
            Raise(nameof(Icon)); Raise(nameof(HasIcon)); Raise(nameof(Status));
            if (Current()) await Gallery.LoadAsync(game, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { if (!_disposed && generation == _generation) { _game = null; _icon = null; _status = "Game lookup cancelled. The destination request was preserved."; RaiseGame(); Raise(nameof(Icon)); Raise(nameof(HasIcon)); Raise(nameof(Status)); } }
        catch (Exception error)
        { if (generation == _generation && !_disposed && _canUse()) ReportFailure(error); else _failed(error); }
        finally
        {
            if (ReferenceEquals(_stop, stop)) _stop = null;
            stop.Dispose(); IsBusy = false;
        }
    }
    private async Task<BitmapSource?> ReadIconAsync(PublicRobloxGame game, Func<bool> current, CancellationToken ct)
    {
        var descriptor = await _directory!.GetGameIconAsync(game.UniverseId, ct);
        if (!current()) return null;
        ObserveRetry(descriptor.RetryAfter);
        if (descriptor.State != PublicDirectoryState.Success || descriptor.Value is not { } icon) return null;
        if (icon.UniverseId != game.UniverseId || icon.Width != 150 || icon.Height != 150 || !Fresh(icon.RetrievedUtc))
            throw new InvalidDataException("The public icon did not match this experience.");
        var encoded = await _directory.FetchGameIconAsync(icon, ct);
        if (!current()) return null;
        ObserveRetry(encoded.RetryAfter);
        return encoded.State == PublicDirectoryState.Success && encoded.Value is { } bytes && Fresh(bytes.RetrievedUtc)
            ? await Task.Run(() => PublicAccountPresentation.DecodePublicPng(bytes.Bytes, bytes.MediaType, ct), CancellationToken.None) : null;
    }
    private bool Fresh(DateTimeOffset time) => time <= _clock.GetUtcNow().AddMinutes(5) && _clock.GetUtcNow() - time <= TimeSpan.FromMinutes(30);
    private bool ValidGame(PublicRobloxGame game, long requested) => game.RequestedPlaceId == requested && game.UniverseId > 0 && game.RootPlaceId > 0
        && game.CreatorId > 0 && Enum.IsDefined(game.CreatorKind) && game.Playing >= 0 && game.MaxPlayers > 0 && (game.Visits is null or >= 0) && Fresh(game.RetrievedUtc)
        && SafeText(game.Name, 256) && SafeText(game.CreatorName, 100);
    private static bool SafeText(string? text, int maximum)
    {
        if (text is not { Length: > 0 } || text.Length > maximum || text.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format)) return false;
        try { new UTF8Encoding(false, true).GetByteCount(text); return true; } catch (EncoderFallbackException) { return false; }
    }
    private void ObserveRetry(TimeSpan? retry)
    {
        if (retry is not { } delay || delay <= TimeSpan.Zero) return;
        var now = _clock.GetUtcNow(); var deadline = delay > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;
        if (deadline > _notBefore) _notBefore = deadline;
    }
    private void ReportFailure(Exception error)
    { Status = "The public game lookup could not finish. Your destination and profiles were preserved."; _failed(error); }
    private void ClearDisplay() { _game = null; Icon = null; RaiseGame(); }
    private void RaiseGame()
    { foreach (string property in new[] { nameof(Game), nameof(HasGame), nameof(Title), nameof(Creator), nameof(Identifiers), nameof(Activity), nameof(Observed) }) Raise(property); }
    public void CancelWork(bool clear = true, bool clearCache = false)
    {
        Interlocked.Increment(ref _generation); _stop?.Cancel();
        _status = _directory is null ? "This public directory does not support game details." : "Game details cleared. Load explicitly when controls are available; the destination request was preserved.";
        Gallery.CancelWork();
        if (clear) ClearDisplay();
        if (clearCache)
            try { _directory?.ClearMemoryCache(); }
            catch (Exception error) { _failed(error); }
        RefreshAvailability();
        Raise(nameof(Status));
    }
    public ValueTask DisposeAsync()
    {
        if (_dispose is not null) return new(_dispose);
        _disposed = true; _browser.PropertyChanged -= DestinationChanged;
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispose = StopAsync(_active, published.Task);
        try { _lifetime.Cancel(); CancelWork(clear: true, clearCache: true); }
        finally { published.TrySetResult(); }
        return new(_dispose);
    }
    private async Task StopAsync(Task active, Task published)
    {
        await published;
        try { await active; } catch (OperationCanceledException) { }
        finally
        {
            try { await Gallery.DisposeAsync(); }
            finally { _lifetime.Dispose(); }
        }
    }
}
