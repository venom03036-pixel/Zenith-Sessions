using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

public sealed record PublicAccountPageBinding(Guid ProfileId, long UserId, long AuthorizationGeneration);
public sealed record PublicAccountPageUpdate(long PageGeneration, long UserId,
    IReadOnlyList<PublicAccountPageBinding> Bindings, PublicDirectoryResult<PublicAccountData> Result);

/// <summary>One latest-page pump borrowing one presenter. No row tasks, polling, retries or additional image cache.</summary>
public sealed class PublicAccountPageLoader : IAsyncDisposable
{
    public const int MaximumBindings = 100;
    private readonly object _gate = new();
    private readonly PublicAccountPresentation _presenter;
    private readonly Func<bool> _canUse;
    private readonly Func<PublicAccountPageUpdate, CancellationToken, Task> _publish;
    private readonly Action<Exception> _failure;
    private readonly TimeProvider _clock;
    private IReadOnlyList<PublicAccountPageBinding> _bindings = Array.Empty<PublicAccountPageBinding>();
    private Plan? _pendingPage;
    private Manual? _pendingManual;
    private ActiveRequest? _current;
    private Task _active = Task.CompletedTask;
    private Task? _dispose;
    private bool _pumping, _disposed;
    private long _epoch, _pageGeneration;

