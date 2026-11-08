using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// Explicit, transient native installation selection. Capability remains Unverified. The real
/// constructor owns one shared trust verifier; preparer, capture and vault remain caller-owned.
/// Changing selection never abandons an existing preparation, native start or child cleanup.
/// </summary>
public sealed class WindowsConfiguredNativeLaunchProvider : IPreparingLaunchProvider, INativeLaunchDiagnosticSource, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<ClientInstallationCandidate, IConfiguredNativeLaunchChild> _factory;
    private readonly IAsyncDisposable _trustOwner;
    private readonly CancellationTokenSource _lifetime = new();
    private ClientInstallationCandidate? _selected;
    private Owner? _active;
    private Task? _dispose;
    private long _generation;
    private bool _disposed, _poisoned;
    private readonly Dictionary<Guid, DiagnosticEntry> _diagnostics = new();
    private long _diagnosticRevision, _diagnosticAttempt;

    /// <param name="extraArguments">Where each account's saved extra client arguments (R-launch-16) are read at preparation time;
    /// null passes none. Only the direct player this provider accepts ever receives them, never a bootstrapper.</param>
    public WindowsConfiguredNativeLaunchProvider(IRobloxNativeHandoffPreparer preparer, IRobloxNativeHandoffCapture capture,
        IAuthorizationDispatchVault dispatchVault, IExtraClientArgumentsSource? extraArguments = null)
    {
        ArgumentNullException.ThrowIfNull(preparer); ArgumentNullException.ThrowIfNull(capture); ArgumentNullException.ThrowIfNull(dispatchVault);
        var trust = new WindowsClientExecutableTrust(); _trustOwner = trust;
        _factory = candidate => new RealChild(new(candidate, trust, preparer, capture, dispatchVault, extraArguments));
    }
    // The injected owner is transferred to this wrapper. Factories/children must not own borrowed
    // authentication/browser/vault services. Tests use memory-only children and a tracked owner.
    internal WindowsConfiguredNativeLaunchProvider(Func<ClientInstallationCandidate, IConfiguredNativeLaunchChild> childFactory,
        IAsyncDisposable trustOwner)
    { _factory = childFactory ?? throw new ArgumentNullException(nameof(childFactory)); _trustOwner = trustOwner ?? throw new ArgumentNullException(nameof(trustOwner)); }

    public ProviderCapability AccountLaunch { get; } = new("Native account launch", CapabilityState.Unverified,
        "An explicitly selected native installation can be prepared for a separately consented trial. Account selection, browser/native compatibility and arrival remain unverified; ordinary launching stays disabled.");
    public bool Selected { get { lock (_gate) return !_disposed && _selected is not null; } }
    public bool CleanupHealthy
    {
        get
        {
            Owner? active; lock (_gate) { if (_poisoned) return false; active = _active; }
            if (active is not null) CheckChildHealth(active);
            lock (_gate) return !_poisoned;
        }
    }
    public bool IsBusy { get { lock (_gate) return _active is not null; } }

    /// <summary>
    /// True when <see cref="Configure"/> would accept this candidate as the trial installation. It runs the
    /// same check (direct-player filename, Roblox-only evidence, safe full path, readable metadata), so a
    /// caller can tell an inspect-only row (the launcher, a bootstrapper-managed player) from a real change.
    /// </summary>
    public static bool CanSelect(ClientInstallationCandidate? candidate)
    {
        if (candidate is null) return false;
        try { CopySelection(candidate); return true; }
        catch (ArgumentException) { return false; }
    }
    public void Configure(ClientInstallationCandidate? candidate)
    {
        var selected = candidate is null ? null : CopySelection(candidate); Owner? active;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _generation++; _selected = selected; active = _active;
            if (active is not null) Interlocked.Exchange(ref active.Fenced, 1);
        }
        Stop(active);
    }
    public void CancelActive()
    {
        Owner? active;
        lock (_gate)
        {
            if (_disposed) return;
            _generation++; active = _active;
            if (active is not null) Interlocked.Exchange(ref active.Fenced, 1);
        }
        Stop(active);
    }
    public Task<ProviderLaunchResult> LaunchAsync(LaunchIntent intent, CancellationToken ct)
    { ArgumentNullException.ThrowIfNull(intent); ct.ThrowIfCancellationRequested(); return Task.FromResult(Rejected()); }
    public Task<bool> ConfirmArrivalAsync(LaunchIntent intent, ProviderLaunchResult observed, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(false); }

    public Task<LaunchPreparationResult> PrepareAsync(LaunchPreparationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context); ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_poisoned) return Task.FromResult(new LaunchPreparationResult(LaunchPreparationFailure.CleanupFailed));
            if (_selected is null || _active is not null) return Task.FromResult(new LaunchPreparationResult(LaunchPreparationFailure.Rejected));
            var owner = new Owner(context, _selected, _generation, CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token));
            _active = owner;
            BeginDiagnostic(owner);
            var work = Task.Run(() => PrepareOwnedAsync(owner), CancellationToken.None);
            owner.PreparationWork = work; return work;
        }
    }
    private async Task<LaunchPreparationResult> PrepareOwnedAsync(Owner owner)
    {
        bool transferred = false; var failure = LaunchPreparationFailure.Rejected;
        try
        {
            owner.Token.ThrowIfCancellationRequested();
            var child = _factory(owner.Selection) ?? throw new InvalidOperationException("Native preparation could not be created.");
            Volatile.Write(ref owner.Child, child);
            var result = await child.PrepareAsync(owner.Context, owner.Token).ConfigureAwait(false);
            CopyChildDiagnostic(owner, child);
            Volatile.Write(ref owner.Prepared, result?.Prepared);
            // A cleanup failure has priority over cancellation and late configuration changes.
            if (result?.Failure == LaunchPreparationFailure.CleanupFailed) Poison(owner);
            CheckChildHealth(owner);
            failure = result?.Failure is LaunchPreparationFailure.Expired ? LaunchPreparationFailure.Expired : LaunchPreparationFailure.Rejected;
            if (result?.Failure == LaunchPreparationFailure.None && result.Prepared is { } prepared)
            {
                if (prepared.IsExpired) failure = LaunchPreparationFailure.Expired;
                else if (ReferenceEquals(prepared.Context, owner.Context)) lock (_gate)
                {
                    if (Current(owner))
                    {
                        var handle = new WrappedLaunch(this, owner, prepared);
                        owner.Handle = handle; transferred = true;
                        UpdateDiagnostic(owner, value => value with { Stage = NativeLaunchDiagnosticStage.Prepared });
                        return new(LaunchPreparationFailure.None, handle);
                    }
                }
            }
        }
        catch (Exception) { /* No child/provider/credential exception text crosses this boundary. */ }
        finally { if (!transferred) await CleanupOwnerAsync(owner).ConfigureAwait(false); }
        return new(owner.CleanupFailed ? LaunchPreparationFailure.CleanupFailed : failure);
    }
    // Called under _gate, or from an already fenced owner with the same immutable generation.
    private bool Current(Owner owner) => !_disposed && !_poisoned && ReferenceEquals(_active, owner)
        && owner.Generation == _generation && Volatile.Read(ref owner.Fenced) == 0 && !owner.Token.IsCancellationRequested;
    private bool IsCurrent(Owner owner) { lock (_gate) return Current(owner); }
    private void Poison(Owner owner)
    { lock (_gate) { owner.CleanupFailed = true; _poisoned = true; } }
    private bool CheckChildHealth(Owner owner)
    {
        var child = Volatile.Read(ref owner.Child);
        if (child is not null)
            try { if (!child.CleanupHealthy) Poison(owner); } catch (Exception) { Poison(owner); }
        return !owner.CleanupFailed;
    }
    private static ProviderLaunchResult Rejected() => new(ProviderLaunchStatus.RejectedBeforeStart);
    private static ProviderLaunchResult NativeResult(ProviderLaunchResult? result)
    {
        if (result is null) return new(ProviderLaunchStatus.Uncertain);
        bool receipt = result.ProcessId is int pid && result.ProcessStartUtcTicks is long start
            && new LaunchProcessReceipt(pid, start, DateTimeOffset.UtcNow).IsValid;
        // The child's non-secret request tracker is kept only beside a valid receipt.
        long? tracker = receipt ? result.BrowserTrackerId : null;
        if (result.Status == ProviderLaunchStatus.ClientStartedUnverified && receipt)
            return new(ProviderLaunchStatus.ClientStartedUnverified, ProcessId: result.ProcessId, ProcessStartUtcTicks: result.ProcessStartUtcTicks,
                BrowserTrackerId: tracker);
        if (result.Status == ProviderLaunchStatus.RejectedBeforeStart && result.ProcessId is null && result.ProcessStartUtcTicks is null)
            return Rejected();
        return new(ProviderLaunchStatus.Uncertain, ProcessId: receipt ? result.ProcessId : null, ProcessStartUtcTicks: receipt ? result.ProcessStartUtcTicks : null,
            BrowserTrackerId: tracker);
    }
    private void Stop(Owner? owner)
    {
        if (owner is null) return;
        Interlocked.Exchange(ref owner.Fenced, 1);
        UpdateDiagnostic(owner, value => value with { Stopped = true });
        try { owner.Stop.Cancel(); } catch (Exception) { /* Actual cleanup still has to join. */ }
        Volatile.Read(ref owner.Handle)?.RequestClose();
    }
    private async Task<bool> CleanupOwnerAsync(Owner owner)
    {
        if (Interlocked.Exchange(ref owner.CleanupStarted, 1) != 0)
        { await owner.Completion.Task.ConfigureAwait(false); return !owner.CleanupFailed; }
        try
        {
            var prepared = Interlocked.Exchange(ref owner.Prepared, null);
            var child = Interlocked.Exchange(ref owner.Child, null);
            if (child is not null) CopyChildDiagnostic(owner, child);
            if (prepared is not null)
                try { await prepared.DisposeAsync().ConfigureAwait(false); } catch (Exception) { Poison(owner); }
            if (child is not null)
            {
                // Copy immutable facts before releasing the child. No reader is called under _gate.
                CopyChildDiagnostic(owner, child);
                try { if (!child.CleanupHealthy) Poison(owner); } catch (Exception) { Poison(owner); }
                try { await child.DisposeAsync().ConfigureAwait(false); } catch (Exception) { Poison(owner); }
                try { if (!child.CleanupHealthy) Poison(owner); } catch (Exception) { Poison(owner); }
            }
        }
        finally
        {
            lock (_gate)
            {
                CompleteDiagnostic(owner);
                if (owner.CleanupFailed) _poisoned = true;
                if (ReferenceEquals(_active, owner)) _active = null;
            }
            try { owner.DiagnosticCancellation.Dispose(); } catch (Exception) { }
            owner.Stop.Dispose(); owner.Completion.TrySetResult();
        }
        return !owner.CleanupFailed;
    }
    public ValueTask DisposeAsync()
    {
        Owner? owner; Task work;
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            _disposed = true; owner = _active;
            if (owner is not null) Interlocked.Exchange(ref owner.Fenced, 1);
            work = _dispose = Task.Run(() => DisposeOwnedAsync(owner), CancellationToken.None);
        }
        try { _lifetime.Cancel(); } catch (Exception) { }
        Stop(owner); return new(work);
    }
    private async Task DisposeOwnedAsync(Owner? owner)
    {
        try
        {
            try { _lifetime.Cancel(); } catch (Exception) { }
            Stop(owner);
            if (owner is not null)
            {
                try { if (owner.PreparationWork is { } work) await work.ConfigureAwait(false); } catch (Exception) { Poison(owner); }
                if (owner.Handle is { } handle) await handle.CloseOwnedAsync().ConfigureAwait(false);
                await owner.Completion.Task.ConfigureAwait(false);
            }
            try { await _trustOwner.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { lock (_gate) _poisoned = true; }
        }
        finally { _lifetime.Dispose(); }
        if (!CleanupHealthy) throw new InvalidOperationException("Native launch cleanup could not be confirmed.");
    }

    public NativeLaunchDiagnosticSnapshot? GetDiagnostic(Guid operationId)
    {
        Owner? owner; IConfiguredNativeLaunchChild? child;
        lock (_gate)
        {
            owner = _active is { } active && active.Context.OriginalIntent.OperationId == operationId ? active : null;
            child = owner is null ? null : Volatile.Read(ref owner.Child);
        }
        if (owner is not null && child is not null) CopyChildDiagnostic(owner, child);
        lock (_gate) return _diagnostics.TryGetValue(operationId, out var entry) ? entry.Snapshot with { } : null;
    }
    // All diagnostic entry ownership is scalar metadata. Never retain an Owner/child/intent/grant
    // in history, and never invoke an injected reader while holding this provider's authority gate.
    private void BeginDiagnostic(Owner owner)
    {
        try
        {
            Guid operationId = owner.Context.OriginalIntent.OperationId;
            if (operationId == Guid.Empty) return;
            _diagnostics.Remove(operationId); // A new attempt must not inherit a reused queue ID's facts.
            if (_diagnosticRevision == long.MaxValue || _diagnosticAttempt == long.MaxValue) return;
            if (_diagnostics.Count >= NativeLaunchDiagnosticLimits.MaximumSnapshots)
            {
                Guid oldest = Guid.Empty; long stamp = long.MaxValue;
                foreach (var pair in _diagnostics)
                    if (pair.Value.Completed && pair.Value.Stamp < stamp) { oldest = pair.Key; stamp = pair.Value.Stamp; }
                if (oldest == Guid.Empty) return; // Never evict owned/unfinished evidence.
                _diagnostics.Remove(oldest);
            }
            DateTimeOffset now = DateTimeOffset.UtcNow;
            owner.DiagnosticStamp = ++_diagnosticAttempt;
            _diagnostics[operationId] = new(owner.DiagnosticStamp, owner.Generation,
                new(operationId, ++_diagnosticRevision, now, now, NativeLaunchDiagnosticStage.VerifyingExecutable, NativeLaunchCleanupStatus.Pending));
            owner.DiagnosticCancellation = owner.Token.Register(() => UpdateDiagnostic(owner, value => value with { Stopped = true }));
        }
        catch (Exception) { /* Metadata failure must not change preparation or provider health. */ }
    }
    private bool DiagnosticEntryFor(Owner owner, out DiagnosticEntry entry)
    {
        if (owner.DiagnosticStamp != 0 && _diagnostics.TryGetValue(owner.Context.OriginalIntent.OperationId, out var found)
            && found.Stamp == owner.DiagnosticStamp && found.Generation == owner.Generation)
        { entry = found; return true; }
        entry = null!; return false;
    }
    private void UpdateDiagnostic(Owner owner, Func<NativeLaunchDiagnosticSnapshot, NativeLaunchDiagnosticSnapshot> update)
    {
        try
        {
            lock (_gate)
            {
                if (!DiagnosticEntryFor(owner, out var entry) || entry.Completed || _diagnosticRevision == long.MaxValue) return;
                var value = update(entry.Snapshot);
                bool stopped = entry.Snapshot.Stopped || value.Stopped || Volatile.Read(ref owner.Fenced) != 0 || owner.Token.IsCancellationRequested;
                entry.Snapshot = (value with { Revision = ++_diagnosticRevision, UpdatedUtc = DateTimeOffset.UtcNow,
                    Stopped = stopped, Stage = stopped ? NativeLaunchDiagnosticStage.Stopped : value.Stage }).Validated();
            }
        }
        catch (Exception) { /* No diagnostic result participates in cancellation, poisoning or reuse. */ }
    }
    private void CopyChildDiagnostic(Owner owner, IConfiguredNativeLaunchChild child)
    {
        try
        {
            var source = child.DiagnosticSource;
            if (source is null || ReferenceEquals(source, this)) return;
            var facts = source.GetDiagnostic(owner.Context.OriginalIntent.OperationId)?.Validated();
            if (facts is null || facts.OperationId != owner.Context.OriginalIntent.OperationId) return;
            lock (_gate)
            {
                if (!DiagnosticEntryFor(owner, out var entry) || entry.Completed || facts.Revision <= entry.ChildRevision
                    || _diagnosticRevision == long.MaxValue) return;
                var previous = entry.Snapshot;
                bool stopped = previous.Stopped || facts.Stopped || Volatile.Read(ref owner.Fenced) != 0 || owner.Token.IsCancellationRequested;
                var stage = (NativeLaunchDiagnosticStage)Math.Max((int)previous.Stage, (int)facts.Stage);
                entry.Snapshot = (previous with
                {
                    Revision = ++_diagnosticRevision,
                    // Reading copies original fact times. It never makes a cached check newly fresh.
                    UpdatedUtc = previous.Stopped && !facts.Stopped ? previous.UpdatedUtc : facts.UpdatedUtc,
                    Stage = stopped ? NativeLaunchDiagnosticStage.Stopped : stage, Stopped = stopped,
                    TrustStatus = facts.TrustStatus ?? previous.TrustStatus, TrustReason = facts.TrustReason ?? previous.TrustReason,
                    PreparationStatus = facts.PreparationStatus ?? previous.PreparationStatus,
                    BrowserOutcome = facts.BrowserOutcome ?? previous.BrowserOutcome, ShapeStatus = facts.ShapeStatus ?? previous.ShapeStatus,
                    DispatchStatus = facts.DispatchStatus ?? previous.DispatchStatus, LaunchMode = facts.LaunchMode ?? previous.LaunchMode,
                    ExecutableObservedUtc = facts.ExecutableObservedUtc ?? previous.ExecutableObservedUtc,
                    PreparationObservedUtc = facts.PreparationObservedUtc ?? previous.PreparationObservedUtc,
                    BrowserObservedUtc = facts.BrowserObservedUtc ?? previous.BrowserObservedUtc,
                    DispatchObservedUtc = facts.DispatchObservedUtc ?? previous.DispatchObservedUtc,
                    CleanupStatus = previous.CleanupStatus == NativeLaunchCleanupStatus.Failed || facts.CleanupStatus == NativeLaunchCleanupStatus.Failed
                        ? NativeLaunchCleanupStatus.Failed : NativeLaunchCleanupStatus.Pending
                }).Validated();
                entry.ChildRevision = facts.Revision;
            }
        }
        catch (Exception) { /* Broken or foreign diagnostic readers cannot poison real cleanup/launch decisions. */ }
    }
    // Called after actual awaited child/handle cleanup, under _gate. Completion is separate from
    // diagnostic validity so a metadata failure cannot pin unbounded history or a real provider slot.
    private void CompleteDiagnostic(Owner owner)
    {
        try
        {
            if (!DiagnosticEntryFor(owner, out var entry) || entry.Completed) return;
            try
            {
                UpdateDiagnostic(owner, value => value with { Stage = NativeLaunchDiagnosticStage.Finished,
                    CleanupStatus = owner.CleanupFailed || value.CleanupStatus == NativeLaunchCleanupStatus.Failed
                        ? NativeLaunchCleanupStatus.Failed : NativeLaunchCleanupStatus.Complete,
                    CleanupObservedUtc = DateTimeOffset.UtcNow });
            }
            finally { entry.Completed = true; }
        }
        catch (Exception) { /* Existing ownership completion remains authoritative. */ }
    }
    private static ClientInstallationCandidate CopySelection(ClientInstallationCandidate candidate)
    {
        const string invalid = "Select a present, unverified native Roblox player installation. Bootstrapper and unsafe paths are unsupported.";
        string? supplied = candidate.ExecutablePath;
        if (candidate.Product != ClientInstallationProduct.Roblox || candidate.FileState != ClientInstallationFileState.PresentUnverified
            || supplied is not { Length: > 3 and <= WindowsClientExecutableTrust.MaximumPathCharacters }
            || supplied.Any(char.IsControl) || supplied.Contains('"') || !Readable(supplied)
            || candidate.Evidence is null || candidate.Evidence.Count is < 1 or > 32
            || candidate.Evidence.Any(item => item is null || !Enum.IsDefined(item.Source) || item.Product != ClientInstallationProduct.Roblox)
            || candidate.Evidence.Distinct().Count() != candidate.Evidence.Count) throw new ArgumentException(invalid);
        string path = supplied.Replace('/', '\\');
        if (!char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.AsSpan(2).Contains(':')
            || path.IndexOfAny(['<', '>', '|', '*', '?']) >= 0) throw new ArgumentException(invalid);
        try
        {
            if (!Path.IsPathFullyQualified(path) || !Path.GetFullPath(path).Equals(path, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException(invalid);
            if (!RobloxNativePlayerFiles.IsDirectPlayer(path)) throw new ArgumentException(invalid);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { throw new ArgumentException(invalid); }
        if (!Metadata(candidate.ProductName) || !Metadata(candidate.FileVersion)) throw new ArgumentException(invalid);
        return candidate with { ExecutablePath = path, Evidence = Array.AsReadOnly(candidate.Evidence.ToArray()) };
    }
    private static bool Metadata(string? value) => value is null || value.Length <= 256 && !value.Any(char.IsControl) && Readable(value);
    private static bool Readable(string value)
    {
        for (int index = 0; index < value.Length; index++)
            if (char.IsSurrogate(value[index]) && (!char.IsHighSurrogate(value[index]) || ++index >= value.Length || !char.IsLowSurrogate(value[index]))) return false;
        return true;
    }
    public override string ToString() => "WindowsConfiguredNativeLaunchProvider { Capability = unverified, Selection = [local], Handoff = [redacted] }";

    private sealed class Owner(LaunchPreparationContext context, ClientInstallationCandidate selection, long generation, CancellationTokenSource stop)
    {
        public LaunchPreparationContext Context { get; } = context;
        public ClientInstallationCandidate Selection { get; } = selection;
        public long Generation { get; } = generation;
        public CancellationTokenSource Stop { get; } = stop;
        public CancellationToken Token { get; } = stop.Token;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? PreparationWork; public IConfiguredNativeLaunchChild? Child; public IPreparedAccountLaunch? Prepared;
        public WrappedLaunch? Handle; public int Fenced, CleanupStarted; public bool CleanupFailed;
        public long DiagnosticStamp;
        public CancellationTokenRegistration DiagnosticCancellation;
    }
    private sealed class DiagnosticEntry(long stamp, long generation, NativeLaunchDiagnosticSnapshot snapshot)
    {
        public long Stamp { get; } = stamp;
        public long Generation { get; } = generation;
        public NativeLaunchDiagnosticSnapshot Snapshot = snapshot;
        public long ChildRevision;
        public bool Completed;
    }
    private sealed class WrappedLaunch : IPreparedAccountLaunch
    {
        private readonly object _gate = new();
        private readonly WindowsConfiguredNativeLaunchProvider _provider;
        private readonly Owner _owner;
        private readonly IPreparedAccountLaunch _prepared;
        private Task<ProviderLaunchResult>? _dispatch;
        private Task<bool>? _close;
        private Task? _dispose;
        private bool _closed;
        public WrappedLaunch(WindowsConfiguredNativeLaunchProvider provider, Owner owner, IPreparedAccountLaunch prepared)
        { _provider = provider; _owner = owner; _prepared = prepared; DispatchIntent = prepared.DispatchIntent; }
        public LaunchPreparationContext Context => _owner.Context;
        public LaunchIntent DispatchIntent { get; }
        public bool IsExpired
        {
            get
            {
                if (Volatile.Read(ref _closed) || !_provider.IsCurrent(_owner) || !_provider.CheckChildHealth(_owner)) return true;
                try { return _prepared.IsExpired; } catch (Exception) { return true; }
            }
        }
        public Task<ProviderLaunchResult> DispatchAsync(LaunchDispatchBoundary boundary, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(boundary);
            lock (_gate)
            {
                if (_closed || _dispatch is not null) return Task.FromResult(Rejected());
                bool allowed = !IsExpired && boundary.Intent == DispatchIntent && !ct.IsCancellationRequested;
                _dispatch = Task.Run(() => DispatchOwnedAsync(boundary, allowed, ct), CancellationToken.None); return _dispatch;
            }
        }
        private async Task<ProviderLaunchResult> DispatchOwnedAsync(LaunchDispatchBoundary boundary, bool allowed, CancellationToken ct)
        {
            var result = Rejected(); bool invoked = false;
            try
            {
                _provider.UpdateDiagnostic(_owner, value => value with { Stage = NativeLaunchDiagnosticStage.Dispatching });
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(_owner.Token, ct);
                if (allowed && !IsExpired && !stop.IsCancellationRequested)
                { invoked = true; result = NativeResult(await _prepared.DispatchAsync(boundary, stop.Token).ConfigureAwait(false)); }
            }
            catch (Exception) { result = invoked ? new(ProviderLaunchStatus.Uncertain) : Rejected(); }
            finally
            {
                if (ct.IsCancellationRequested) _provider.UpdateDiagnostic(_owner, value => value with { Stopped = true });
                Volatile.Write(ref _closed, true);
                if (!await _provider.CleanupOwnerAsync(_owner).ConfigureAwait(false)) result = result with { Status = ProviderLaunchStatus.Uncertain };
            }
            return result;
        }
        public void RequestClose()
        {
            lock (_gate)
            {
                Volatile.Write(ref _closed, true);
                if (_close is null)
                {
                    var actual = _dispatch;
                    _close = actual is null ? Task.Run(() => _provider.CleanupOwnerAsync(_owner), CancellationToken.None) : JoinDispatchAsync(actual);
                }
            }
        }
        private async Task<bool> JoinDispatchAsync(Task actual)
        { try { await actual.ConfigureAwait(false); } catch (Exception) { _provider.Poison(_owner); } return !_owner.CleanupFailed; }
        public Task<bool> CloseOwnedAsync() { RequestClose(); lock (_gate) return _close!; }
        public ValueTask DisposeAsync()
        {
            RequestClose(); _provider.Stop(_owner);
            lock (_gate) return new(_dispose ??= RequireCleanupAsync(_close!));
        }
        private static async Task RequireCleanupAsync(Task<bool> cleanup)
        { if (!await cleanup.ConfigureAwait(false)) throw new InvalidOperationException("Native launch preparation cleanup could not be confirmed."); }
        public override string ToString() => "ConfiguredPreparedLaunch { Handoff = [redacted], Selection = [pinned], Identity = unverified }";
    }
    private sealed class RealChild(WindowsNativeAccountLaunchProvider child) : IConfiguredNativeLaunchChild
    {
        public bool CleanupHealthy => !child.CleanupBlocked;
        public INativeLaunchDiagnosticSource DiagnosticSource => child;
        public Task<LaunchPreparationResult> PrepareAsync(LaunchPreparationContext context, CancellationToken ct) => child.PrepareAsync(context, ct);
        public ValueTask DisposeAsync() => child.DisposeAsync();
    }
}

internal interface IConfiguredNativeLaunchChild : IAsyncDisposable
{
    bool CleanupHealthy { get; }
    INativeLaunchDiagnosticSource? DiagnosticSource => null;
    Task<LaunchPreparationResult> PrepareAsync(LaunchPreparationContext context, CancellationToken ct);
}
