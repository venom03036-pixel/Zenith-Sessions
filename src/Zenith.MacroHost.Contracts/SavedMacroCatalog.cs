namespace Zenith.MacroHost.Contracts;

/// <summary>Opaque load ownership; an old load can never replace a newer library epoch.</summary>
public sealed class SavedMacroCatalogLoadAttempt
{
    internal Guid Owner { get; }
    internal Guid Epoch { get; }
    internal SavedMacroCatalogLoadAttempt(Guid owner, Guid epoch) { Owner = owner; Epoch = epoch; }
}

/// <summary>Capture before an asynchronous save; publish only its exact immutable metadata after success.</summary>
public sealed class SavedMacroSaveAttempt
{
    internal Guid Owner { get; }
    internal Guid Epoch { get; }
    public Guid MacroId { get; }
    public Guid SavedRevision { get; } = Guid.NewGuid();
    internal SavedMacroSaveAttempt(Guid owner, Guid epoch, Guid macroId) { Owner = owner; Epoch = epoch; MacroId = macroId; }
}

/// <summary>
/// Thread-safe bounded saved metadata only. The document owner performs all IO and reports its outcome here.
/// Pending attempts are replaced per ID and removed on deletion/reset; no deleted-descriptor history grows.
/// </summary>
public sealed class SavedMacroCatalog : IMacroCatalogSource
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, SavedMacroDescriptor> _items = new();
    private readonly Dictionary<Guid, SavedMacroSaveAttempt> _saves = new();
    private readonly Guid _hostSession = Guid.NewGuid();
    private readonly string _hostVersion;
    private Guid _epoch = Guid.NewGuid();
    private SavedMacroCatalogLoadAttempt? _load;
    private long _revision = 1;
    private MacroCatalogState _state = MacroCatalogState.Starting;
    private int _reading, _reservedNew;

    /// <summary>Ephemeral identity of this host instance; contains no document authority.</summary>
    public Guid HostSessionId => _hostSession;

    public SavedMacroCatalog(string hostVersion)
    {
        if (!CatalogText.Valid(hostVersion, MacroCatalogLimits.MaximumHostVersionLength, allowEmpty: false))
            throw new ArgumentException("A bounded host version is required.", nameof(hostVersion));
        _hostVersion = hostVersion;
    }

    public SavedMacroCatalogLoadAttempt BeginLoad()
    {
        lock (_sync)
        {
            ResetLocked(); _load = new(_hostSession, _epoch); return _load;
        }
    }

    public void Reset() { lock (_sync) ResetLocked(); }
    private void ResetLocked()
    {
        _epoch = Guid.NewGuid(); _items.Clear(); _saves.Clear(); _reservedNew = 0; _load = null;
        _state = MacroCatalogState.Starting; AdvanceLocked();
    }

    public bool CompleteLoad(SavedMacroCatalogLoadAttempt attempt, IReadOnlyList<SavedMacroDescriptor> items)
    {
        ArgumentNullException.ThrowIfNull(attempt); ArgumentNullException.ThrowIfNull(items);
        lock (_sync) if (!CurrentLocked(attempt)) return false;
        int count = items.Count;
        var owned = new Dictionary<Guid, SavedMacroDescriptor>(); bool valid = count is >= 0 and <= MacroCatalogLimits.MaximumItems;
        if (valid)
        {
            for (int index = 0; index < count; index++)
            {
                var item = items[index];
                try { item?.Validated(); }
                catch (ArgumentException) { valid = false; break; }
                if (item is null || !owned.TryAdd(item.Id, item)) { valid = false; break; }
            }
        }
        lock (_sync)
        {
            if (!CurrentLocked(attempt)) return false;
            if (!valid) { UnavailableLocked(); return false; }
            _items.Clear(); foreach (var item in owned) _items.Add(item.Key, item.Value);
            _load = null; _state = MacroCatalogState.Ready; return AdvanceLocked();
        }
    }

    public bool FailLoad(SavedMacroCatalogLoadAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (_sync) { if (!CurrentLocked(attempt)) return false; UnavailableLocked(); return true; }
    }

    public SavedMacroSaveAttempt? BeginSave(Guid macroId)
    {
        if (macroId == Guid.Empty) throw new ArgumentException("A saved macro ID is required.", nameof(macroId));
        lock (_sync)
        {
            if (_state != MacroCatalogState.Ready) return null;
            if (!_items.ContainsKey(macroId) && !_saves.ContainsKey(macroId))
            {
                if (_items.Count + _reservedNew >= MacroCatalogLimits.MaximumItems) { UnavailableLocked(); return null; }
                _reservedNew++;
            }
            var attempt = new SavedMacroSaveAttempt(_hostSession, _epoch, macroId); _saves[macroId] = attempt; return attempt;
        }
    }

    public bool PublishSaved(SavedMacroSaveAttempt attempt, SavedMacroDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(attempt); ArgumentNullException.ThrowIfNull(descriptor);
        lock (_sync)
        {
            if (!CurrentLocked(attempt)) return false;
            bool valid = descriptor.Id == attempt.MacroId && descriptor.SavedRevision == attempt.SavedRevision;
            try { descriptor.Validated(); } catch (ArgumentException) { valid = false; }
            if (!valid) { UnavailableLocked(); return false; }
            _saves.Remove(attempt.MacroId); if (!_items.ContainsKey(descriptor.Id)) _reservedNew--;
            _items[descriptor.Id] = descriptor;
            return AdvanceLocked();
        }
    }

    /// <summary>A failed document save keeps the previously published saved descriptor and cursor unchanged.</summary>
    public bool FailSave(SavedMacroSaveAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (_sync)
        {
            if (!CurrentLocked(attempt)) return false;
            _saves.Remove(attempt.MacroId); if (!_items.ContainsKey(attempt.MacroId)) _reservedNew--;
            return true;
        }
    }

    public bool Remove(Guid macroId)
    {
        if (macroId == Guid.Empty) throw new ArgumentException("A saved macro ID is required.", nameof(macroId));
        lock (_sync)
        {
            bool removed = _items.Remove(macroId); bool pending = _saves.Remove(macroId);
            if (pending && !removed) _reservedNew--;
            removed = pending || removed;
            if (removed) AdvanceLocked(); return removed;
        }
    }

    public Task<MacroCatalogPage> GetPageAsync(MacroCatalogQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query); query.Validated(); ct.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
        { lock (_sync) return Task.FromResult(StatusLocked(MacroCatalogState.Busy)); }
        // Explicit requests use one owned pool task, never a UI callback or a polling worker. Its caller owns the task.
        return Task.Run(() => { try { return ReadPage(query, ct); } finally { Volatile.Write(ref _reading, 0); } }, CancellationToken.None);
    }

    private MacroCatalogPage ReadPage(MacroCatalogQuery query, CancellationToken ct)
    {
        SavedMacroDescriptor[] snapshot; long revision;
        lock (_sync)
        {
            ct.ThrowIfCancellationRequested();
            if (query.HostSessionId is { } host && host != _hostSession || query.CatalogRevision is { } requested && requested != _revision)
                return StatusLocked(MacroCatalogState.Changed);
            if (_state != MacroCatalogState.Ready) return StatusLocked(_state);
            if (query.Offset > _items.Count) return StatusLocked(MacroCatalogState.Changed);
            revision = _revision; snapshot = _items.Values.ToArray();
        }
        ct.ThrowIfCancellationRequested();
        Array.Sort(snapshot, static (left, right) =>
        {
            int result = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            if (result == 0) result = StringComparer.Ordinal.Compare(left.Name, right.Name);
            return result != 0 ? result : left.Id.CompareTo(right.Id);
        });
        ct.ThrowIfCancellationRequested();
        int count = Math.Min(query.Limit, snapshot.Length - query.Offset); var page = new SavedMacroDescriptor[count];
        Array.Copy(snapshot, query.Offset, page, 0, count);
        lock (_sync)
        {
            ct.ThrowIfCancellationRequested();
            if (_revision != revision || _state != MacroCatalogState.Ready) return StatusLocked(MacroCatalogState.Changed);
            int? next = query.Offset + count < snapshot.Length ? query.Offset + count : null;
            return new(MacroCatalogState.Ready, _hostSession, revision, _hostVersion, Array.AsReadOnly(page), snapshot.Length, next,
                MacroCatalogMessages.For(MacroCatalogState.Ready));
        }
    }

    private bool CurrentLocked(SavedMacroCatalogLoadAttempt attempt) => attempt.Owner == _hostSession && attempt.Epoch == _epoch && ReferenceEquals(_load, attempt);
    private bool CurrentLocked(SavedMacroSaveAttempt attempt) => _state == MacroCatalogState.Ready && attempt.Owner == _hostSession && attempt.Epoch == _epoch
        && _saves.TryGetValue(attempt.MacroId, out var current) && ReferenceEquals(current, attempt);
    private MacroCatalogPage StatusLocked(MacroCatalogState state) => new(state, _hostSession, _revision, _hostVersion,
        Array.Empty<SavedMacroDescriptor>(), 0, null, MacroCatalogMessages.For(state));
    private void UnavailableLocked()
    { _state = MacroCatalogState.Unavailable; _epoch = Guid.NewGuid(); _saves.Clear(); _reservedNew = 0; _load = null; AdvanceLocked(); }
    private bool AdvanceLocked()
    {
        if (_revision == long.MaxValue) { _state = MacroCatalogState.Unavailable; _saves.Clear(); _reservedNew = 0; _load = null; return false; }
        _revision++; return true;
    }
}
