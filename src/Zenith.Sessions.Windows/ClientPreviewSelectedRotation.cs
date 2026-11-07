using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// Admission for pinned (selected) previews whose sources cannot all fit the shared byte budget. Sources that
/// fit stay put across batches. An over-budget selection shares one rotating slot: each batch takes at most one
/// turn, evicting the current slot holder (then, only if still needed, the other sources that were already
/// resident when the batch began) and handing the slot to the next waiting target after a persisted cursor in
/// selection order. So at most one source is torn down and re-created per batch, no source is created and
/// evicted in the same batch, and every waiting row still gets a turn within (waiting rows) batches.
/// Single owner: only the provider's readback lane calls it, so it needs no locking.
/// </summary>
internal sealed class ClientPreviewSelectedRotation
{
    private ClientPreviewTarget? _cursor;
    private ClientPreviewTarget[] _candidates = [];
    private bool _turnTaken;

    /// <summary>
    /// Starts one selected batch and returns its visit order: cached targets first, in selection order, so the
    /// outgoing slot holder is captured from its warm source before any hand-off; then the uncached targets,
    /// cyclically from the one after the cursor, so a fixed selection order cannot favour the first rows.
    /// </summary>
    public int[] BeginBatch<T>(IReadOnlyList<ClientPreviewTarget> targets, ClientPreviewSourceCache<T> cache) where T : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(targets); ArgumentNullException.ThrowIfNull(cache);
        _turnTaken = false;
        var order = new int[targets.Count]; int next = 0, start = 0;
        var resident = new List<ClientPreviewTarget>(targets.Count);
        for (int index = 0; index < targets.Count; index++)
        {
            if (!cache.Contains(targets[index])) continue;
            order[next++] = index;
            // The slot holder is the first eviction candidate; the rest follow in least-recent (visit) order.
            if (targets[index] == _cursor) resident.Insert(0, targets[index]); else resident.Add(targets[index]);
        }
        for (int index = 0; index < targets.Count; index++)
            if (targets[index] == _cursor) { start = index + 1; break; }
        for (int step = 0; step < targets.Count; step++)
        {
            int index = (start + step) % targets.Count;
            if (!cache.Contains(targets[index])) order[next++] = index;
        }
        _candidates = resident.ToArray();
        return order;
    }

    /// <summary>
    /// Admits a target whose source is not cached. Spare room is used without evicting anything; otherwise the
    /// batch's single rotation turn may evict only sources that were resident when the batch began.
    /// </summary>
    public bool Admit<T>(ClientPreviewSourceCache<T> cache, ClientPreviewTarget target, PreviewSurfaceSize size, CancellationToken ct)
        where T : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(cache); ArgumentNullException.ThrowIfNull(target);
        ct.ThrowIfCancellationRequested();
        if (cache.Fits(size)) return true;
        if (_turnTaken || !cache.MakeRoom(size, _candidates, ct)) return false;
        _turnTaken = true; _cursor = target;
        return true;
    }

    public void Reset() { _cursor = null; _candidates = []; _turnTaken = false; }
}
