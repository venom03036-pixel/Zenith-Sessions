namespace Zenith.Sessions.Core;

public sealed record LaunchBatchMember(Guid ProfileId, long ProfileRevision, int Priority, LaunchAccountBinding? Binding)
{
    public Guid? RetryOfOperationId => RetrySource?.Intent.OperationId;
    internal LaunchOperation? RetrySource { get; init; }
    /// <summary>The destination frozen for this account when its review was captured. In a spread review it is the place
    /// alone: the queue hands out the review's listed servers only when it records the requests.</summary>
    public RobloxRequestedDestination? Destination { get; internal init; }
}

/// <summary>
/// One reviewed recipient snapshot with each account's frozen destination, valid for one submission to its original queue.
/// <see cref="Destination"/> is the shared destination, or the place alone when <see cref="Spread"/> holds listed servers to hand out.
/// </summary>
public sealed class LaunchBatchReview
{
    private readonly object _owner;
    private int _consumed;
    internal long StopGeneration { get; }
    public RobloxRequestedDestination Destination { get; }
    public IReadOnlyList<LaunchBatchMember> Recipients { get; }
    /// <summary>
    /// The listed servers a spread review hands out, in the order frozen at review; null when every account shares
    /// <see cref="Destination"/>. Its counts describe the reviewed accounts. The queue gives these servers only to the requests
    /// it records (see <see cref="LaunchCoordinator.EnqueueBatchAsync"/>); <see cref="ServerSpread.ExplainRecorded"/> describes that.
    /// </summary>
    public ServerSpread? Spread { get; }

    internal LaunchBatchReview(object owner, long generation, RobloxRequestedDestination destination, LaunchBatchMember[] recipients,
        ServerSpread? spread = null)
    {
        _owner = owner;
        StopGeneration = generation;
        Destination = destination;
        // Freeze each account's destination with the snapshot. A spread's servers are handed out when the requests are recorded,
        // so an account that turns out to be pending, or cannot be recorded, uses none of them up.
        for (int i = 0; i < recipients.Length; i++)
            recipients[i] = recipients[i] with { Destination = destination };
        Recipients = Array.AsReadOnly(recipients);
        Spread = spread;
    }

    internal bool TryConsume(object owner) => ReferenceEquals(_owner, owner) && Interlocked.Exchange(ref _consumed, 1) == 0;
}

public enum LaunchBatchDisposition { Recorded, AlreadyPending, QueueFull, ResourceBudgetUnavailable, RetryNoLongerEligible }
public sealed record LaunchBatchRow(Guid ProfileId, LaunchBatchDisposition Disposition, LaunchOperation? Operation = null);

public sealed partial class LaunchCoordinator
{
    private readonly object _batchReviewOwner = new();

