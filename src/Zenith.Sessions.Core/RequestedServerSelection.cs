using System.Globalization;

namespace Zenith.Sessions.Core;

public enum RequestedServerChoiceKind { SpareSeat, ExactFit, NoRoom, InvalidRecipientCount }

/// <summary>
/// A public server chosen from one list as a requested destination for a whole batch. Reported room is advice:
/// it never reserves a seat or proves access, account identity or arrival.
/// </summary>
/// <param name="UsableCount">Distinct fresh, well-formed servers of the place that were compared.</param>
public sealed record RequestedServerChoice(RequestedServerChoiceKind Kind, RobloxPublicServer? Server, int RecipientCount,
    int UsableCount, string Explanation);

/// <summary>
/// One requested destination per account, in recipient order: a different listed public server for each account while
/// servers last, then the place with no chosen server. Reported room is advice: it never reserves a seat or proves arrival.
/// The spread carries counts, not text: the queue hands its servers only to the requests it records, so the one wording is
/// <see cref="ExplainRecorded"/>, written from those requests.
/// </summary>
/// <param name="SpreadCount">Accounts given their own listed server.</param>
/// <param name="PlaceOnlyCount">Accounts left with the place and no chosen server.</param>
/// <param name="UsableCount">Distinct fresh, well-formed servers of the place that were compared.</param>
public sealed record ServerSpread(IReadOnlyList<RobloxRequestedDestination> Destinations, int SpreadCount, int PlaceOnlyCount,
    int UsableCount)
{
    /// <summary>
    /// What this spread gave the <paramref name="requests"/> a queue recorded from it, <paramref name="withServer"/> of them with
    /// their own listed server. It counts requests, not accounts that joined: a recorded request can still be blocked.
    /// </summary>
    public string ExplainRecorded(int requests, int withServer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(withServer);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(withServer, requests);
        // Only this spread's servers can be handed out, and every one is used before a request goes without one.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(withServer, SpreadCount);
        string count = requests == 1 ? "1 request" : requests.ToString(CultureInfo.InvariantCulture) + " requests";
        string given = withServer.ToString(CultureInfo.InvariantCulture);
        int rest = requests - withServer;
        string all = requests == 1 ? "the request asks" : $"the {count} ask";
        string explanation = requests == 0 ? "No request was recorded, so no listed server was handed out."
            : withServer == requests
                ? (requests == 1 ? "The request got its own listed server" : $"Each of the {count} got its own listed server")
                    + " with room for one more player and a spare seat."
            : withServer > 0
                ? $"{given} of {count} got their own listed server with room for one more player and a spare seat. Only {given} loaded {(withServer == 1 ? "server had" : "servers had")} that room, so the other {rest.ToString(CultureInfo.InvariantCulture)} {(rest == 1 ? "asks" : "ask")} for the place without a chosen server."
            : UsableCount == 0
                ? $"No fresh public server is loaded, so {all} for the place without a chosen server. Browse public servers first to spread them."
                : $"None of the loaded servers had room for one more player and a spare seat, so {all} for the place without a chosen server.";
        return withServer > 0 ? explanation + " Seats are not reserved, room can change and arrival still needs verification." : explanation;
    }
}

public static class RequestedServerSelection
{
    /// <summary>
    /// Hand each of <paramref name="count"/> accounts a different fresh public server of <paramref name="placeId"/> that has room
    /// for that account and one spare seat (players + 2 &lt;= maximum), in list order or, when <paramref name="shuffle"/> is set,
    /// in an order drawn from <paramref name="random"/>. A server listed twice is judged by its newest copy, as
    /// <see cref="ChooseForBatch"/> judges it, and is handed out once. Accounts beyond the servers with room get the place
    /// with no chosen server. Pure and bounded by the list length and count; it sends no request.
    /// </summary>
    public static ServerSpread SpreadServers(long placeId, IReadOnlyList<RobloxPublicServer> page, int count, DateTimeOffset now,
        bool shuffle, Random? random)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(placeId);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (shuffle) ArgumentNullException.ThrowIfNull(random);

        var newest = NewestFreshCopies(placeId, page, now);
        var withRoom = new List<(RobloxPublicServer Server, int Index)>(newest.Count);
        foreach (var candidate in newest.Values)
            // Widened so huge reported counts cannot wrap into apparent room.
            if ((long)candidate.Server.MaxPlayers - candidate.Server.Playing >= 2) withRoom.Add(candidate);
        withRoom.Sort((left, right) => left.Index.CompareTo(right.Index));
        if (shuffle)
            for (int i = withRoom.Count - 1; i > 0; i--)
            {
                int j = random!.Next(i + 1);
                (withRoom[i], withRoom[j]) = (withRoom[j], withRoom[i]);
            }

