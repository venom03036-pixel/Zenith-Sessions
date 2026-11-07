namespace Zenith.Sessions.Core;

public sealed record LaunchBatchMember(Guid ProfileId, long ProfileRevision, int Priority, LaunchAccountBinding? Binding)
{
    public Guid? RetryOfOperationId => RetrySource?.Intent.OperationId;
    internal LaunchOperation? RetrySource { get; init; }
}

/// <summary>One reviewed destination and recipient snapshot, valid for one submission to its original queue.</summary>
public sealed class LaunchBatchReview
{
    private readonly object _owner;
    private int _consumed;
    internal long StopGeneration { get; }
    public RobloxRequestedDestination Destination { get; }
    public IReadOnlyList<LaunchBatchMember> Recipients { get; }

    internal LaunchBatchReview(object owner, long generation, RobloxRequestedDestination destination, LaunchBatchMember[] recipients)
    {
        _owner = owner;
        StopGeneration = generation;
        Destination = destination;
        Recipients = Array.AsReadOnly(recipients);
    }

    internal bool TryConsume(object owner) => ReferenceEquals(_owner, owner) && Interlocked.Exchange(ref _consumed, 1) == 0;
}

public enum LaunchBatchDisposition { Recorded, AlreadyPending, QueueFull, ResourceBudgetUnavailable, RetryNoLongerEligible }
public sealed record LaunchBatchRow(Guid ProfileId, LaunchBatchDisposition Disposition, LaunchOperation? Operation = null);

public sealed partial class LaunchCoordinator
{
    private readonly object _batchReviewOwner = new();

