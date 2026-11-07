using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

/// <summary>
/// Matches the dashboard's client rows to the newest process observations. Owned by the shell view model and used on the
/// dispatcher thread only: the key set and index map are reused every presentation, so once warm, matching allocates nothing
/// and costs O(rows + observations) instead of building two key strings per comparison (Rule 7).
/// Row identity, order and the add/remove rules are the dashboard's.
/// </summary>
internal sealed class SessionRowReconciler(Action<SessionRow> removed, Func<SessionObservation, SessionRow> create)
{
    private readonly HashSet<SessionProcessKey> _live = [];
    private readonly Dictionary<SessionProcessKey, int> _index = [];

    /// <returns>True when a row was added or removed.</returns>
    public bool Reconcile(IList<SessionRow> rows, IReadOnlyList<SessionObservation> observations)
    {
        _live.Clear(); _index.Clear();
        for (int i = 0; i < observations.Count; i++) _live.Add(observations[i].ProcessKey);
        bool changed = false;
        // Every removal happens before any index is recorded, so recorded indexes stay valid.
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            var row = rows[i];
            if (_live.Contains(row.ProcessKey)) continue;
            removed(row); rows.RemoveAt(i); changed = true;
        }
        for (int i = 0; i < rows.Count; i++) _index.TryAdd(rows[i].ProcessKey, i);
        for (int i = 0; i < observations.Count; i++)
        {
            var observation = observations[i];
            var key = observation.ProcessKey;
            if (_index.TryGetValue(key, out int index)) { rows[index].Update(observation); continue; }
            // A duplicate later in the same snapshot updates the row just added, as before.
            rows.Add(create(observation)); _index.Add(key, rows.Count - 1); changed = true;
        }
        return changed;
    }
}
