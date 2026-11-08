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

public static class RequestedServerSelection
{
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

        // The deciding copy of each server and its list position; later positions win only when that copy decides.
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

    private static (RobloxPublicServer Server, int Index) Fuller((RobloxPublicServer Server, int Index)? best, (RobloxPublicServer Server, int Index) candidate)
        => best is not { } current || candidate.Server.Playing > current.Server.Playing
            || candidate.Server.Playing == current.Server.Playing && candidate.Index < current.Index ? candidate : current;

    private static string Players(RobloxPublicServer server) => server.Playing.ToString(CultureInfo.InvariantCulture) + " of "
        + server.MaxPlayers.ToString(CultureInfo.InvariantCulture) + " players";
}
