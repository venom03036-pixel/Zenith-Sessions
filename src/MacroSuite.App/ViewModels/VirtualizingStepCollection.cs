using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;

namespace MacroSuite.App.ViewModels;

/// <summary>
/// UI facade over <see cref="MacroActionStore"/>. Only the indices WPF asks for
/// become <see cref="MacroStepItemViewModel"/> instances (LRU cap).
/// </summary>
public sealed class VirtualizingStepCollection :
    IList<MacroStepItemViewModel>,
    IList,
    INotifyCollectionChanged,
    INotifyPropertyChanged
{
    public const int DefaultCacheCap = 192;

    private readonly MacroActionStore _store;
    private readonly Dictionary<int, MacroStepItemViewModel> _cache = new();
    private readonly LinkedList<int> _lru = new();
    private readonly int _cacheCap;
    private int _pinIndex = -1;

    public VirtualizingStepCollection(MacroActionStore store, int cacheCap = DefaultCacheCap)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _cacheCap = Math.Max(32, cacheCap);
    }

    public MacroActionStore Store => _store;

    public int CachedViewModelCount => _cache.Count;
    internal IEnumerable<MacroStepItemViewModel> CachedItems => _cache.Values;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public int Count => _store.Count;
    public bool IsReadOnly => false;
    public bool IsFixedSize => false;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    public MacroStepItemViewModel this[int index]
    {
        get => GetOrCreate(index);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            var list = _store.ToList();
            list[index] = value.Step;
            _store.ReplaceAll(list);
            InvalidateCache();
            NotifyReset();
        }
    }

    object? IList.this[int index]
    {
        get => this[index];
        set => this[index] = (MacroStepItemViewModel)value!;
    }

    public MacroStepItemViewModel GetOrCreate(int index)
    {
        if (_cache.TryGetValue(index, out var existing))
        {
            Touch(index);
            existing.StepIndex = index + 1;
            return existing;
        }

        MacroStep? previous = index > 0 ? _store[index - 1] : null;
        var vm = MacroStepItemViewModel.FromStep(_store[index], index, previous);
        AddToCache(index, vm);
        return vm;
    }

    public void Add(MacroStepItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _store.Add(item.Step);
        int index = _store.Count - 1;
        AddToCache(index, item);
        item.StepIndex = index + 1;
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, index));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
    }

    public void AddStep(MacroStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        _store.Add(step);
        NotifyAppended(1);
    }

    public void AddSteps(IReadOnlyList<MacroStep> steps, bool notify = true)
    {
        if (steps.Count == 0) return;
        _store.AddRange(steps);
        if (notify)
            NotifyAppended(steps.Count);
        else
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
    }

    public void Clear()
    {
        _store.Clear();
        InvalidateCache();
        NotifyReset();
    }

    public bool Contains(MacroStepItemViewModel item) => IndexOf(item) >= 0;

    public void CopyTo(MacroStepItemViewModel[] array, int arrayIndex)
    {
        for (int i = 0; i < Count; i++)
            array[arrayIndex + i] = this[i];
    }

    public bool Remove(MacroStepItemViewModel item)
    {
        int index = IndexOf(item);
        if (index < 0) return false;
        RemoveAt(index);
        return true;
    }

    public void RemoveAt(int index)
    {
        _store.RemoveAt(index);
        InvalidateCache();
        NotifyReset();
    }

    public void Insert(int index, MacroStepItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _store.Insert(index, item.Step);
        InvalidateCache();
        NotifyReset();
    }

    public int IndexOf(MacroStepItemViewModel item)
    {
        if (item is null) return -1;
        int byStep = _store.IndexOf(item.Step);
        if (byStep >= 0) return byStep;

        Guid id = item.Step.StepId;
        if (id != Guid.Empty)
        {
            for (int i = 0; i < _store.Count; i++)
            {
                if (_store[i].StepId == id)
                    return i;
            }
        }

        foreach (var kv in _cache)
        {
            if (ReferenceEquals(kv.Value, item))
                return kv.Key;
        }

        return -1;
    }

    public void ReplaceAt(int index, MacroStep step)
    {
        _store.ReplaceAt(index, step);
        InvalidateCache();
        NotifyReset();
    }

    public void Move(int oldIndex, int newIndex)
    {
        _store.Move(oldIndex, newIndex);
        InvalidateCache();
        NotifyReset();
    }

    public void RefreshFromStore()
    {
        InvalidateCache();
        NotifyReset();
    }

    public void ReplaceAll(IEnumerable<MacroStep> steps)
    {
        _store.ReplaceAll(steps);
        InvalidateCache();
        NotifyReset();
    }

    public void PinIndex(int index) => _pinIndex = index;

    public IEnumerator<MacroStepItemViewModel> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    int IList.Add(object? value)
    {
        Add((MacroStepItemViewModel)value!);
        return Count - 1;
    }

    bool IList.Contains(object? value) => value is MacroStepItemViewModel vm && Contains(vm);

    int IList.IndexOf(object? value) => value is MacroStepItemViewModel vm ? IndexOf(vm) : -1;

    void IList.Insert(int index, object? value) => Insert(index, (MacroStepItemViewModel)value!);

    void IList.Remove(object? value)
    {
        if (value is MacroStepItemViewModel vm)
            Remove(vm);
    }

    void ICollection.CopyTo(Array array, int index)
    {
        for (int i = 0; i < Count; i++)
            array.SetValue(this[i], index + i);
    }

    private void NotifyAppended(int added)
    {
        InvalidateCache();
        // Reset: WPF regenerates only visible containers; avoids N CollectionChanged.Add events.
        NotifyReset();
        _ = added;
    }

    private void NotifyReset()
    {
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
    }

    private void InvalidateCache()
    {
        _cache.Clear();
        _lru.Clear();
    }

    private void AddToCache(int index, MacroStepItemViewModel vm)
    {
        _cache[index] = vm;
        Touch(index);
        TrimCache();
    }

    private void Touch(int index)
    {
        var node = _lru.Find(index);
        if (node != null)
            _lru.Remove(node);
        _lru.AddFirst(index);
    }

    private void TrimCache()
    {
        while (_cache.Count > _cacheCap && _lru.Count > 0)
        {
            int last = _lru.Last!.Value;
            _lru.RemoveLast();
            if (last == _pinIndex) continue;
            _cache.Remove(last);
        }
    }
}