    /// <summary>Freeze an exact requested destination for every selected account before presenting a launch review.</summary>
    public LaunchBatchReview CaptureBatchReview(RobloxRequestedDestination destination,
        IReadOnlyList<(AccountProfile Profile, LaunchAccountBinding? Binding)> recipients)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if(destination.ServerKind==RobloxServerKind.Private) throw new ArgumentException("Private servers require an explicit attended trial.",nameof(destination));
        var (generation, members) = CaptureMembers(recipients);
        return new(_batchReviewOwner, generation, destination, members);
    }

    /// <summary>
    /// Freeze the selected accounts and the listed public servers of <paramref name="placeId"/> with room for one more player
    /// and a spare seat, in an order drawn from <paramref name="random"/> (see <see cref="RequestedServerSelection.SpreadServers"/>).
    /// Submitting the review gives each recorded request a different one of those servers while they last, and the rest the
    /// place with no chosen server. Repeated accounts count once. The list is read only here, so later changes to it never
    /// reach the review.
    /// </summary>
    public LaunchBatchReview CaptureSpreadBatchReview(long placeId, IReadOnlyList<RobloxPublicServer> servers,
        IReadOnlyList<(AccountProfile Profile, LaunchAccountBinding? Binding)> recipients, Random random)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(random);
        var place = new RobloxRequestedDestination(placeId);
        var (generation, members) = CaptureMembers(recipients);
        var spread = RequestedServerSelection.SpreadServers(placeId, servers, members.Length, _time.GetUtcNow(), shuffle: true, random);
        return new(_batchReviewOwner, generation, place, members, spread);
    }

    private (long Generation, LaunchBatchMember[] Members) CaptureMembers(
        IReadOnlyList<(AccountProfile Profile, LaunchAccountBinding? Binding)> recipients)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        long generation = Interlocked.Read(ref _generation);
        int count = RequireBatchCount(recipients.Count);
        var members = new LaunchBatchMember[count];
        for (int i = 0; i < count; i++)
            members[i] = CaptureBatchMember(recipients[i].Profile, recipients[i].Binding);
        return (generation, DistinctMembers(members));
    }

    /// <summary>Review retries for requests that ended before any client started and all chose one destination:
    /// the same exact server, or the same place with no server.</summary>
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
            // A place taken from the profile's preference was never a reviewed destination, so only a
            // chosen place (with or without an exact server) can be pinned again.
            if (!EndedBeforeAnyClient(previous)
                || previous.Intent is not { } intent
                || !intent.HasExplicitDestination || !HasValidDestination(intent) || intent.PrivateServer is not null
                || intent.OperationId == Guid.Empty || intent.ProfileId != member.ProfileId || intent.UserId != binding.UserId
                || intent.AuthorizationGeneration <= 0)
                throw new ArgumentException("Retry requires a request for the same account that ended before any client started, with a chosen place.", nameof(recipients));
            if (destination is null) destination = new(intent.PlaceId, intent.RequestedJobId, intent.RequestedServerKind);
            else if (destination.PlaceId != intent.PlaceId || destination.JobId != intent.RequestedJobId
                || destination.ServerKind != intent.RequestedServerKind)
                throw new ArgumentException("A retry review must use one destination: the same place, and the same server or no server.", nameof(recipients));
            members[i] = member with { RetrySource = previous };
        }
        return new(_batchReviewOwner, generation, destination!, DistinctMembers(members));
    }

    // Only a failure recorded before native startup qualifies: the provider refused the start, or
    // preparation was refused or expired before dispatch. A process receipt or any other problem stays
    // out, and so does every row that is not Failed: an Uncertain, ReviewRequired or
    // ClientStartedUnverified row may have a client, and a Cancelled row was a user's or a stop's decision.
    private static bool EndedBeforeAnyClient(LaunchOperation previous) => previous is
    {
        State: LaunchState.Failed, ProcessReceipt: null,
        Problem: LaunchProblem.StartupRejected or LaunchProblem.PreparationRejected or LaunchProblem.PreparationExpired
    };

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
    public Task<IReadOnlyList<LaunchBatchRow>> EnqueueBatchAsync(LaunchBatchReview review, CancellationToken ct = default)
        => EnqueueBatchCoreAsync(review, false, ct);

    /// <summary>An explicit Launch gesture records and starts this fresh batch, including from the initial paused state.
    /// It cannot resume unrelated queued work, reuse a stopped review, or infer trial consent. Queue-only callers stay paused.</summary>
    public Task<IReadOnlyList<LaunchBatchRow>> StartBatchAsync(LaunchBatchReview review, CancellationToken ct = default)
        => EnqueueBatchCoreAsync(review, true, ct);

    private async Task<IReadOnlyList<LaunchBatchRow>> EnqueueBatchCoreAsync(LaunchBatchReview review, bool start, CancellationToken ct)
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

            if (start && _paused && _operations.Any(row => row.State == LaunchState.Queued || LaunchDescriptions.AwaitsCapacity(row)))
                throw new LaunchQueueException("Other requests are paused in the queue. Resume or cancel them before launching this selection.");

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
            // A spread's listed servers go only to the requests recorded here, in recipient order: first to requests that can be
            // queued now, then any left over to requests recorded Blocked, which never start unless they are queued again.
            var spread = review.Spread;
            int nextServer = 0;
            List<(int Row, int Operation)>? blockedWithoutServer = null;
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
                var destination = member.Destination ?? review.Destination;
                var intent = new LaunchIntent(Guid.NewGuid(), member.ProfileId, member.ProfileRevision,
                    member.Binding?.UserId ?? 0, destination.PlaceId, member.Priority, _time.GetUtcNow(),
                    member.Binding?.AuthorizationGeneration ?? 0, destination.JobId, destination.ServerKind, true);
                bool withServer = spread is not null && nextServer < spread.SpreadCount
                    && NewRequestProblem(intent, trial: false) == LaunchProblem.None;
                if (withServer) intent = WithServer(intent, spread!.Destinations[nextServer]);
                LaunchOperation operation;
                try { operation = CreateQueuedOperation(intent, trial: false, ct); }
                catch (LaunchQueueException)
                {
                    // Not recorded, so its server stays for the next request.
                    rows[i] = new(member.ProfileId, LaunchBatchDisposition.ResourceBudgetUnavailable);
                    continue;
                }
                if (withServer) nextServer++;
                else if (spread is not null && operation.State == LaunchState.Blocked) (blockedWithoutServer ??= []).Add((i, operations.Count));
                operations.Add(operation);
                rows[i] = new(member.ProfileId, LaunchBatchDisposition.Recorded, operation);
            }
            // Blocked requests take the servers left over, before anything is saved or published. Their forecasts were resolved
            // before the server was attached; a Blocked request never starts unless it is queued again.
            if (blockedWithoutServer is not null)
                foreach (var (row, index) in blockedWithoutServer)
                {
                    if (nextServer >= spread!.SpreadCount) break;
                    var given = operations[index] with { Intent = WithServer(operations[index].Intent, spread.Destinations[nextServer++]) };
                    operations[index] = given;
                    rows[row] = rows[row] with { Operation = given };
                }
            RequireCurrent();
            if (operations.Count == 0) return Array.AsReadOnly(rows);

            TrimHistory();
            int firstAdded = _operations.Count;
            _operations.AddRange(operations);
            bool resumed = start && _paused && operations.Any(row => row.State == LaunchState.Queued);
            if (resumed)
            {
                _pauseReason = LaunchQueuePauseReason.None; _paused = false;
                // Panic can run without this lane. Its generation wins before the write and again after it.
                if (Stopped()) _paused = true;
            }
            // Once a write begins, own it through durable completion. A concurrent stop
            // cancels the entire accepted subset before the runner can acquire this lane.
            await PersistAsync(CancellationToken.None, notify: false).ConfigureAwait(false);
            try { PublishWhileCurrent(Stopped); }
            finally
            {
                if (Stopped())
                {
                    if (resumed) _paused = true;
                    var now = _time.GetUtcNow();
                    int index = firstAdded;
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

    private static LaunchIntent WithServer(LaunchIntent intent, RobloxRequestedDestination server)
        => intent with { RequestedJobId = server.JobId, RequestedServerKind = server.ServerKind };
}
