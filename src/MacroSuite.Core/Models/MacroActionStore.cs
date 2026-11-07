using MacroSuite.Core.Steps;

namespace MacroSuite.Core.Models;

/// <summary>
/// Authoritative in-memory macro actions. Playback and persistence read from here.
/// The UI must not keep a parallel copy of every action as a ViewModel.
/// </summary>
public sealed class MacroActionStore
{
    private readonly List<MacroStep> _items = new(256);

    public int Count => _items.Count;

    public MacroStep this[int index] => _items[index];

    public IReadOnlyList<MacroStep> Items => _items;

    public void Add(MacroStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        _items.Add(step);
    }

    public void AddRange(IEnumerable<MacroStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _items.AddRange(steps);
    }

    public void Insert(int index, MacroStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        _items.Insert(index, step);
    }

    public void RemoveAt(int index) => _items.RemoveAt(index);

    public bool Remove(MacroStep step) => _items.Remove(step);

    public void Clear() => _items.Clear();

    public void ReplaceAt(int index, MacroStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        _items[index] = step;
    }

    public void Move(int from, int to)
    {
        if (from == to) return;
        var item = _items[from];
        _items.RemoveAt(from);
        _items.Insert(Math.Clamp(to, 0, _items.Count), item);
    }

    public void ReplaceAll(IEnumerable<MacroStep> steps)
    {
        _items.Clear();
        _items.AddRange(steps);
    }

    public int IndexOf(MacroStep step) => _items.IndexOf(step);

    public List<MacroStep> ToList() => new(_items);

    /// <summary>Rough working-set hint for warnings (not a measurement).</summary>
    public static long EstimateBytes(int actionCount) => actionCount * 96L;
}

/// <summary>Warns before unusually large macros; does not block recording.</summary>
public static class MacroSizePolicy
{
    public const int WarnActions = 50_000;
    public const int WarnActionsHigh = 100_000;
    public const int WarnActionsExtreme = 500_000;

    public static string? WarningForCount(int count)
    {
        if (count >= WarnActionsExtreme)
            return $"This macro has {count:N0} actions. Estimated in-memory size ~{MacroActionStore.EstimateBytes(count) / (1024 * 1024)} MB of action data (UI remains virtualized).";
        if (count >= WarnActionsHigh)
            return $"This macro has {count:N0} actions. The editor shows only visible rows; save/load may take longer.";
        if (count >= WarnActions)
            return $"This macro has {count:N0} actions. Consider splitting if recording continues.";
        return null;
    }
}
