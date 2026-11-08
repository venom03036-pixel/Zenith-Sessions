using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// Unverified composition only. The application must retain its production capability gate until
/// real browser, native account and destination behavior is established. All injected services
/// remain caller-owned; this provider owns only its coordinator, cancellation and prepared handles.
/// </summary>
public sealed class WindowsNativeAccountLaunchProvider : IPreparingLaunchProvider, INativeLaunchDiagnosticSource, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ClientInstallationCandidate _installation;
    private readonly WindowsClientExecutableTrust _executableTrust;
    private readonly IRobloxNativeHandoffPreparer _preparer;
    private readonly IRobloxNativeHandoffCapture _capture;
    private readonly IExtraClientArgumentsSource? _extraArguments;
    private readonly NativeHandoffCoordinator _coordinator;
    private readonly CancellationTokenSource _lifetime = new();
    private Owner? _active;
    private Task? _dispose;
    private bool _disposed, _cleanupBlocked;
    private NativeLaunchDiagnosticSnapshot? _diagnostic;
    private Owner? _diagnosticOwner;
    private long _diagnosticRevision;
    private NativeLaunchExecutableIdentity? _executableIdentity;
    internal NativeLaunchExecutableIdentity? ExecutableIdentity => Volatile.Read(ref _executableIdentity);

    /// <param name="extraArguments">Where the account's saved extra client arguments (R-launch-16) are read at preparation time;
    /// null passes none. They are read before the executable is held and appended after the official request by the direct start.</param>
    public WindowsNativeAccountLaunchProvider(ClientInstallationCandidate installation, WindowsClientExecutableTrust executableTrust,
        IRobloxNativeHandoffPreparer handoffPreparer, IRobloxNativeHandoffCapture capture, IAuthorizationDispatchVault dispatchVault,
        IExtraClientArgumentsSource? extraArguments = null)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Evidence is null || installation.Evidence.Count > 32) throw new ArgumentException("The installation evidence is invalid.");
        _installation = installation with { Evidence = Array.AsReadOnly(installation.Evidence.ToArray()) };
        _executableTrust = executableTrust ?? throw new ArgumentNullException(nameof(executableTrust));
        _preparer = handoffPreparer ?? throw new ArgumentNullException(nameof(handoffPreparer));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _extraArguments = extraArguments;
        _coordinator = new(dispatchVault ?? throw new ArgumentNullException(nameof(dispatchVault)));
    }

    public ProviderCapability AccountLaunch { get; } = new("Native account launch", CapabilityState.Unverified,
        "Owned preparation and guarded dispatch composition exists. Current browser grammar, native account selection, compatibility and arrival have not been verified; production launching remains disabled.");
    internal bool CleanupBlocked { get { lock (_gate) return _cleanupBlocked; } }

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
            if (_cleanupBlocked) return Task.FromResult(new LaunchPreparationResult(LaunchPreparationFailure.CleanupFailed));
            if (_active is not null || !ValidOriginal(context.OriginalIntent))
                return Task.FromResult(new LaunchPreparationResult(LaunchPreparationFailure.Rejected));
            var owner = new Owner(context, CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token));
            _active = owner;
            Volatile.Write(ref _executableIdentity, null);
            BeginDiagnostic(owner);
            var work = Task.Run(() => PrepareOwnedAsync(owner), CancellationToken.None);
            owner.PreparationWork = work; return work;
        }
    }

    private async Task<LaunchPreparationResult> PrepareOwnedAsync(Owner owner)
    {
        WindowsNativeHandoffPreparation? executable = null; NativeHandoffPreparation? native = null;
        bool transferred = false, cancelled = false, handoffEntered = false, handoffReturned = false;
        var failure = LaunchPreparationFailure.Rejected;
        try
        {
            // Saved extra arguments are read first, from saved state only; a read failure or an invalid saved value is a
            // Rejected preparation (no browser, account or file work follows), and no argument text is retained anywhere.
            // They are for Zenith's own direct player start only: a bootstrapper installation (R-launch-17) never reads them.
            IReadOnlyList<string>? extra = null;
            if (_extraArguments is not null && !BootstrapperProducts.IsBootstrapper(_installation.Product))
            {
                extra = await _extraArguments.ResolveAsync(owner.Context.OriginalIntent.ProfileId, owner.Token).ConfigureAwait(false);
                owner.Token.ThrowIfCancellationRequested();
            }
            executable = await _executableTrust.PrepareAsync(_installation, extra, owner.Token).ConfigureAwait(false);
            var trust = executable.Trust;
            Volatile.Write(ref _executableIdentity, WindowsClientExecutableTrust.LaunchIdentity(trust));
            UpdateDiagnostic(owner, value => value with { TrustStatus = trust.Status,
                TrustReason = trust.Reason, ExecutableObservedUtc = trust.ObservedUtc });
            owner.Token.ThrowIfCancellationRequested();
            if (executable.Trust.Status is ClientExecutableTrustStatus.VerifiedRobloxPublisher or ClientExecutableTrustStatus.VerifiedAcceptedPublisher && executable.Prepared is not null)
            {
                UpdateDiagnostic(owner, value => value with { Stage = NativeLaunchDiagnosticStage.PreparingHandoff });
                handoffEntered = true;
                var result = await _preparer.PrepareNativeHandoffAsync(owner.Context.OriginalIntent, _capture, owner.Token).ConfigureAwait(false);
                handoffReturned = true;
                UpdateDiagnostic(owner, value => value with { PreparationStatus = result?.Status,
                    PreparationObservedUtc = DateTimeOffset.UtcNow, BrowserOutcome = result?.BrowserFacts?.Outcome,
                    ShapeStatus = result?.BrowserFacts?.ShapeStatus, BrowserObservedUtc = result?.BrowserFacts?.ObservedUtc,
                    LaunchMode = result?.BrowserFacts?.Mode });
                native = result?.Preparation; Volatile.Write(ref owner.Native, native);
                failure = result?.Status switch
                {
                    NativeHandoffPreparationStatus.Expired => LaunchPreparationFailure.Expired,
                    NativeHandoffPreparationStatus.CleanupFailed => LaunchPreparationFailure.CleanupFailed,
                    _ => LaunchPreparationFailure.Rejected
                };
                owner.Token.ThrowIfCancellationRequested();
                if (result?.Status == NativeHandoffPreparationStatus.Ready)
                {
                    if (native?.Candidate is not { } candidate)
                    {
                        failure = LaunchPreparationFailure.Rejected;
                        UpdateDiagnostic(owner, value => value with { PreparationStatus = NativeHandoffPreparationStatus.CaptureRejected,
                            PreparationObservedUtc = DateTimeOffset.UtcNow });
                    }
                    else if (candidate.IsExpired || candidate.IsConsumed || candidate.IsClosed)
                    {
                        failure = LaunchPreparationFailure.Expired;
                        UpdateDiagnostic(owner, value => value with { PreparationStatus = NativeHandoffPreparationStatus.Expired,
                            PreparationObservedUtc = DateTimeOffset.UtcNow });
                    }
                    else if (MatchesOriginal(owner.Context.OriginalIntent, native!))
                    {
                        var handle = new PreparedLaunch(this, owner, executable, native!);
                        lock (_gate)
                        {
                            if (_disposed) throw new OperationCanceledException("Native launch preparation was stopped.");
                            owner.Token.ThrowIfCancellationRequested();
                            owner.Handle = handle; transferred = true; executable = null; native = null;
                            UpdateDiagnostic(owner, value => value with { Stage = NativeLaunchDiagnosticStage.Prepared });
                        }
                        return new(LaunchPreparationFailure.None, handle);
                    }
                    else UpdateDiagnostic(owner, value => value with { PreparationStatus = NativeHandoffPreparationStatus.RequestChanged,
                        PreparationObservedUtc = DateTimeOffset.UtcNow });
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            if (handoffEntered && !handoffReturned) UpdateDiagnostic(owner, value => value with
                { PreparationStatus = NativeHandoffPreparationStatus.Cancelled, PreparationObservedUtc = DateTimeOffset.UtcNow });
        }
        catch (Exception)
        {
            if (handoffEntered && !handoffReturned) UpdateDiagnostic(owner, value => value with
                { PreparationStatus = NativeHandoffPreparationStatus.Failed, PreparationObservedUtc = DateTimeOffset.UtcNow });
            // Browser, native and storage error text is never retained.
        }
        finally
        {
            if (!transferred)
            {
                bool cleaned = Cleanup(native, executable);
                if (!cleaned) failure = LaunchPreparationFailure.CleanupFailed;
                Finish(owner, cleaned && failure != LaunchPreparationFailure.CleanupFailed);
            }
        }
        if (cancelled && failure != LaunchPreparationFailure.CleanupFailed) throw new OperationCanceledException(owner.Token);
        return new(failure);
    }

    private static bool ValidOriginal(LaunchIntent? intent) => intent is not null && intent.OperationId != Guid.Empty
        && intent.ProfileId != Guid.Empty && intent.ProfileRevision is >= 0 and < long.MaxValue
        && intent.UserId > 0 && intent.AuthorizationGeneration is > 0 and <= long.MaxValue - 3
        && intent.Priority is >= 0 and <= 100 && intent.RequestedUtc != default && LaunchCoordinator.HasValidDestination(intent);

    private static bool MatchesOriginal(LaunchIntent original, NativeHandoffPreparation native)
    {
        try
        {
            var final = native.Intent; var context = native.Candidate.Context; var grant = native.FinalGrant; var record = grant.Record;
            return final.ProfileRevision >= original.ProfileRevision && final.ProfileRevision < long.MaxValue
                && final.ProfileRevision - original.ProfileRevision <= 1 && final.AuthorizationGeneration == original.AuthorizationGeneration + 2
                && (final with { ProfileRevision = original.ProfileRevision, AuthorizationGeneration = original.AuthorizationGeneration }) == original
                && context.LaunchOperationId == original.OperationId && context.ProfileId == original.ProfileId
                && context.ExpectedUserId == original.UserId && context.PendingAuthorizationGeneration == original.AuthorizationGeneration + 1
                && context.Destination.PlaceId == original.PlaceId && context.Destination.JobId == original.RequestedJobId
                && context.Destination.ServerKind == original.RequestedServerKind
                && context.Destination.PrivateServer == original.PrivateServer
                && grant.ProfileId == original.ProfileId && grant.HighestMutationId == context.OperationId
                && grant.HighestAllocated == final.AuthorizationGeneration && grant.LastCommitted == final.AuthorizationGeneration
                && record is { Provider: RobloxSessionIdentity.Provider, Health: AuthorizationHealth.Linked }
                && record.ProfileId == original.ProfileId && record.VerifiedUserId == original.UserId
                && record.Generation == final.AuthorizationGeneration && record.ExpiresUtc > DateTimeOffset.UtcNow
                && record.Validated() == record && grant.RecordSha256 is { Length: 64 }
                && grant.RecordSha256.AsSpan().IndexOfAnyExcept("0123456789ABCDEF") < 0;
        }
        catch (Exception) { return false; }
    }

    private static ProviderLaunchResult Rejected() => new(ProviderLaunchStatus.RejectedBeforeStart);
    private static ProviderLaunchResult Describe(NativeHandoffAttempt attempt)
    {
        var receipt = attempt.ProcessReceipt is { IsValid: true } valid ? valid : null;
        // The request's non-secret tracker travels only beside a valid receipt, never on its own.
        long? tracker = receipt is null ? null : attempt.BrowserTrackerId;
        if (attempt.Status == NativeHandoffAttemptStatus.StartedUnverified && receipt is not null)
            return new(ProviderLaunchStatus.ClientStartedUnverified, ProcessId: receipt.ProcessId, ProcessStartUtcTicks: receipt.ProcessStartUtcTicks,
                BrowserTrackerId: tracker);
        if (!Enum.IsDefined(attempt.Status) || attempt.Status is NativeHandoffAttemptStatus.Uncertain or NativeHandoffAttemptStatus.StartedUnverified
            || attempt.ProcessReceipt is not null)
            return new(ProviderLaunchStatus.Uncertain, ProcessId: receipt?.ProcessId, ProcessStartUtcTicks: receipt?.ProcessStartUtcTicks,
                BrowserTrackerId: tracker);
        return Rejected();
    }
    private static bool Cleanup(NativeHandoffPreparation? native, WindowsNativeHandoffPreparation? executable)
    {
        bool complete = true;
        try { native?.Dispose(); } catch (Exception) { complete = false; }
        try { executable?.Dispose(); } catch (Exception) { complete = false; }
        return complete;
    }
    private void Cancel(Owner owner)
    {
        Volatile.Read(ref owner.Native)?.Candidate?.Context.Dispose();
        try { owner.Stop.Cancel(); } catch (Exception) { /* Cancellation cannot abandon its actual owner. */ }
    }
    private void Finish(Owner owner, bool cleanupComplete = true)
    {
        Volatile.Write(ref owner.Native, null);
        lock (_gate)
        {
            // An unconfirmed cleanup is never a reusable provider slot, even if the queue
            // separately retains an uncertain operation. Recreate only after explicit recovery.
            if (!cleanupComplete) _cleanupBlocked = true;
            if (owner.Finished) return;
            UpdateDiagnostic(owner, value => value with { Stage = NativeLaunchDiagnosticStage.Finished,
                CleanupStatus = cleanupComplete ? NativeLaunchCleanupStatus.Complete : NativeLaunchCleanupStatus.Failed,
                CleanupObservedUtc = DateTimeOffset.UtcNow });
            owner.Finished = true; if (ReferenceEquals(_active, owner)) _active = null;
            if (ReferenceEquals(_diagnosticOwner, owner)) _diagnosticOwner = null;
        }
        try { owner.DiagnosticCancellation.Dispose(); } catch (Exception) { } // Join outside _gate before CTS disposal.
        owner.Stop.Dispose(); owner.Completion.TrySetResult();
    }

    public NativeLaunchDiagnosticSnapshot? GetDiagnostic(Guid operationId)
    {
        lock (_gate) return _diagnostic is { } value && value.OperationId == operationId ? value with { } : null;
    }
    private void BeginDiagnostic(Owner owner)
    {
        try
        {
            if (_diagnosticRevision == long.MaxValue) { _diagnostic = null; _diagnosticOwner = null; return; }
            DateTimeOffset now = DateTimeOffset.UtcNow;
            _diagnosticOwner = owner;
            _diagnostic = new(owner.Context.OriginalIntent.OperationId, checked(++_diagnosticRevision), now, now,
                NativeLaunchDiagnosticStage.VerifyingExecutable, NativeLaunchCleanupStatus.Pending);
            owner.DiagnosticCancellation = owner.Token.Register(() => UpdateDiagnostic(owner, value => value with { Stopped = true }));
        }
        catch (Exception) { /* Observability cannot change preparation, trust or reuse decisions. */ }
    }
    private void UpdateDiagnostic(Owner owner, Func<NativeLaunchDiagnosticSnapshot, NativeLaunchDiagnosticSnapshot> update)
    {
        try
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_diagnosticOwner, owner) || _diagnostic is not { } previous || _diagnosticRevision == long.MaxValue) return;
                var value = update(previous);
                bool stopped = previous.Stopped || value.Stopped || owner.Token.IsCancellationRequested;
                _diagnostic = (value with { Revision = ++_diagnosticRevision, UpdatedUtc = DateTimeOffset.UtcNow,
                    Stopped = stopped, Stage = stopped ? NativeLaunchDiagnosticStage.Stopped : value.Stage }).Validated();
            }
        }
        catch (Exception) { /* Fixed diagnostics are best-effort and never influence authority/cleanup. */ }
    }
    public ValueTask DisposeAsync()
    {
        Owner? owner; Task work;
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            _disposed = true; owner = _active;
            if (owner is not null) Volatile.Read(ref owner.Native)?.Candidate?.Context.Dispose();
            work = _dispose = Task.Run(() => DisposeOwnedAsync(owner), CancellationToken.None);
        }
        try { _lifetime.Cancel(); } catch (Exception) { /* Join still occurs if a cancellation callback fails. */ }
        return new(work);
    }
    private async Task DisposeOwnedAsync(Owner? owner)
    {
        try
        {
            if (owner is not null)
            {
                Cancel(owner);
                try { if (owner.PreparationWork is { } work) await work.ConfigureAwait(false); }
                catch (Exception) { /* Preparation's fixed result/cleanup remains owned. */ }
                if (owner.Handle is { } handle) await handle.DisposeAsync().ConfigureAwait(false);
                await owner.Completion.Task.ConfigureAwait(false);
            }
        }
        finally { try { await _coordinator.DisposeAsync().ConfigureAwait(false); } finally { _lifetime.Dispose(); } }
    }
    public override string ToString() => "WindowsNativeAccountLaunchProvider { Capability = unverified, Preparations = [owned] }";

    private sealed class Owner(LaunchPreparationContext context, CancellationTokenSource stop)
    {
        public LaunchPreparationContext Context { get; } = context;
        public CancellationTokenSource Stop { get; } = stop;
        public CancellationToken Token { get; } = stop.Token;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? PreparationWork; public PreparedLaunch? Handle; public NativeHandoffPreparation? Native; public bool Finished;
        public CancellationTokenRegistration DiagnosticCancellation;
    }

    private sealed class PreparedLaunch(WindowsNativeAccountLaunchProvider provider, Owner owner,
        WindowsNativeHandoffPreparation executable, NativeHandoffPreparation native) : IPreparedAccountLaunch
    {
        private readonly object _gate = new();
        private WindowsNativeHandoffPreparation? _executable = executable;
        private NativeHandoffPreparation? _native = native;
        private Task<ProviderLaunchResult>? _dispatch;
        private Task? _dispose;
        private bool _closed;
        public LaunchPreparationContext Context => owner.Context;
        public LaunchIntent DispatchIntent { get; } = native.Intent;
        public bool IsExpired
        {
            get
            {
                var current = Volatile.Read(ref _native);
                return Volatile.Read(ref _closed) || owner.Token.IsCancellationRequested || current is null
                    || current.Candidate.IsConsumed || current.Candidate.IsExpired || current.Candidate.IsClosed;
            }
        }
        public Task<ProviderLaunchResult> DispatchAsync(LaunchDispatchBoundary boundary, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(boundary);
            lock (_gate)
            {
                if (_closed || _dispatch is not null) return Task.FromResult(Rejected());
                bool allowed = boundary.Intent == DispatchIntent && !IsExpired && !ct.IsCancellationRequested;
                _dispatch = Task.Run(() => DispatchOwnedAsync(boundary, allowed, ct), CancellationToken.None);
                return _dispatch;
            }
        }
        private async Task<ProviderLaunchResult> DispatchOwnedAsync(LaunchDispatchBoundary boundary, bool allowed, CancellationToken ct)
        {
            var result = Rejected(); bool entered = false;
            try
            {
                provider.UpdateDiagnostic(owner, value => value with { Stage = NativeLaunchDiagnosticStage.Dispatching });
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(owner.Token, ct);
                var current = Volatile.Read(ref _native); var held = Volatile.Read(ref _executable);
                if (allowed && !IsExpired && current is not null && held?.Prepared is { } start)
                {
                    stop.Token.ThrowIfCancellationRequested(); entered = true;
                    var attempt = await provider._coordinator.DispatchAsync(current.Candidate, current.FinalGrant, DispatchIntent, start,
                        boundary.ValidateAsync, () => boundary.IsCurrent, boundary.ReadAdmission, stop.Token).ConfigureAwait(false);
                    provider.UpdateDiagnostic(owner, value => value with { DispatchStatus = attempt.Status, DispatchObservedUtc = DateTimeOffset.UtcNow });
                    result = Describe(attempt);
                }
                else provider.UpdateDiagnostic(owner, value => value with { DispatchStatus = stop.IsCancellationRequested
                    ? NativeHandoffAttemptStatus.Cancelled : NativeHandoffAttemptStatus.NotStarted,
                    DispatchObservedUtc = DateTimeOffset.UtcNow, Stopped = stop.IsCancellationRequested });
            }
            catch (OperationCanceledException)
            {
                result = entered ? new(ProviderLaunchStatus.Uncertain) : Rejected();
                provider.UpdateDiagnostic(owner, value => value with { DispatchStatus = entered ? NativeHandoffAttemptStatus.Uncertain
                    : NativeHandoffAttemptStatus.Cancelled, DispatchObservedUtc = DateTimeOffset.UtcNow, Stopped = true });
            }
            catch (Exception)
            {
                result = entered ? new(ProviderLaunchStatus.Uncertain) : Rejected();
                provider.UpdateDiagnostic(owner, value => value with { DispatchStatus = entered ? NativeHandoffAttemptStatus.Uncertain
                    : NativeHandoffAttemptStatus.NotStarted, DispatchObservedUtc = DateTimeOffset.UtcNow });
            }
            finally
            {
                if (ct.IsCancellationRequested) provider.UpdateDiagnostic(owner, value => value with { Stopped = true });
                if (!Release()) result = result with { Status = ProviderLaunchStatus.Uncertain };
            }
            return result;
        }
        private bool Release()
        {
            Volatile.Write(ref _closed, true);
            bool complete = Cleanup(Interlocked.Exchange(ref _native, null), Interlocked.Exchange(ref _executable, null));
            provider.Finish(owner, complete); return complete;
        }
        public ValueTask DisposeAsync()
        {
            Task work;
            lock (_gate)
            {
                if (_dispose is not null) return new(_dispose);
                Volatile.Write(ref _closed, true);
                Volatile.Read(ref _native)?.Candidate.Context.Dispose();
                var actual = _dispatch;
                work = _dispose = Task.Run(async () =>
                {
                    try { if (actual is not null) await actual.ConfigureAwait(false); }
                    finally { if (!Release()) throw new InvalidOperationException("Native launch preparation cleanup could not be confirmed."); }
                }, CancellationToken.None);
            }
            provider.Cancel(owner); return new(work);
        }
        public override string ToString() => "PreparedAccountLaunch { Handoff = [redacted], Grant = [protected], Identity = unverified }";
    }
}
