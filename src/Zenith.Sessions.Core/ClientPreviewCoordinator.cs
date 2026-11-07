namespace Zenith.Sessions.Core;

/// <summary>
/// One shared opt-in preview owner. One worker/provider call captures at most four targets per
/// batch, with one latest publication and one coalesced request. Overview retains a bounded
/// observed roster. The coordinator owns and disposes its injected provider.
/// </summary>
public sealed class ClientPreviewCoordinator : IAsyncDisposable
{
    private readonly IClientPreviewProvider _provider;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;
    private readonly Dictionary<ClientPreviewTarget, long> _surfaceTimes = new();
    private Request? _request;
    private CaptureOperation? _capture;
    private TaskCompletionSource<bool> _drained = CompletedDrain();
    private ClientPreviewSnapshot _snapshot = new(0, false, Array.Empty<ClientPreviewResult>(), "Client previews are off.");
    private long _generation, _revision;
    private DateTimeOffset? _eligibilityExpiresUtc;
    private string _stoppedExplanation = "Client previews are off.";
    private const string EligibilityExpired = "Preview eligibility expired. Start previews again after fresh resource observations.";
    private bool _providerOwned, _releaseRequested, _releaseFailed, _disposed;
    private Task? _dispose;

    public ClientPreviewCoordinator(IClientPreviewProvider provider, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider; _clock = timeProvider ?? TimeProvider.System;
        _worker = Task.Run(RunAsync, CancellationToken.None);
    }

    /// <summary>The worker's actual lifetime; it ends only after disposal, not after StopAsync.</summary>
    public Task Completion => _worker;
    public Task ActiveOperation { get { lock (_gate) return _capture?.Completion ?? Task.CompletedTask; } }

    /// <summary>Explicit consent for this exact stable target set. A newer Start replaces pending work.</summary>
    public void Start(IReadOnlyList<ClientPreviewTarget> targets, DateTimeOffset? eligibilityExpiresUtc = null)
    {
        var owned = ValidateTargets(targets, allowEmpty: false);
        StartCore(owned, ClientPreviewMode.Selected, eligibilityExpiresUtc);
    }

    /// <summary>Explicit overview consent. Windowless valid processes remain unavailable roster tiles.</summary>
    public void StartOverview(IReadOnlyList<ClientPreviewTarget> targets, DateTimeOffset? eligibilityExpiresUtc = null)
    {
        var owned = ValidateTargets(targets, allowEmpty: true, overview: true);
        StartCore(owned, ClientPreviewMode.Overview, eligibilityExpiresUtc);
    }

