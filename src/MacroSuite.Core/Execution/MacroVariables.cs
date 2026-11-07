using System.Collections.Concurrent;

namespace MacroSuite.Core.Execution;

/// <summary>
/// Thread-safe variables dictionary scoped to a macro execution run.
/// Allows steps and rules to pass state dynamically.
/// </summary>
public sealed class MacroVariables
{
    private readonly ConcurrentDictionary<string, object?> _variables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sets or replaces a variable value.</summary>
    public void Set<T>(string name, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _variables[name] = value;
    }

    /// <summary>Attempts to retrieve a variable cast to type <typeparamref name="T"/>.</summary>
    public bool TryGet<T>(string name, out T? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_variables.TryGetValue(name, out var raw) && raw is not null)
        {
            if (raw is T typedVal)
            {
                value = typedVal;
                return true;
            }

            try
            {
                value = (T)Convert.ChangeType(raw, typeof(T));
                return true;
            }
            catch
            {
                value = default;
                return false;
            }
        }

        value = default;
        return false;
    }

    /// <summary>Gets a variable value, or returns <paramref name="defaultValue"/> if not found or incompatible.</summary>
    public T Get<T>(string name, T defaultValue = default!)
    {
        return TryGet<T>(name, out var val) && val is not null ? val : defaultValue;
    }

    /// <summary>Checks whether a variable exists in the dictionary.</summary>
    public bool Contains(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _variables.ContainsKey(name);
    }

    /// <summary>Removes a variable by name.</summary>
    public bool Remove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _variables.TryRemove(name, out _);
    }

    /// <summary>Clears all stored variables.</summary>
    public void Clear()
    {
        _variables.Clear();
    }

    /// <summary>Returns an immutable snapshot of all current variable values.</summary>
    public IReadOnlyDictionary<string, object?> ToSnapshot()
    {
        return new Dictionary<string, object?>(_variables, StringComparer.OrdinalIgnoreCase);
    }
}
