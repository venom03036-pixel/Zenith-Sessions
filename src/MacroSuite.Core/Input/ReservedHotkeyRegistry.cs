namespace MacroSuite.Core.Input;

/// <summary>
/// Virtual keys reserved by the application (panic, transport, script start, auto-clicker).
/// Recording must never persist these as macro actions.
/// </summary>
public sealed class ReservedHotkeyRegistry
{
    private readonly HashSet<int> _virtualKeys = new();
    private readonly object _gate = new();

    public void Replace(IEnumerable<int> virtualKeys)
    {
        ArgumentNullException.ThrowIfNull(virtualKeys);
        lock (_gate)
        {
            _virtualKeys.Clear();
            foreach (int vk in virtualKeys)
            {
                if (vk > 0)
                    _virtualKeys.Add(vk);
            }
        }
    }

    public bool Contains(int virtualKey)
    {
        lock (_gate)
            return _virtualKeys.Contains(virtualKey);
    }

    public int[] Snapshot()
    {
        lock (_gate)
            return _virtualKeys.ToArray();
    }
}
