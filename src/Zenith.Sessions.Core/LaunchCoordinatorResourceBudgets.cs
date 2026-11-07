namespace Zenith.Sessions.Core;

public sealed partial class LaunchCoordinator
{
    private static readonly LaunchResourceBudget DefaultResourceBudget = LaunchResourceBudget.FromSettings(new());
    private readonly Func<LaunchIntent, LaunchResourceBudget>? _budgetResolver;
    private readonly Func<LaunchOperation, IReadOnlyList<LaunchOperation>, LaunchAdmission>? _candidateAdmission;

    // The resolver is a fast local policy snapshot, never browser/network/credential work.
    // Provider messages may contain secrets. Fail without forwarding its text or exception.
    private LaunchResourceBudget ResolveResourceBudget(LaunchIntent intent, CancellationToken ct)
    {
        try
        {
            var budget = _budgetResolver is null ? DefaultResourceBudget : _budgetResolver(intent);
            ct.ThrowIfCancellationRequested();
            return budget?.Validated() ?? throw new InvalidOperationException();
        }
        catch (Exception)
        {
            ct.ThrowIfCancellationRequested();
            throw new LaunchQueueException("A valid launch resource forecast is unavailable. No request was committed or started; review the resource settings before retrying.");
        }
    }

    // Caller owns _serial. This is only for an explicitly eligible legacy pre-start row.
    // Unknown forecasts on possible/already-started clients are never reconstructed as proof.
    private async Task<LaunchOperation?> PinLegacyResourceBudgetAsync(LaunchOperation chosen, long generation, CancellationToken ct)
    {
        if (chosen.ResourceBudget is not null) return chosen;
        if (chosen.ProcessReceipt is not null || chosen.State is not (LaunchState.Queued or LaunchState.Blocked))
            throw new LaunchQueueException("This possible client has no pinned resource forecast. Review it before creating a fresh request.");
        var budget = ResolveResourceBudget(chosen.Intent, ct);
        if (_paused || generation != Interlocked.Read(ref _generation) || _stop.IsCancellationRequested) return null;
        ct.ThrowIfCancellationRequested();
        int index = _operations.FindIndex(row => row.Intent.OperationId == chosen.Intent.OperationId);
        if (index < 0 || _operations[index] != chosen) throw new LaunchQueueException("The selected launch request changed before its resource forecast could be pinned.");
        var pinned = chosen with { ResourceBudget = budget };
        _operations[index] = pinned; await PersistAsync(ct).ConfigureAwait(false); return pinned;
    }

    private LaunchAdmission AdmissionForStart(IReadOnlyList<LaunchOperation> operations, Guid startingOperation)
    {
        // Read the actual immutable row in this snapshot, not the earlier Queued/Preparing
        // local whose state or prepared authorization intent may have changed since selection.
        var candidate = operations.FirstOrDefault(row => row.Intent.OperationId == startingOperation);
        if (candidate is null || candidate.ResourceBudget is not { IsValid: true } || candidate.ProcessReceipt is not null
            || candidate.State is not (LaunchState.Queued or LaunchState.Blocked or LaunchState.Preparing or LaunchState.Validating or LaunchState.Launching))
            return new(false, "A current un-emitted launch request with a pinned resource forecast is required.");
        // Admission asks whether this candidate fits once. Retain every other forecast; exclude
        // only the exact current candidate whose native request has not yet been emitted.
        var remaining = operations.Where(row => row.Intent.OperationId != startingOperation).ToArray();
        if (HasUnreconciledStart(remaining)) return new(false, "A previous possible client needs a pinned forecast and fresh exact process observation or explicit closure review.");
        if (_candidateAdmission is null) return _admit(remaining);
        try { return _candidateAdmission(candidate, remaining) ?? new(false, "Fresh candidate resource admission is unavailable."); }
        catch (Exception) { return new(false, "Fresh candidate resource admission is unavailable."); }
    }
}
