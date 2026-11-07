using MacroSuite.Core.Input;
using Zenith.Sessions.Core;

namespace Zenith.MacroHost.Execution;

public enum CompanionMacroOperationStatus { Rejected, Busy, PreparationFailed, Finished, Stopped, CleanupFailed }

public sealed record CompanionMacroRecipientResult(TargetedInputTarget Target, TargetedMacroStartStatus StartStatus,
    TargetedMacroResult? Run);

public sealed record CompanionMacroOperationResult(CompanionMacroOperationStatus Status, Guid? OperationId,
    SavedMacroPreparationStatus? Preparation, IReadOnlyList<CompanionMacroRecipientResult> Recipients);

public sealed record CompanionMacroStartResult(CompanionMacroOperation? Operation, CompanionMacroOperationStatus? Rejection);

/// <summary>Host-local execution for registered diagnostic targets. Borrows the saved-document and input owners.</summary>
public sealed class CompanionMacroOperations
{
    private const int MaximumRecipients = 32;
    private readonly HostInputOperationArbiter _arbiter;
    private readonly SavedMacroPlanPreparer _preparer;
    private readonly TargetedMacroRunner _runner;
    private FailedCleanup? _failed;

    public CompanionMacroOperations(HostInputOperationArbiter arbiter, SavedMacroPlanPreparer preparer, TargetedMacroRunner runner)
    {
        _arbiter = arbiter ?? throw new ArgumentNullException(nameof(arbiter));
        _preparer = preparer ?? throw new ArgumentNullException(nameof(preparer));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    /// <summary>Run one saved revision on the captured recipients. This task includes actual cleanup after Stop.</summary>
    public async Task<CompanionMacroOperationResult> RunAsync(SavedMacroReference reference,
        IReadOnlyList<TargetedInputTarget> recipients, long expectedStopGeneration, CancellationToken ct = default)
    {
        var start = Start(reference, recipients, expectedStopGeneration, ct);
        return start.Operation is { } operation
            ? await operation.Completion.ConfigureAwait(false)
            : new(start.Rejection!.Value, null, null, []);
    }

    /// <summary>Reserve the host synchronously. Document preparation and recipient checks run on a worker.</summary>
    public CompanionMacroStartResult Start(SavedMacroReference reference,
        IReadOnlyList<TargetedInputTarget> recipients, long expectedStopGeneration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        long runnerGeneration = _runner.StopGeneration;
        if (!reference.IsValid || recipients.Count is < 1 or > MaximumRecipients)
            return new(null, CompanionMacroOperationStatus.Rejected);
        var targets = new TargetedInputTarget[recipients.Count];
        var windows = new HashSet<nint>();
        for (int i = 0; i < targets.Length; i++)
        {
            var target = recipients[i];
            if (target is not { IsValid: true } || !windows.Add(target.Window))
                return new(null, CompanionMacroOperationStatus.Rejected);
            targets[i] = target;
        }

        if (ct.IsCancellationRequested || _arbiter.StopGeneration != expectedStopGeneration)
            return new(null, CompanionMacroOperationStatus.Stopped);
        if (!_arbiter.TryAcquire(HostInputOperationKind.Companion, expectedStopGeneration, out var owner, out _))
            return new(null, _arbiter.StopGeneration == expectedStopGeneration
                ? CompanionMacroOperationStatus.Busy : CompanionMacroOperationStatus.Stopped);
        var lease = owner!;
        var operation = new CompanionMacroOperation(lease, targets);
        operation.Begin(() => ExecuteAsync(reference, targets, runnerGeneration, lease, operation, ct));
        return new(operation, null);
    }

    private async Task<CompanionMacroOperationResult> ExecuteAsync(SavedMacroReference reference,
        TargetedInputTarget[] targets, long runnerGeneration, HostInputOperationLease lease,
        CompanionMacroOperation operation, CancellationToken ct)
    {
        var attempts = new List<Attempt>(targets.Length);
        SavedMacroPreparationStatus? preparation = null;
        bool clean = false, stopped = false;
        CancellationTokenRegistration stopRegistration = default;
        try
        {
            stopRegistration = ct.UnsafeRegister(static state => ((HostInputOperationLease)state!).SignalStop(), lease);
            if (!_runner.TryBindOperation(lease, runnerGeneration))
            {
                clean = true;
                var unavailable = !lease.CanContinue || runnerGeneration != _runner.StopGeneration
                    ? CompanionMacroOperationStatus.Stopped : CompanionMacroOperationStatus.Busy;
                return new(unavailable, lease.OperationId, null, []);
            }
            try
            {
                var prepared = await _preparer.PrepareAsync(reference, TargetedMacroScope.OwnedWindowDiagnostic, lease.StopToken)
                    .ConfigureAwait(false);
                preparation = prepared.Status;
                operation.PublishPreparation(prepared.Status);
                if (prepared.Prepared is { } ready)
                {
                    for (int i = 0; i < targets.Length; i++)
                    {
                        var start = _runner.TryPrepareBound(ready.Plan, targets[i], lease, out var unstarted);
                        attempts.Add(new(targets[i], start, unstarted));
                        operation.PublishStart(i, start);
                        start.Run?.Begin();
                    }
                }
            }
            catch
            {
                // A later rejected start cannot abandon earlier interpreters.
                lease.SignalStop();
                throw;
            }
            finally
            {
                await Task.WhenAll(attempts.Select(static attempt => attempt.Completion)).ConfigureAwait(false);
                clean = attempts.All(static attempt => attempt.Clean);
                stopped = !lease.CanContinue;
                if (!clean && attempts.All(static attempt => attempt.Start.Run?.LifecycleCleanupFailed != true))
                    Volatile.Write(ref _failed, new(lease, attempts.Where(static attempt => !attempt.Clean).ToArray()));
            }
        }
        finally
        {
            operation.PublishFinalizing();
            try { await stopRegistration.DisposeAsync().ConfigureAwait(false); }
            finally { await lease.CompleteAsync(clean).ConfigureAwait(false); }
        }

        var outcomes = attempts.Select(static attempt => new CompanionMacroRecipientResult(attempt.Target,
            attempt.Start.Status, attempt.Start.Run?.Completion.Result)).ToArray();
        stopped |= lease.StopToken.IsCancellationRequested;
        var status = !clean ? CompanionMacroOperationStatus.CleanupFailed
            : stopped ? CompanionMacroOperationStatus.Stopped
            : preparation != SavedMacroPreparationStatus.Ready ? CompanionMacroOperationStatus.PreparationFailed
            : attempts.All(static attempt => attempt.Start.Run is null) ? CompanionMacroOperationStatus.Rejected
            : CompanionMacroOperationStatus.Finished;
        return new(status, lease.OperationId, preparation, Array.AsReadOnly(outcomes));
    }

    /// <summary>Retry only the run-owned releases from an explicitly selected failed companion operation.</summary>
    public async Task<bool> RecoverAsync(Guid operationId, CancellationToken ct = default)
    {
        var failed = Volatile.Read(ref _failed);
        if (failed is null || failed.Lease.OperationId != operationId) return false;
        bool recovered = await _arbiter.TryRecoverAsync(failed.Lease, async token =>
        {
            token.ThrowIfCancellationRequested();
            var outcomes = await Task.WhenAll(failed.Attempts.Select(static attempt => attempt.RetryAsync())).ConfigureAwait(false);
            if (!outcomes.All(static released => released)) throw new HostInputOperationCleanupException();
        }, ct).ConfigureAwait(false);
        if (recovered) Interlocked.CompareExchange(ref _failed, null, failed);
        return recovered;
    }

    private sealed record FailedCleanup(HostInputOperationLease Lease, Attempt[] Attempts);

    private sealed record Attempt(TargetedInputTarget Target, TargetedMacroStartResult Start, UnstartedTargetedMacro? Unstarted)
    {
        public Task Completion => (Task?)Start.Run?.Completion ?? (Task?)Unstarted?.Completion ?? Task.CompletedTask;
        public bool Clean => Start.Run?.Completion.Result.Status != TargetedMacroResultStatus.CleanupFailed
            && (Unstarted is null || Unstarted.Completion.Result.State == TargetedInputRunState.Stopped);

        public async Task<bool> RetryAsync()
        {
            if (Start.Run is { } run)
                return (await run.RetryCleanupAsync().ConfigureAwait(false)).Input.State == TargetedInputRunState.Stopped;
            return Unstarted is { } pending
                && (await pending.Input.RetryCleanupAsync().ConfigureAwait(false)).State == TargetedInputRunState.Stopped;
        }
    }
}
