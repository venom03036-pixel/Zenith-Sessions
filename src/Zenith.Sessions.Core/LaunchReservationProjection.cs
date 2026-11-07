using System.Collections.Frozen;

namespace Zenith.Sessions.Core;

/// <summary>Admission arithmetic only. Observed operation IDs never establish account identity or arrival.</summary>
public sealed record LaunchReservationProjectionResult(IReadOnlyList<ResourceReservation> Reservations, int OccupiedForAdmission,
    bool FreshProcessInventory, int CreditedReservations, int FullBudgetReservations, string Explanation,
    IReadOnlySet<Guid> SafelyObservedOperationIds, bool ForecastsKnown = true);

/// <summary>
/// Retains each queue forecast and subtracts only measured private consumption already reflected
/// in a fresh system sample. It never automatically releases a reservation or infers process exit;
/// the explicit manual-closure marker releases its forecast while OS observations remain counted.
/// </summary>
public static class LaunchReservationProjection
{
    public const int MaximumOperations = 1101, MaximumObservations = 4096;
    public static readonly TimeSpan MaximumEvidenceAge = TimeSpan.FromSeconds(3);

    public static LaunchReservationProjectionResult Evaluate(LaunchQueueSnapshot queue, DesktopSnapshot desktop, CapacitySettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(queue); ArgumentNullException.ThrowIfNull(desktop); ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(desktop.Resources);
        settings.Validated();
        if (queue.Operations is null || desktop.Sessions is null) throw new ArgumentException("Queue and process snapshots are required.");
        int operationCount = queue.Operations.Count, observationCount = desktop.Sessions.Count;
        if (now == default || operationCount is < 0 or > MaximumOperations || observationCount is < 0 or > MaximumObservations)
            throw new ArgumentException("Reservation projection requires bounded queue and process snapshots and a current time.");
        // Index bounded inputs; never trust an unrelated/unbounded IReadOnlyList enumerator.
        var operations = new LaunchOperation[operationCount]; var operationIds = new HashSet<Guid>();
        var receiptPids = new Dictionary<int, int>();
        for (int index = 0; index < operations.Length; index++)
        {
            var operation = queue.Operations[index];
            if (operation?.Intent is null || operation.Intent.OperationId == Guid.Empty || !operationIds.Add(operation.Intent.OperationId)
                || !Enum.IsDefined(operation.State) || !Enum.IsDefined(operation.Problem))
                throw new ArgumentException("Queue reservations require distinct valid operation identities and known states.");
            operation.ResourceBudget?.Validated();
            operations[index] = operation;
            if (!ClosureAcknowledged(operation) && operation.ProcessReceipt is { IsValid: true } receipt)
                receiptPids[receipt.ProcessId] = receiptPids.GetValueOrDefault(receipt.ProcessId) + 1;
        }
        var observations = new Dictionary<int, SessionObservation>();
        bool freshInventory = desktop.Problem is null && desktop.ProcessInventoryComplete && desktop.ProcessRevision > 0
            && desktop.ProcessObservedUtc is { } inventoryTime && Fresh(inventoryTime, now);
        for (int index = 0; index < observationCount; index++)
        {
            var row = desktop.Sessions[index];
            if (row is null || row.ProcessId <= 0 || row.ProcessStartUtcTicks <= 0
                || row.ProcessStartUtcTicks > DateTimeOffset.MaxValue.UtcTicks || row.ObservedUtc == default
                || row.ProcessStartUtcTicks > row.ObservedUtc.UtcTicks || row.Problem is not null
                || desktop.ProcessObservedUtc is not { } completed || row.ObservedUtc > completed || !Fresh(row.ObservedUtc, now))
            { freshInventory = false; continue; }
            if (!observations.TryAdd(row.ProcessId, row)) freshInventory = false;
        }
        var resource = desktop.Resources;
        bool freshResource = resource is { IsValid: true } && Fresh(resource.ObservedUtc, now, CapacityPolicy.ResourceSampleExpiry);
        var fallbackBudget = LaunchResourceBudget.FromSettings(settings);
        var reservations = new List<ResourceReservation>(operations.Length); var safelyObserved = new HashSet<Guid>();
        var fullBudgets = new List<LaunchResourceBudget>(operations.Length); bool forecastsKnown = true;
        ulong physicalCreditTotal = 0, commitCreditTotal = 0;
        foreach (var operation in operations)
        {
            // A retained valid receipt remains a possible occupied process, including after
            // cancellation/arrival/terminal display changes. No time-based automatic release.
            var receipt = operation.ProcessReceipt;
            if (ClosureAcknowledged(operation)) continue; // explicit manual release, never inferred from absence
            if (!LaunchDescriptions.NeedsReservation(operation.State) && receipt is not { IsValid: true }) continue;
            // A legacy estimate can explain the display, but cannot authorize a new start:
            // the original request's cost was never recorded and cannot be reconstructed.
            if (operation.ResourceBudget is null) forecastsKnown = false;
            var budget = operation.ResourceBudget ?? fallbackBudget;
            ulong fullPhysical = budget.PhysicalBytes, fullCommit = budget.CommitBytes;
            ulong physicalCredit = 0, commitCredit = 0;
            if (freshInventory && freshResource && receipt is { IsValid: true } && receipt.RecordedUtc <= now
                && receiptPids.GetValueOrDefault(receipt.ProcessId) == 1 && observations.TryGetValue(receipt.ProcessId, out var row)
                && row.ProcessStartUtcTicks == receipt.ProcessStartUtcTicks && row.ObservedUtc <= resource.ObservedUtc
                && CleanMemory(row, resource))
            {
                safelyObserved.Add(operation.Intent.OperationId);
                commitCredit = Math.Min(fullCommit, (ulong)row.PrivateCommitBytes);
                // Full/shared working set is NEVER physical credit. Missing/inconsistent private
                // working set keeps the physical forecast while valid commit remains independent.
                if (row.PrivateWorkingSetBytes is { } privateWorking && privateWorking >= 0 && privateWorking <= row.WorkingSetBytes
                    && privateWorking <= row.PrivateCommitBytes && (ulong)privateWorking <= resource.TotalPhysical - resource.AvailablePhysical)
                    physicalCredit = Math.Min(fullPhysical, (ulong)privateWorking);
            }
            reservations.Add(new(operation.Intent.OperationId, fullPhysical - physicalCredit, fullCommit - commitCredit));
            fullBudgets.Add(budget);
            physicalCreditTotal = SaturatingAdd(physicalCreditTotal, physicalCredit);
            commitCreditTotal = SaturatingAdd(commitCreditTotal, commitCredit);
        }
        // If independently sampled rows collectively claim more private consumption than the
        // global sample contains, retain that channel's whole forecast; never distribute guesses.
        bool physicalConflict = freshResource && physicalCreditTotal > resource.TotalPhysical - resource.AvailablePhysical;
        bool commitConflict = freshResource && commitCreditTotal > resource.CommitUsed;
        int credited = 0, full = 0;
        for (int index = 0; index < reservations.Count; index++)
        {
            var entry = reservations[index];
            var budget = fullBudgets[index];
            if (physicalConflict || commitConflict) reservations[index] = entry = entry with
            { PhysicalBytes = physicalConflict ? budget.PhysicalBytes : entry.PhysicalBytes, CommitBytes = commitConflict ? budget.CommitBytes : entry.CommitBytes };
            if (entry.PhysicalBytes < budget.PhysicalBytes || entry.CommitBytes < budget.CommitBytes) credited++; else full++;
        }
        int occupied = checked(observationCount + reservations.Count - safelyObserved.Count);
        string explanation = !forecastsKnown
            ? "New starts are paused: a recovered request has no recorded memory budget. Displayed legacy forecasts are provisional. Review and confirm its possible client is closed before another start."
            : !freshInventory || !freshResource
            ? "Full launch forecasts retained: fresh complete process evidence and coherent system memory measurements are required. Absence does not prove exit."
            : "Only unique exact process receipts with fresh coherent private-memory measurements reduce forecast growth. Missing or conflicting private metrics retain that budget. No account or arrival is established.";
        return new(reservations.AsReadOnly(), occupied, freshInventory, credited, full, explanation, safelyObserved.ToFrozenSet(), forecastsKnown);
    }