    /// <summary>Freeze an exact requested destination and selected accounts before presenting a launch review.</summary>
    public LaunchBatchReview CaptureBatchReview(RobloxRequestedDestination destination,
        IReadOnlyList<(AccountProfile Profile, LaunchAccountBinding? Binding)> recipients)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(recipients);
        long generation = Interlocked.Read(ref _generation);
        int count = RequireBatchCount(recipients.Count);
        var members = new LaunchBatchMember[count];
        for (int i = 0; i < count; i++)
            members[i] = CaptureBatchMember(recipients[i].Profile, recipients[i].Binding);
        return new(_batchReviewOwner, generation, destination, DistinctMembers(members));
    }

    /// <summary>Review retries for rejected starts that originally requested the same exact server.</summary>
    public LaunchBatchReview CaptureBatchRetryReview(
        IReadOnlyList<(LaunchOperation Previous, AccountProfile Profile, LaunchAccountBinding Binding)> recipients)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        long generation = Interlocked.Read(ref _generation);
        int count = RequireBatchCount(recipients.Count);
        var members = new LaunchBatchMember[count];
        RobloxRequestedDestination? destination = null;
        for (int i = 0; i < count; i++)
        {
            var (previous, profile, binding) = recipients[i];
            ArgumentNullException.ThrowIfNull(previous);
            ArgumentNullException.ThrowIfNull(binding);
            var member = CaptureBatchMember(profile, binding);
            if (previous is not { State: LaunchState.Failed, Problem: LaunchProblem.StartupRejected, ProcessReceipt: null }
                || previous.Intent is not { } intent
                || !intent.HasExplicitDestination || intent.RequestedJobId is null || !HasValidDestination(intent)
                || intent.OperationId == Guid.Empty || intent.ProfileId != member.ProfileId || intent.UserId != binding.UserId
                || intent.AuthorizationGeneration <= 0)
                throw new ArgumentException("Retry requires a rejected start for the same account and a known exact server.", nameof(recipients));
            if (destination is null) destination = new(intent.PlaceId, intent.RequestedJobId, intent.RequestedServerKind);
            else if (destination.PlaceId != intent.PlaceId || destination.JobId != intent.RequestedJobId
                || destination.ServerKind != intent.RequestedServerKind)
                throw new ArgumentException("A retry review must use one exact destination.", nameof(recipients));
            members[i] = member with { RetrySource = previous };
        }
        return new(_batchReviewOwner, generation, destination!, DistinctMembers(members));
    }

    private static int RequireBatchCount(int count)
        => count is >= 1 and <= MaximumPendingOperations ? count : throw new ArgumentOutOfRangeException(nameof(count));

    private static LaunchBatchMember CaptureBatchMember(AccountProfile profile, LaunchAccountBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validated();
        if (binding is { UserId: <= 0 } or { AuthorizationGeneration: <= 0 })
            throw new ArgumentException("A launch binding must identify a positive verified user and authorization generation.", nameof(binding));
        return new(profile.Id, profile.Revision, profile.Priority, binding);
    }

    private static LaunchBatchMember[] DistinctMembers(LaunchBatchMember[] members)
    {
        var unique = new Dictionary<Guid, LaunchBatchMember>(members.Length);
        var result = new List<LaunchBatchMember>(members.Length);
        foreach (var member in members)
        {
            if (unique.TryAdd(member.ProfileId, member)) result.Add(member);
            else if (unique[member.ProfileId] != member)
                throw new ArgumentException("This account has conflicting selections. Review its current profile and sign-in.", nameof(members));
        }
        return result.ToArray();
    }

    /// <summary>Record the accepted subset together. The ordinary launch and authorization gates apply to every row.</summary>
    public async Task<IReadOnlyList<LaunchBatchRow>> EnqueueBatchAsync(LaunchBatchReview review, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireHealthy();
            bool Stopped() => review.StopGeneration != Interlocked.Read(ref _generation)
                || ct.IsCancellationRequested || _stop.IsCancellationRequested;
            void RequireCurrent()
            {
                ct.ThrowIfCancellationRequested();
                if (Stopped()) throw new LaunchQueueException("The launch queue was stopped. Review the accounts and destination again.");
            }
            RequireCurrent();
            if (!review.TryConsume(_batchReviewOwner))
                throw new LaunchQueueException("This launch review was already submitted or belongs to another queue.");

            var pendingProfiles = new HashSet<Guid>();
            var latestByProfile = review.Recipients[0].RetrySource is null ? null : new Dictionary<Guid, LaunchOperation>();
            int remaining = MaximumPendingOperations;
            foreach (var operation in _operations)
            {
                // List order is the commit order, including after reopening the queue file.
                if (latestByProfile is not null) latestByProfile[operation.Intent.ProfileId] = operation;
                if (IsPendingOrUnresolved(operation))
                {
                    pendingProfiles.Add(operation.Intent.ProfileId);
                    remaining--;
                }
            }
            var operations = new List<LaunchOperation>(Math.Min(review.Recipients.Count, Math.Max(remaining, 0)));
            var rows = new LaunchBatchRow[review.Recipients.Count];
            for (int i = 0; i < rows.Length; i++)
            {
                RequireCurrent();
                var member = review.Recipients[i];
                if (pendingProfiles.Contains(member.ProfileId))
                {
                    rows[i] = new(member.ProfileId, LaunchBatchDisposition.AlreadyPending);
                    continue;
                }
                if (member.RetrySource is { } source && latestByProfile?.GetValueOrDefault(member.ProfileId) != source)
                {
                    rows[i] = new(member.ProfileId, LaunchBatchDisposition.RetryNoLongerEligible);
                    continue;
                }
                if (operations.Count >= remaining)
                {
                    rows[i] = new(member.ProfileId, LaunchBatchDisposition.QueueFull);
                    continue;
                }
                var destination = review.Destination;
                var intent = new LaunchIntent(Guid.NewGuid(), member.ProfileId, member.ProfileRevision,
                    member.Binding?.UserId ?? 0, destination.PlaceId, member.Priority, _time.GetUtcNow(),
                    member.Binding?.AuthorizationGeneration ?? 0, destination.JobId, destination.ServerKind, true);
                LaunchOperation operation;
                try { operation = CreateQueuedOperation(intent, trial: false, ct); }
                catch (LaunchQueueException)
                {
                    rows[i] = new(member.ProfileId, LaunchBatchDisposition.ResourceBudgetUnavailable);
                    continue;
                }
                operations.Add(operation);
                rows[i] = new(member.ProfileId, LaunchBatchDisposition.Recorded, operation);
            }
            RequireCurrent();
            if (operations.Count == 0) return Array.AsReadOnly(rows);

            TrimHistory();
            int start = _operations.Count;
            _operations.AddRange(operations);
            // Once a write begins, own it through durable completion. A concurrent stop
            // cancels the entire accepted subset before the runner can acquire this lane.
            await PersistAsync(CancellationToken.None, notify: false).ConfigureAwait(false);
            try { PublishWhileCurrent(Stopped); }
            finally
            {
                if (Stopped())
                {
                    var now = _time.GetUtcNow();
                    int index = start;
                    for (int i = 0; i < rows.Length; i++)
                    {
                        if (rows[i].Operation is not { } operation) continue;
                        var cancelled = operation with { State = LaunchState.Cancelled, Problem = LaunchProblem.None, UpdatedUtc = now };
                        _operations[index++] = cancelled;
                        rows[i] = rows[i] with { Operation = cancelled };
                    }
                    await PersistAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            Signal();
            return Array.AsReadOnly(rows);
        }
        finally { _serial.Release(); }
    }
}
