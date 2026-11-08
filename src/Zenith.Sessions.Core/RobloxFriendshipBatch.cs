namespace Zenith.Sessions.Core;

public sealed record RobloxFriendshipPair(RobloxAccountContext First, RobloxAccountContext Second);
public sealed record RobloxFriendshipPairResult(RobloxFriendshipPair Pair, string Status)
{ public override string ToString() => "[Friendship pair result]"; }

/// <summary>A fixed, single-use review. The application displays every pair before calling RunConfirmedAsync.</summary>
public sealed class RobloxFriendshipReview
{
    private int _used;
    public IReadOnlyList<RobloxFriendshipPair> Pairs { get; }
    public RobloxFriendshipReview(IReadOnlyList<RobloxAccountContext> accounts)
    {
        if (accounts.Count is < 2 or > 20 || accounts.Any(a => !a.IsValid) || accounts.Select(a => a.UserId).Distinct().Count() != accounts.Count
            || accounts.Select(a => a.ProfileId).Distinct().Count() != accounts.Count)
            throw new ArgumentException("Choose between 2 and 20 different, verified saved accounts.");
        var pairs = new List<RobloxFriendshipPair>();
        for (int i = 0; i < accounts.Count; i++) for (int j = i + 1; j < accounts.Count; j++) pairs.Add(new(accounts[i], accounts[j]));
        Pairs = pairs.AsReadOnly();
    }
    internal void Consume() { if (Interlocked.Exchange(ref _used, 1) != 0) throw new InvalidOperationException("Review the pairs again before another run."); }
    public override string ToString() => "[Single-use friendship review]";
}

/// <summary>One request at a time. Never launched automatically; unknown state, challenge, rate limit or failure stops the run.</summary>
public sealed class RobloxFriendshipBatch(IRobloxAccountDataService service, TimeProvider? clock = null)
{
    public async Task<IReadOnlyList<RobloxFriendshipPairResult>> RunConfirmedAsync(RobloxFriendshipReview review, CancellationToken ct,
        Action<RobloxFriendshipPairResult>? report = null)
    {
        ct.ThrowIfCancellationRequested(); review.Consume();
        var contexts = review.Pairs.SelectMany(p => new[] { p.First, p.Second }).DistinctBy(a => a.ProfileId).ToDictionary(a => a.ProfileId);
        var results = new List<RobloxFriendshipPairResult>(); bool first = true, stopped = false;
        for (int index = 0; index < review.Pairs.Count; index++)
        {
            var pair = review.Pairs[index];
            if (ct.IsCancellationRequested)
            {
                foreach (var remaining in review.Pairs.Skip(index)) Add(new(remaining, "Not attempted"));
                ct.ThrowIfCancellationRequested();
            }
            if (stopped) { Add(new(pair, "Not attempted")); continue; }
            try
            {
                var relation = await Send(pair.First, pair.Second, RobloxAccountRead.FriendshipStatus);
                if (relation.Observation?.Friendship is not { } status || !Enum.IsDefined(status)) { Stop("Friendship status unavailable"); continue; }
                if (status == RobloxFriendshipStatus.Friends) { Add(new(pair, "Already friends")); continue; }
                if (status == RobloxFriendshipStatus.NotFriends) await Send(pair.First, pair.Second, RobloxAccountRead.RequestFriendship);
                if (status == RobloxFriendshipStatus.RequestReceived) await Send(pair.First, pair.Second, RobloxAccountRead.AcceptFriendship);
                else await Send(pair.Second, pair.First, RobloxAccountRead.AcceptFriendship);
                var confirmed = await Send(pair.First, pair.Second, RobloxAccountRead.FriendshipStatus);
                if (confirmed.Observation?.Friendship == RobloxFriendshipStatus.Friends) Add(new(pair, "Friends confirmed"));
                else Stop("Request sent; friendship not confirmed");
            }
            catch (BatchStopped error) { Stop(error.Status); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Add(new(pair, "Interrupted; review friendship state before retrying"));
                foreach (var remaining in review.Pairs.Skip(index + 1)) Add(new(remaining, "Not attempted"));
                throw;
            }
            catch (Exception error) when (error is AccountLinkException or RobloxSessionException)
            { Stop("Stopped; review saved sign-ins and friendship state before retrying"); }
            void Stop(string status) { Add(new(pair, status)); stopped = true; }
        }
        return results.AsReadOnly();
        void Add(RobloxFriendshipPairResult result) { results.Add(result); report?.Invoke(result); }

        async Task<RobloxAccountResult> Send(RobloxAccountContext from, RobloxAccountContext to, RobloxAccountRead kind)
        {
            if (!first) await Task.Delay(TimeSpan.FromSeconds(1), clock ?? TimeProvider.System, ct).ConfigureAwait(false);
            first = false; ct.ThrowIfCancellationRequested();
            var result = await service.ReadAccountAsync(contexts[from.ProfileId], kind, contexts[to.ProfileId], ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); contexts[from.ProfileId] = result.Context;
            if (result.Outcome != RobloxAccountOutcome.Success) throw new BatchStopped(result.Outcome switch
            {
                RobloxAccountOutcome.Challenge => "Stopped: Roblox security check required",
                RobloxAccountOutcome.RateLimited => "Stopped: Roblox is busy or rate limiting requests",
                _ => "Stopped: request not confirmed; review before retrying"
            });
            return result;
        }
    }
    private sealed class BatchStopped(string status) : Exception("Friendship run stopped") { public string Status { get; } = status; }
}