    private void StartCore(ClientPreviewTarget[] owned, ClientPreviewMode mode, DateTimeOffset? eligibilityExpiresUtc)
    {
        CaptureOperation? capture;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ReplaceRequest(owned, mode, eligibilityExpiresUtc);
            capture = _capture;
        }
        capture?.Cancel(); _ = StopCore(EligibilityExpired, onlyIfLeaseExpired: true); Signal();
    }

    // The caller holds _gate. Retained frames were already validated and are keyed by exact identity;
    // their original generation and capture time are never changed to match the new request.
    private void ReplaceRequest(ClientPreviewTarget[] owned, ClientPreviewMode mode, DateTimeOffset? expiry)
    {
        var previous = _request;
        var retained = mode == ClientPreviewMode.Overview && previous?.Mode == ClientPreviewMode.Overview
            ? OverviewRows(owned, _snapshot.Results) : Pending(owned);
        ClientPreviewTarget? next = previous is { Mode: ClientPreviewMode.Overview } && previous.Targets.Count != 0
            ? previous.Targets[previous.Cursor % previous.Targets.Count] : null;
        _generation = checked(_generation + 1);
        _request = new(_generation, Array.AsReadOnly(owned), mode);
        if (mode == ClientPreviewMode.Overview && previous?.Mode == ClientPreviewMode.Overview)
        {
            foreach (var key in _surfaceTimes.Keys.ToArray()) if (!owned.Contains(key)) _surfaceTimes.Remove(key);
            if (next is not null)
            {
                int cursor = Array.IndexOf(owned, next);
                if (cursor >= 0) _request.Cursor = cursor;
            }
        }
        else _surfaceTimes.Clear();
        _eligibilityExpiresUtc = expiry; _stoppedExplanation = "Client previews are off.";
        if (_providerOwned) { _releaseRequested = true; EnsureDrainPending(); }
        Publish(_request.Generation, !_releaseRequested, mode == ClientPreviewMode.Overview
                ? OverviewRows(owned, retained) : retained,
            _releaseRequested ? "Previous preview cleanup is pending." : "Client previews requested.", _releaseRequested);
    }

    /// <summary>
    /// Replace an already-active overview roster. Same identities preserve scheduling; actual changes
    /// fence the previous operation and retain only exact unchanged identities. Never restarts stopped work.
    /// </summary>
    public void UpdateOverviewTargets(IReadOnlyList<ClientPreviewTarget> targets)
    {
        ClientPreviewTarget[] owned;
        try { owned = ValidateTargets(targets, allowEmpty: true, overview: true); }
        catch (ArgumentException) { Stop(); return; }
        CaptureOperation? capture = null;
        bool changed = false;
        lock (_gate)
        {
            if (_disposed || _request is not { Mode: ClientPreviewMode.Overview } request) return;
            if (!LeaseExpired() && !SameTargets(request.Targets, owned))
            {
                ReplaceRequest(owned, ClientPreviewMode.Overview, _eligibilityExpiresUtc);
                capture = _capture;
                changed = true;
            }
        }
        capture?.Cancel(); _ = StopCore(EligibilityExpired, onlyIfLeaseExpired: true);
        if (changed) Signal();
    }

    /// <summary>Renew only from actual fresh essential-resource evidence; stopped/expired work never restarts.</summary>
    public void RenewEligibility(DateTimeOffset expiry)
    {
        bool wasExpired;
        lock (_gate)
        {
            if (_disposed || _request is null) return;
            wasExpired = LeaseExpired();
            if (!wasExpired) _eligibilityExpiresUtc = expiry;
        }
        _ = StopCore(EligibilityExpired, onlyIfLeaseExpired: true); Signal();
    }

    /// <summary>
    /// The caller offers its current visible/selected identities. Any change stops capture and
    /// clears pixels; returning to the previous context never restarts it without explicit Start.
    /// </summary>
    public void UpdateTargets(IReadOnlyList<ClientPreviewTarget> targets)
    {
        ClientPreviewTarget[] owned;
        try { owned = ValidateTargets(targets, allowEmpty: true); }
        catch (ArgumentException) { Stop(); return; }
        bool changed;
        lock (_gate) changed = _request is { } request && !SameTargets(request.Targets, owned);
        if (changed) Stop();
    }

    /// <summary>Immediately fences/cancels capture and removes published pixels. Actual cleanup remains owned.</summary>
    public void Stop() { _ = StopCore(); }

    /// <summary>
    /// Clears immediately, then waits for actual provider release. Cancellation only cancels the
    /// caller's wait; the coordinator continues owning cleanup and DisposeAsync still joins it.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!await StopCore().WaitAsync(ct).ConfigureAwait(false)) throw new ClientPreviewException();
    }

    private Task<bool> StopCore(string? reason = null, bool onlyIfLeaseExpired = false)
    {
        CaptureOperation? capture;
        Task<bool> drained;
        lock (_gate)
        {
            if (onlyIfLeaseExpired && !LeaseExpired()) return _drained.Task;
            if (_request is null && (_releaseRequested || _releaseFailed || !_providerOwned)) return _drained.Task;
            _generation = checked(_generation + 1); _request = null; _surfaceTimes.Clear();
            _eligibilityExpiresUtc = null; _stoppedExplanation = reason ?? "Client previews are off.";
            _releaseRequested = _providerOwned;
            if (_releaseRequested) EnsureDrainPending();
            capture = _capture; drained = _drained.Task;
            Publish(_generation, false, Array.Empty<ClientPreviewResult>(),
                reason ?? (_releaseRequested ? "Stopping previews; capture cleanup is pending." : _stoppedExplanation), _releaseRequested);
        }
        capture?.Cancel(); Signal();
        return drained;
    }

    /// <summary>Same immutable object until publication changes, including one-time frame expiry.</summary>
    public ClientPreviewSnapshot GetSnapshot()
    {
        _ = StopCore(EligibilityExpired, onlyIfLeaseExpired: true);
        lock (_gate)
        {
            DateTimeOffset now = _clock.GetUtcNow();
            ClientPreviewResult[]? expired = null;
            for (int index = 0; index < _snapshot.Results.Count; index++)
            {
                var result = _snapshot.Results[index];
                if (result.Frame is not { } frame) continue;
                bool overview = _snapshot.Mode == ClientPreviewMode.Overview;
                if (overview && Retainable(frame.CapturedUtc, now))
                {
                    if (result.Status != ClientPreviewStatus.Ready || Fresh(frame.CapturedUtc, now)) continue;
                    expired ??= _snapshot.Results.ToArray();
                    expired[index] = result with { Status = ClientPreviewStatus.Stale,
                        Explanation = ClientPreviewMessages.For(ClientPreviewStatus.Stale) };
                    continue;
                }
                if (!overview && Fresh(frame.CapturedUtc, now)) continue;
                expired ??= _snapshot.Results.ToArray();
                expired[index] = Result(result.Target, ClientPreviewStatus.NoFrame);
            }
            if (expired is not null) Publish(_snapshot.Generation, _snapshot.Running, Array.AsReadOnly(expired),
                _snapshot.Mode == ClientPreviewMode.Overview ? "Overview frames retain their actual capture times; stale pictures are last-known only."
                    : "The previous preview expired. No fresh pixels are available.", _snapshot.Stopping);
            return _snapshot;
        }
    }

    private async Task RunAsync()
    {
        long? completedAt = null;
        try
        {
            while (true)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                _wake.Wait(0); // The state below subsumes any already-coalesced notification.
                bool release;
                bool expired;
                Request? request;
                lock (_gate) { release = _releaseRequested; request = _request; expired = LeaseExpired(); }
                if (expired) { _ = StopCore(EligibilityExpired, onlyIfLeaseExpired: true); continue; }
                if (release) { await ReleaseProviderAsync().ConfigureAwait(false); continue; }
                if (request is null) { await _wake.WaitAsync(_lifetime.Token).ConfigureAwait(false); continue; }
                if (completedAt is { } last)
                {
                    TimeSpan elapsed = _clock.GetElapsedTime(last, _clock.GetTimestamp());
                    TimeSpan delay = elapsed < TimeSpan.Zero ? ClientPreviewLimits.MinimumBatchInterval
                        : ClientPreviewLimits.MinimumBatchInterval - elapsed;
                    lock (_gate)
                        if (_eligibilityExpiresUtc is { } expiry)
                        {
                            TimeSpan remaining = expiry - _clock.GetUtcNow();
                            if (remaining < delay) delay = remaining;
                        }
                    if (delay > TimeSpan.Zero) { await WaitForChangeOrDelayAsync(delay).ConfigureAwait(false); continue; }
                }
                CaptureOperation operation;
                Dictionary<ClientPreviewTarget, long> previous;
                Batch batch;
                lock (_gate)
                {
                    if (_disposed || _releaseRequested || !ReferenceEquals(request, _request) || LeaseExpired()) continue;
                    batch = SelectBatch(request);
                    operation = new(_lifetime.Token);
                    if (batch.Targets.Count != 0)
                    {
                        _capture = operation; _providerOwned = true;
                        EnsureDrainPending();
                    }
                    previous = new(_surfaceTimes);
                }
                if (batch.Targets.Count == 0)
                {
                    operation.Complete();
                    DateTimeOffset? expiry;
                    lock (_gate) expiry = _eligibilityExpiresUtc;
                    if (expiry is { } deadline)
                        await WaitForChangeOrDelayAsync(deadline - _clock.GetUtcNow()).ConfigureAwait(false);
                    else await _wake.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                int attempted = 0;
                try
                {
                    IReadOnlyList<ClientPreviewResult> validated;
                    if (request.Mode == ClientPreviewMode.Overview && _provider is IClientPreviewBatchProvider progress)
                    {
                        var result = await progress.CaptureBatchAsync(batch.Targets, request.Generation, operation.Token).ConfigureAwait(false);
                        operation.Token.ThrowIfCancellationRequested();
                        bool coverage = ValidCoverage(batch.Targets, result);
                        if (coverage)
                        {
                            var prefix = batch.Targets.Take(result!.AttemptedCount).ToArray();
                            validated = ValidateResults(prefix, request.Generation, result.Results, previous, operation.Token, out bool valid);
                            if (valid) attempted = result.AttemptedCount;
                            else validated = All(batch.Targets, ClientPreviewStatus.Failed);
                        }
                        else validated = All(batch.Targets, ClientPreviewStatus.Failed);
                    }
                    else
                    {
                        var results = await _provider.CaptureAsync(batch.Targets, request.Generation, operation.Token).ConfigureAwait(false);
                        operation.Token.ThrowIfCancellationRequested();
                        validated = ValidateResults(batch.Targets, request.Generation, results, previous, operation.Token, out bool valid);
                        // Without explicit progress, only a complete non-deferred response supports a whole-batch advance.
                        if (valid && results is not null && results.Count == batch.Targets.Count
                            && results.All(result => result.Status is not ClientPreviewStatus.Deferred and not ClientPreviewStatus.Cancelled))
                            attempted = batch.Targets.Count;
                    }
                    operation.Token.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        if (!Current(request, operation)) continue;
                        foreach (var result in validated)
                            if (result.Frame is { } frame) _surfaceTimes[result.Target] = frame.SurfaceTimestampTicks;
                        Publish(request.Generation, true, request.Mode == ClientPreviewMode.Overview
                            ? MergeOverview(request, validated) : validated, "Optional client previews are active.", false);
                    }
                }
                catch (OperationCanceledException) when (operation.Token.IsCancellationRequested) { }
                catch (Exception)
                {
                    // No native/provider exception message or free text is published or retained.
                    lock (_gate)
                        if (Current(request, operation)) Publish(request.Generation, true,
                            request.Mode == ClientPreviewMode.Overview ? MergeOverview(request, All(batch.Targets, ClientPreviewStatus.Failed))
                                : All(request.Targets, ClientPreviewStatus.Failed), "Client preview capture could not finish.", false);
                }
                finally
                {
                    completedAt = _clock.GetTimestamp();
                    lock (_gate)
                    {
                        if (Current(request, operation) && request.Mode == ClientPreviewMode.Overview)
                        {
                            // Zero progress rotates one scheduling position only; it does not fabricate an attempt or timestamp.
                            int advanced = Math.Clamp(attempted, 1, batch.Indices.Count);
                            request.Cursor = (batch.Indices[advanced - 1] + 1) % request.Targets.Count;
                        }
                        if (ReferenceEquals(_capture, operation)) _capture = null;
                    }
                    operation.Complete();
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { await ReleaseProviderAsync().ConfigureAwait(false); }
    }

    private bool Current(Request request, CaptureOperation operation) => !_disposed && !operation.Token.IsCancellationRequested
        && ReferenceEquals(request, _request) && request.Generation == _generation && !_releaseRequested && !LeaseExpired();

    private bool LeaseExpired() => _request is not null && _eligibilityExpiresUtc is { } expiry && expiry <= _clock.GetUtcNow();

    private async Task ReleaseProviderAsync()
    {
        lock (_gate)
        {
            if (!_providerOwned)
            {
                _releaseRequested = false; _releaseFailed = false; _drained.TrySetResult(true);
                return;
            }
        }
        bool success;
        try
        {
            // There is no abandoned cleanup deadline. This executes only on the capture worker,
            // after the actual CaptureAsync owner has completed, even if cancellation was ignored.
            await _provider.ReleaseAsync(CancellationToken.None).ConfigureAwait(false); success = true;
        }
        catch (Exception) { success = false; }
        lock (_gate)
        {
            _releaseRequested = false; _releaseFailed = !success;
            if (success) _providerOwned = false;
            else { _request = null; _surfaceTimes.Clear(); }
            _drained.TrySetResult(success);
            if (!success) Publish(_generation, false, Array.Empty<ClientPreviewResult>(),
                "Preview cleanup failed. Resources remain owned until cleanup or shutdown finishes.", true);
            else if (_request is { } request && !_disposed) Publish(request.Generation, true,
                request.Mode == ClientPreviewMode.Overview ? OverviewRows(request.Targets, _snapshot.Results)
                    : Pending(request.Targets), "Client previews requested.", false);
            else Publish(_generation, false, Array.Empty<ClientPreviewResult>(), _stoppedExplanation, false);
        }
    }

    private async Task WaitForChangeOrDelayAsync(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return;
        if (delay > TimeSpan.FromMilliseconds(uint.MaxValue - 1)) delay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        if (_wake.Wait(0)) return;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Task signal = _wake.WaitAsync(stop.Token), timer = Task.Delay(delay, _clock, stop.Token);
        try { await Task.WhenAny(signal, timer).ConfigureAwait(false); }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(signal, timer).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private IReadOnlyList<ClientPreviewResult> ValidateResults(IReadOnlyList<ClientPreviewTarget> targets, long generation,
        IReadOnlyList<ClientPreviewResult>? results, IReadOnlyDictionary<ClientPreviewTarget, long> previous,
        CancellationToken ct, out bool valid)
    {
        valid = false;
        if (results is null) return All(targets, ClientPreviewStatus.Failed);
        int resultCount = results.Count;
        if (resultCount < 0 || resultCount > targets.Count) return All(targets, ClientPreviewStatus.Failed);
        var output = new ClientPreviewResult[targets.Count];
        var seen = new HashSet<ClientPreviewTarget>();
        for (int index = 0; index < output.Length; index++) output[index] = Result(targets[index], ClientPreviewStatus.NoFrame);
        DateTimeOffset now = _clock.GetUtcNow();
        for (int resultIndex = 0; resultIndex < resultCount; resultIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var result = results[resultIndex];
            if (result is null || result.Target is null || !seen.Add(result.Target) || !Enum.IsDefined(result.Status)
                || result.Status == ClientPreviewStatus.Stale)
                return All(targets, ClientPreviewStatus.Failed);
            int index = -1;
            for (int candidate = 0; candidate < targets.Count; candidate++)
                if (targets[candidate] == result.Target) { index = candidate; break; }
            if (index < 0 || result.Status != ClientPreviewStatus.Ready && result.Frame is not null)
                return All(targets, ClientPreviewStatus.Failed);
            if (result.Status != ClientPreviewStatus.Ready) { output[index] = Result(result.Target, result.Status); continue; }
            if (result.Frame is not { } frame || frame.Target != result.Target || frame.Generation != generation
                || !ValidFrame(frame, now)) return All(targets, ClientPreviewStatus.Failed);
            if (previous.TryGetValue(result.Target, out long timestamp) && frame.SurfaceTimestampTicks <= timestamp)
            { output[index] = Result(result.Target, ClientPreviewStatus.NoFrame); continue; }
            // This detached unpooled array is never modified or reused. Provider/native lifetime
            // ends independently; a UI consumer may retain this immutable publication until replaced.
            var owned = frame with { Pixels = frame.Pixels.ToArray() };
            ct.ThrowIfCancellationRequested();
            output[index] = new(result.Target, ClientPreviewStatus.Ready, owned, ClientPreviewMessages.For(ClientPreviewStatus.Ready));
        }
        valid = true;
        return Array.AsReadOnly(output);
    }

    private static bool ValidCoverage(IReadOnlyList<ClientPreviewTarget> targets, ClientPreviewBatchResult? batch)
    {
        if (batch is null || batch.AttemptedCount < 0 || batch.AttemptedCount > targets.Count
            || batch.Results is null || batch.Results.Count != batch.AttemptedCount) return false;
        for (int index = 0; index < batch.AttemptedCount; index++)
            if (batch.Results[index] is not { } result || result.Target != targets[index]) return false;
        return true;
    }

    private static Batch SelectBatch(Request request)
    {
        if (request.Mode == ClientPreviewMode.Selected)
            return new(request.Targets, Array.AsReadOnly(Enumerable.Range(0, request.Targets.Count).ToArray()));
        var targets = new List<ClientPreviewTarget>(ClientPreviewLimits.MaximumTargets);
        var indices = new List<int>(ClientPreviewLimits.MaximumTargets);
        for (int offset = 0; offset < request.Targets.Count && targets.Count < ClientPreviewLimits.MaximumTargets; offset++)
        {
            int index = (request.Cursor + offset) % request.Targets.Count;
            if (request.Targets[index].Window == 0) continue;
            targets.Add(request.Targets[index]); indices.Add(index);
        }
        return new(targets.AsReadOnly(), indices.AsReadOnly());
    }

    private IReadOnlyList<ClientPreviewResult> OverviewRows(IReadOnlyList<ClientPreviewTarget> targets,
        IReadOnlyList<ClientPreviewResult> previous)
    {
        var output = new ClientPreviewResult[targets.Count];
        DateTimeOffset now = _clock.GetUtcNow();
        long retainedBytes = 0;
        for (int index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            output[index] = Result(target, target.Window == 0 ? ClientPreviewStatus.InvalidClient : ClientPreviewStatus.NoFrame);
            if (target.Window == 0) continue;
            foreach (var old in previous)
            {
                if (old.Target != target) continue;
                if (old.Frame is { } frame && old.Status is ClientPreviewStatus.Ready or ClientPreviewStatus.Stale
                    && Retainable(frame.CapturedUtc, now)
                    && retainedBytes + frame.Pixels.Length <= ClientPreviewLimits.MaximumOverviewPixelBytes)
                {
                    retainedBytes += frame.Pixels.Length;
                    var status = old.Status == ClientPreviewStatus.Ready && Fresh(frame.CapturedUtc, now)
                        ? ClientPreviewStatus.Ready : ClientPreviewStatus.Stale;
                    output[index] = new(target, status, frame, ClientPreviewMessages.For(status));
                }
                else if (old.Frame is null) output[index] = old;
                break;
            }
        }
        return Array.AsReadOnly(output);
    }

    private IReadOnlyList<ClientPreviewResult> MergeOverview(Request request, IReadOnlyList<ClientPreviewResult> updates)
    {
        var output = OverviewRows(request.Targets, _snapshot.Results).ToArray();
        foreach (var update in updates)
        {
            for (int index = 0; index < output.Length; index++)
            {
                if (output[index].Target != update.Target) continue;
                // Only a previously accepted exact-identity frame can survive an unavailable refresh.
                // Invalid, hidden, minimized, unsupported or cancelled sources clear their pixels.
                output[index] = update.Status is ClientPreviewStatus.Deferred or ClientPreviewStatus.NoFrame or ClientPreviewStatus.Failed
                    && output[index].Frame is { } previous
                    ? new(update.Target, ClientPreviewStatus.Stale, previous, ClientPreviewMessages.For(ClientPreviewStatus.Stale))
                    : update;
                break;
            }
        }
        return Array.AsReadOnly(output);
    }

    private static bool ValidFrame(ClientPreviewFrame frame, DateTimeOffset now)
    {
        if (frame.Width is < 1 or > ClientPreviewLimits.MaximumWidth || frame.Height is < 1 or > ClientPreviewLimits.MaximumHeight
            || frame.Stride != frame.Width * 4 || frame.Pixels.Length != frame.Stride * frame.Height
            || frame.SourceWidth is < 1 or > ClientPreviewLimits.MaximumSourceDimension
            || frame.SourceHeight is < 1 or > ClientPreviewLimits.MaximumSourceDimension
            || (long)frame.SourceWidth * frame.SourceHeight > ClientPreviewLimits.MaximumSourcePixels
            || frame.Width > frame.SourceWidth || frame.Height > frame.SourceHeight || frame.SurfaceTimestampTicks <= 0
            || !Fresh(frame.CapturedUtc, now)) return false;
        return Math.Abs((long)frame.Width * frame.SourceHeight - (long)frame.Height * frame.SourceWidth)
            <= Math.Max(frame.SourceWidth, frame.SourceHeight);
    }
    private static bool Fresh(DateTimeOffset captured, DateTimeOffset now) => captured - now <= ClientPreviewLimits.MaximumFutureSkew
        && now - captured <= ClientPreviewLimits.MaximumFrameAge;
    private static bool Retainable(DateTimeOffset captured, DateTimeOffset now) => captured - now <= ClientPreviewLimits.MaximumFutureSkew
        && now - captured <= ClientPreviewLimits.MaximumOverviewFrameAge;

    private static ClientPreviewTarget[] ValidateTargets(IReadOnlyList<ClientPreviewTarget> targets, bool allowEmpty, bool overview = false)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count > (overview ? ClientPreviewLimits.MaximumOverviewTargets : ClientPreviewLimits.MaximumTargets)
            || !allowEmpty && targets.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(targets), overview ? "The observed overview roster exceeds its bounded capacity."
                : "Choose one to four unique observed clients for previews.");
        var owned = new ClientPreviewTarget[targets.Count];
        var processes = new HashSet<SessionProcessKey>(); var windows = new HashSet<nint>();
        for (int index = 0; index < owned.Length; index++)
        {
            var target = targets[index];
            if (target is null || !target.Process.IsValid || !overview && target.Window == 0
                || !processes.Add(target.Process) || target.Window != 0 && !windows.Add(target.Window))
                throw new ArgumentException("Preview targets must have unique valid process instances and windows.", nameof(targets));
            owned[index] = target;
        }
        return owned;
    }
    private static bool SameTargets(IReadOnlyList<ClientPreviewTarget> first, IReadOnlyList<ClientPreviewTarget> second)
    {
        if (first.Count != second.Count) return false;
        foreach (var target in first) if (!second.Contains(target)) return false;
        return true;
    }
    private static ClientPreviewResult Result(ClientPreviewTarget target, ClientPreviewStatus status) => new(target, status, null, ClientPreviewMessages.For(status));
    private static IReadOnlyList<ClientPreviewResult> Pending(IReadOnlyList<ClientPreviewTarget> targets) => All(targets, ClientPreviewStatus.NoFrame);
    private static IReadOnlyList<ClientPreviewResult> All(IReadOnlyList<ClientPreviewTarget> targets, ClientPreviewStatus status)
    {
        var results = new ClientPreviewResult[targets.Count];
        for (int index = 0; index < results.Length; index++) results[index] = Result(targets[index], status);
        return Array.AsReadOnly(results);
    }
    private void Publish(long generation, bool running, IReadOnlyList<ClientPreviewResult> results, string explanation, bool stopping)
        => _snapshot = new(generation, running, results, explanation, checked(++_revision), stopping, _request?.Mode ?? _snapshot.Mode);
    private static TaskCompletionSource<bool> CompletedDrain()
    { var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); result.SetResult(true); return result; }
    private void EnsureDrainPending()
    { if (_drained.Task.IsCompleted) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    private void Signal()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* One pending signal is sufficient for the latest request. */ }
        catch (ObjectDisposedException) { /* Disposal already joined the only worker. */ }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            _disposed = true; _generation = checked(_generation + 1); _request = null; _surfaceTimes.Clear();
            _eligibilityExpiresUtc = null;
            Publish(_generation, false, Array.Empty<ClientPreviewResult>(),
                _providerOwned ? "Stopping previews; capture cleanup is pending." : "Client previews are off.", _providerOwned);
            _dispose = DisposeCoreAsync(_capture);
            return new(_dispose);
        }
    }
    private async Task DisposeCoreAsync(CaptureOperation? capture)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        bool success = true;
        capture?.Cancel();
        try { await _lifetime.CancelAsync().ConfigureAwait(false); }
        catch (Exception) { success = false; }
        Signal();
        try { await _worker.ConfigureAwait(false); }
        catch (Exception) { success = false; }
        try { await _provider.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { success = false; }
        lock (_gate)
        {
            if (success)
            {
                _providerOwned = false; _releaseRequested = false; _releaseFailed = false;
                _drained.TrySetResult(true); _drained = CompletedDrain();
                Publish(_generation, false, Array.Empty<ClientPreviewResult>(), "Client previews are off.", false);
            }
            else Publish(_generation, false, Array.Empty<ClientPreviewResult>(), "Preview cleanup failed during shutdown.", true);
        }
        _lifetime.Dispose(); _wake.Dispose();
        if (!success) throw new ClientPreviewException();
    }

    private sealed record Request(long Generation, IReadOnlyList<ClientPreviewTarget> Targets, ClientPreviewMode Mode)
    { public int Cursor { get; set; } }
    private sealed record Batch(IReadOnlyList<ClientPreviewTarget> Targets, IReadOnlyList<int> Indices);
    private sealed class CaptureOperation(CancellationToken lifetime)
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _done; private int _cancelers;
        public CancellationToken Token => _stop.Token;
        public Task Completion => _completed.Task;
        public void Cancel()
        {
            lock (_gate) { if (_done) return; _cancelers++; }
            try { _stop.Cancel(); }
            catch (AggregateException) { /* Publication is fenced; actual provider cleanup is still awaited. */ }
            finally { lock (_gate) { if (--_cancelers == 0 && _done) _stop.Dispose(); } }
        }
        public void Complete()
        { lock (_gate) { _done = true; if (_cancelers == 0) _stop.Dispose(); _completed.TrySetResult(); } }
    }
}
