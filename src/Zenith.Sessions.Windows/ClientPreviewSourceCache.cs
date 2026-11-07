using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// Single-owner native source inventory. The provider's readback lane owns every call, so an
/// eviction can only happen after the previous borrowed frame and native conversion have ended.
/// Disposal failure retains the source and its reservation, and quarantines new admission.
/// </summary>
internal sealed class ClientPreviewSourceCache<T> where T : class, IDisposable
{
    private readonly Dictionary<ClientPreviewTarget, Entry> _entries = [];
    private readonly LinkedList<ClientPreviewTarget> _leastRecent = new();
    public int Count => _entries.Count;
    public bool Quarantined { get; private set; }
    public ClientPreviewTarget[] Keys => _entries.Keys.ToArray();

    public void RequireUsable()
    {
        if (Quarantined) throw new ClientPreviewException();
    }

    public bool TryGet(ClientPreviewTarget target, out T source)
    {
        if (_entries.TryGetValue(target, out var entry))
        {
            _leastRecent.Remove(entry.Node); _leastRecent.AddLast(entry.Node);
            source = entry.Source; return true;
        }
        source = null!; return false;
    }

    public bool Fits(PreviewSurfaceSize addition)
    {
        RequireUsable();
        var sizes = new PreviewSurfaceSize[_entries.Count + 1]; int index = 0;
        foreach (var entry in _entries.Values) sizes[index++] = entry.Size;
        sizes[index] = addition;
        return ClientPreviewBufferReader.FitsBudget(sizes);
    }

    public bool MakeRoom(PreviewSurfaceSize addition, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); RequireUsable();
        // An impossible individual source must not evict healthy cached sources or pin the cursor.
        if (!ClientPreviewBufferReader.FitsBudget([addition])) return false;
        while (!Fits(addition))
        {
            ct.ThrowIfCancellationRequested();
            Drop(_leastRecent.First?.Value ?? throw new ClientPreviewException());
        }
        ct.ThrowIfCancellationRequested(); return true;
    }

    /// <summary>
    /// Evicts the shortest prefix of <paramref name="candidates"/> (absent ones are skipped) that lets
    /// <paramref name="addition"/> fit. Nothing is evicted when even every candidate would not free enough room.
    /// </summary>
    public bool MakeRoom(PreviewSurfaceSize addition, IReadOnlyList<ClientPreviewTarget> candidates, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ct.ThrowIfCancellationRequested(); RequireUsable();
        if (!ClientPreviewBufferReader.FitsBudget([addition])) return false;
        var removed = new HashSet<ClientPreviewTarget>(); int prefix = -1;
        if (FitsWithout(removed, addition)) prefix = 0;
        for (int index = 0; prefix < 0 && index < candidates.Count; index++)
            if (_entries.ContainsKey(candidates[index]) && removed.Add(candidates[index]) && FitsWithout(removed, addition)) prefix = index + 1;
        if (prefix < 0) return false;
        for (int index = 0; index < prefix; index++)
        {
            ct.ThrowIfCancellationRequested();
            Drop(candidates[index]);
        }
        ct.ThrowIfCancellationRequested(); return true;
    }

    public bool Contains(ClientPreviewTarget target) => _entries.ContainsKey(target);

    private bool FitsWithout(HashSet<ClientPreviewTarget> removed, PreviewSurfaceSize addition)
    {
        var sizes = new List<PreviewSurfaceSize>(_entries.Count + 1);
        foreach (var (target, entry) in _entries) if (!removed.Contains(target)) sizes.Add(entry.Size);
        sizes.Add(addition);
        return ClientPreviewBufferReader.FitsBudget(sizes);
    }

    public void Add(ClientPreviewTarget target, PreviewSurfaceSize size, T source)
    {
        ArgumentNullException.ThrowIfNull(source); RequireUsable();
        if (_entries.ContainsKey(target) || !Fits(size)) throw new ClientPreviewException();
        var node = new LinkedListNode<ClientPreviewTarget>(target);
        _entries.Add(target, new(source, size, node)); _leastRecent.AddLast(node);
    }

    public void Drop(ClientPreviewTarget target)
    {
        if (!_entries.TryGetValue(target, out var entry)) return;
        try { entry.Source.Dispose(); }
        catch (Exception) { Quarantined = true; throw new ClientPreviewException(); }
        // Keep the reservation/identity until actual terminal disposal succeeds.
        _entries.Remove(target); _leastRecent.Remove(entry.Node);
    }

    public void ReleaseAll()
    {
        bool failed = false;
        foreach (var target in Keys)
            try { Drop(target); }
            catch (ClientPreviewException) { failed = true; }
        if (_entries.Count == 0) Quarantined = false;
        if (failed) throw new ClientPreviewException();
    }

    private sealed record Entry(T Source, PreviewSurfaceSize Size, LinkedListNode<ClientPreviewTarget> Node);
}
