namespace MacroSuite.SDK.Contracts;

/// <summary>
/// Scoped variable store accessible to plugins during macro execution.
/// </summary>
public interface IPluginVariables
{
    /// <summary>Attempts to retrieve a variable with type conversion.</summary>
    bool TryGet<T>(string key, out T? value);

    /// <summary>Stores or overwrites a variable value.</summary>
    void Set<T>(string key, T value);

    /// <summary>Checks whether a variable exists.</summary>
    bool Contains(string key);

    /// <summary>Removes a variable by key.</summary>
    bool Remove(string key);
}