    // Process evidence ages against MaximumEvidenceAge; the system sample against the shared resource expiry.
    private static bool Fresh(DateTimeOffset observed, DateTimeOffset now) => Fresh(observed, now, MaximumEvidenceAge);
    private static bool Fresh(DateTimeOffset observed, DateTimeOffset now, TimeSpan age) => observed != default && observed <= now && now - observed <= age;
    private static bool ClosureAcknowledged(LaunchOperation operation) => operation.State == LaunchState.Cancelled && operation.Problem == LaunchProblem.ClientClosureAcknowledged;
    private static bool CleanMemory(SessionObservation row, ResourceSnapshot resource) => row.WorkingSetBytes >= 0
        && (ulong)row.WorkingSetBytes <= resource.TotalPhysical && row.PrivateCommitBytes >= 0 && (ulong)row.PrivateCommitBytes <= resource.CommitUsed
        && (!row.CpuPercent.HasValue || double.IsFinite(row.CpuPercent.Value) && row.CpuPercent is >= 0 and <= 100)
        && (!row.VirtualAddressBytes.HasValue || row.VirtualAddressBytes >= 0)
        && (!row.PageFaultsPerSecond.HasValue || double.IsFinite(row.PageFaultsPerSecond.Value) && row.PageFaultsPerSecond >= 0);
    private static ulong SaturatingAdd(ulong left, ulong right) => left > ulong.MaxValue - right ? ulong.MaxValue : left + right;
}