    public PublicAccountPageLoader(PublicAccountPresentation presenter, Func<bool> canUse,
        Func<PublicAccountPageUpdate, CancellationToken, Task> publish, Action<Exception>? failure = null, TimeProvider? clock = null)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _canUse = canUse ?? throw new ArgumentNullException(nameof(canUse)); _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _failure = failure ?? (_ => { }); _clock = clock ?? TimeProvider.System;
    }
    public Task ActiveOperation { get { lock (_gate) return _active; } }

    public void ReplacePage(long pageGeneration, IReadOnlyList<PublicAccountPageBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (pageGeneration < 0 || bindings.Count > MaximumBindings) throw new ArgumentException("Use a bounded current account page.");
        var copy = new PublicAccountPageBinding[bindings.Count]; var profiles = new HashSet<Guid>();
        for (int i = 0; i < copy.Length; i++)
        { Validate(bindings[i]); if (!profiles.Add(bindings[i].ProfileId)) throw new ArgumentException("The page contains duplicate profiles."); copy[i] = bindings[i]; }
        ActiveRequest? cancel;
        lock (_gate)
        {
            if (_disposed) return;
            _pageGeneration = pageGeneration; _bindings = Array.AsReadOnly(copy); _epoch++;
            _pendingPage = new(_epoch, pageGeneration, _bindings); CancelPendingManualLocked(); cancel = _current;
            StartLocked();
        }
        cancel?.Cancel();
    }

    public Task<PublicDirectoryResult<PublicAccountData>> RefreshAsync(long pageGeneration, PublicAccountPageBinding binding, CancellationToken ct = default)
    {
        Validate(binding); ct.ThrowIfCancellationRequested();
        if (pageGeneration < 0) throw new ArgumentException("A current page generation is required.");
        ActiveRequest? cancel; Manual manual;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(Unavailable());
            CancelPendingManualLocked(); long epoch = ++_epoch;
            manual = new(new(epoch, pageGeneration, Array.AsReadOnly(new[] { binding })), ct);
            _pendingManual = manual; cancel = _current;
            _pendingPage = _pageGeneration == pageGeneration && _bindings.Count != 0 ? new(epoch, pageGeneration, _bindings) : null;
            StartLocked();
        }
        cancel?.Cancel(); return manual.Completion.Task;
    }
    private static void Validate(PublicAccountPageBinding? binding)
    {
        if (binding is null || binding.ProfileId == Guid.Empty || binding.UserId <= 0 || binding.AuthorizationGeneration <= 0)
            throw new ArgumentException("A verified last-known player/profile/grant binding is required.");
    }
    private void StartLocked()
    {
        if (_pumping) return;
        _pumping = true;
        _active = PumpAsync(); // ForceYielding below publishes this actual owner before callbacks/native work.
    }
    private async Task PumpAsync()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        while (true)
        {
            Plan plan; Manual? manual;
            lock (_gate)
            {
                manual = _pendingManual;
                if (manual is not null) { plan = manual.Plan; _pendingManual = null; }
                else if (_pendingPage is { } page) { plan = page; _pendingPage = null; }
                else { _pumping = false; return; }
            }
            PublicDirectoryResult<PublicAccountData>? final = null;
            try
            {
                foreach (var group in plan.Bindings.GroupBy(binding => binding.UserId))
                {
                    if (!Current(plan) || !_canUse()) break;
                    bool cached = manual is null && _presenter.TryGetCached(group.Key, out _);
                    var request = new ActiveRequest(manual?.CallerToken ?? default, _clock);
                    lock (_gate)
                    {
                        if (_disposed || plan.Epoch != _epoch) { request.Complete(); break; }
                        _current = request;
                    }
                    try
                    {
                        final = await _presenter.FetchAsync(group.Key, request.Token, forceRefresh: manual is not null).ConfigureAwait(false);
                        if (!Current(plan) || !_canUse() || request.Token.IsCancellationRequested) break;
                        var update = new PublicAccountPageUpdate(plan.PageGeneration, group.Key, Array.AsReadOnly(group.ToArray()), final);
                        await _publish(update, request.Token).ConfigureAwait(false);
                        // Never retry a Deferred request in a tight loop, including shared HTTP-slot pressure.
                        if (!Current(plan) || !cached && (final.State == PublicDirectoryState.Deferred
                            || final.Value?.AvatarState == PublicDirectoryState.Deferred)) break;
                    }
                    finally
                    {
                        lock (_gate) if (ReferenceEquals(_current, request)) _current = null;
                        request.Complete();
                    }
                }
                if (manual is not null)
                {
                    if (!Current(plan) || manual.CallerToken.IsCancellationRequested || !_canUse()) manual.Completion.TrySetCanceled();
                    else manual.Completion.TrySetResult(final ?? Unavailable());
                }
            }
            catch (OperationCanceledException)
            { manual?.Completion.TrySetCanceled(); }
            catch (Exception)
            {
                try { _failure(new InvalidOperationException("The public account image request could not be completed.")); } catch { }
                if (manual is not null)
                {
                    if (Current(plan) && _canUse()) manual.Completion.TrySetResult(Unavailable());
                    else manual.Completion.TrySetCanceled();
                }
            }
        }
    }
    private bool Current(Plan plan) { lock (_gate) return !_disposed && plan.Epoch == _epoch; }
    public void CancelWork()
    {
        ActiveRequest? cancel;
        lock (_gate)
        { _epoch++; _pendingPage = null; _bindings = Array.Empty<PublicAccountPageBinding>(); CancelPendingManualLocked(); cancel = _current; }
        cancel?.Cancel();
    }
    private void CancelPendingManualLocked()
    { _pendingManual?.Completion.TrySetCanceled(); _pendingManual = null; }
    public ValueTask DisposeAsync()
    {
        ActiveRequest? cancel; Task disposal;
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            _disposed = true; _epoch++; _pendingPage = null; _bindings = Array.Empty<PublicAccountPageBinding>();
            CancelPendingManualLocked(); cancel = _current; disposal = _dispose = JoinAsync(_active);
        }
        cancel?.Cancel(); return new(disposal);
    }
    private static async Task JoinAsync(Task active) => await active.ConfigureAwait(false);
    private static PublicDirectoryResult<PublicAccountData> Unavailable() => new(PublicDirectoryState.Failure, null, "The public account image is unavailable. Keep the placeholder and refresh explicitly later.");
    private sealed record Plan(long Epoch, long PageGeneration, IReadOnlyList<PublicAccountPageBinding> Bindings);
    private sealed class Manual(Plan plan, CancellationToken callerToken)
    {
        public Plan Plan { get; } = plan;
        public CancellationToken CallerToken { get; } = callerToken;
        public TaskCompletionSource<PublicDirectoryResult<PublicAccountData>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class ActiveRequest
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _deadline, _source;
        private bool _complete; private int _cancelers;
        public CancellationToken Token { get; }
        public ActiveRequest(CancellationToken caller, TimeProvider clock)
        { _deadline = new(TimeSpan.FromSeconds(20), clock); _source = CancellationTokenSource.CreateLinkedTokenSource(caller, _deadline.Token); Token = _source.Token; }
        public void Cancel()
        {
            lock (_gate) { if (_complete) return; _cancelers++; }
            try { _source.Cancel(); } finally { lock (_gate) { if (--_cancelers == 0 && _complete) DisposeSources(); } }
        }
        public void Complete() { lock (_gate) { _complete = true; if (_cancelers == 0) DisposeSources(); } }
        private void DisposeSources() { _source.Dispose(); _deadline.Dispose(); }
    }
}