        int spread = Math.Min(count, withRoom.Count), placeOnly = count - spread;
        var destinations = new RobloxRequestedDestination[count];
        RobloxRequestedDestination? place = null;
        for (int i = 0; i < count; i++)
            destinations[i] = i < spread ? withRoom[i].Server.RequestedDestination : place ??= new(placeId);
        return new(Array.AsReadOnly(destinations), spread, placeOnly, newest.Count);
    }

    /// <summary>
    /// The fullest fresh public server of <paramref name="placeId"/> with room for every recipient and one spare seat,
    /// else the fullest with exactly enough room, else none. Equal player counts keep the list's order. Stale, foreign,
    /// malformed, full and overfull rows are never chosen, and a server listed twice is judged by its newest copy
    /// (the fuller copy when both are equally new). Pure and bounded by the list length; it sends no request.
    /// </summary>
    public static RequestedServerChoice ChooseForBatch(long placeId, IReadOnlyList<RobloxPublicServer> page, int recipients, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(placeId);
        if (recipients <= 0)
            return new(RequestedServerChoiceKind.InvalidRecipientCount, null, recipients, 0,
                "Check the accounts to send, or select one account, before finding a server with room. No server was chosen.");

        var newest = NewestFreshCopies(placeId, page, now);
        (RobloxPublicServer Server, int Index)? spare = null, exact = null;
        foreach (var candidate in newest.Values)
        {
            // Widened so huge reported counts cannot wrap into an apparent fit.
            long open = (long)candidate.Server.MaxPlayers - candidate.Server.Playing;
            if (open >= (long)recipients + 1) spare = Fuller(spare, candidate);
            else if (open == recipients) exact = Fuller(exact, candidate);
        }

        string accounts = recipients == 1 ? "1 account" : recipients.ToString(CultureInfo.InvariantCulture) + " accounts";
        if (spare is { } withSpare)
            return new(RequestedServerChoiceKind.SpareSeat, withSpare.Server, recipients, newest.Count,
                $"Chose the fullest listed server with room for {accounts} and a spare seat: {Players(withSpare.Server)} when checked. Seats are not reserved, room can change and arrival still needs verification.");
        if (exact is { } exactFit)
            return new(RequestedServerChoiceKind.ExactFit, exactFit.Server, recipients, newest.Count,
                $"Chose the fullest listed server with exactly enough room for {accounts}: {Players(exactFit.Server)} when checked. No seat is spare; seats are not reserved, room can change and arrival still needs verification.");
        return new(RequestedServerChoiceKind.NoRoom, null, recipients, newest.Count, newest.Count == 0
            ? "No fresh public server was listed, so no server was chosen. Try again later or browse the servers yourself."
            : $"None of the {newest.Count.ToString(CultureInfo.InvariantCulture)} listed servers had room for {accounts}, so no server was chosen. Try again later or browse the servers yourself.");
    }

    // The deciding copy of each fresh, well-formed server of the place and its list position: the newest copy, or the fuller
    // one when both are equally new. Later positions win only when that copy decides.
    private static Dictionary<Guid, (RobloxPublicServer Server, int Index)> NewestFreshCopies(long placeId,
        IReadOnlyList<RobloxPublicServer> page, DateTimeOffset now)
    {
        var newest = new Dictionary<Guid, (RobloxPublicServer Server, int Index)>();
        for (int index = 0; index < page.Count; index++)
        {
            var row = page[index];
            if (row is null || row.PlaceId != placeId || !RobloxRequestedDestination.TryNormalizeJobId(row.JobId, out var job)
                || row.Playing < 0 || row.MaxPlayers <= 0 || !RequestedServerCapacity.IsFresh(row.ObservedUtc, now)) continue;
            var id = Guid.ParseExact(job!, "D");
            if (newest.TryGetValue(id, out var seen) && (seen.Server.ObservedUtc > row.ObservedUtc
                || seen.Server.ObservedUtc == row.ObservedUtc && seen.Server.Playing >= row.Playing)) continue;
            newest[id] = (row, index);
        }
        return newest;
    }

    private static (RobloxPublicServer Server, int Index) Fuller((RobloxPublicServer Server, int Index)? best, (RobloxPublicServer Server, int Index) candidate)
        => best is not { } current || candidate.Server.Playing > current.Server.Playing
            || candidate.Server.Playing == current.Server.Playing && candidate.Index < current.Index ? candidate : current;

    private static string Players(RobloxPublicServer server) => server.Playing.ToString(CultureInfo.InvariantCulture) + " of "
        + server.MaxPlayers.ToString(CultureInfo.InvariantCulture) + " players";
}
