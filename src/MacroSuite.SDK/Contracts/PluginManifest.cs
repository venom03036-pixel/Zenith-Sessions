namespace MacroSuite.SDK.Contracts;

/// <summary>
/// Deserializable schema representing a plugin's <c>plugin.json</c> declaration file.
/// Validated before assembly loading to enforce security and version compatibility.
/// </summary>
public sealed class PluginManifest
{
    /// <summary>Unique plugin identifier matching the descriptor.</summary>
    public string PluginId { get; init; } = string.Empty;

    /// <summary>User-visible plugin name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Brief summary of functionality.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Plugin author or organization name.</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>Plugin version string (SemVer, e.g. '1.2.0').</summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>Minimum host engine version required (e.g. '1.0.0').</summary>
    public string MinHostVersion { get; init; } = "1.0.0";

    /// <summary>Name of the primary .dll assembly file in the plugin folder.</summary>
    public string EntryAssembly { get; init; } = string.Empty;

    /// <summary>Fully-qualified class name implementing <see cref="IPluginDescriptor"/>.</summary>
    public string EntryType { get; init; } = string.Empty;

    /// <summary>List of required engine feature IDs.</summary>
    public List<string> RequiredFeatures { get; init; } = new();

    /// <summary>
    /// Validates the basic structural integrity of the manifest fields.
    /// </summary>
    public ValidationResult Validate()
    {
        if (string.IsNullOrWhiteSpace(PluginId))
        {
            return ValidationResult.Failure("Manifest is missing required 'pluginId'.");
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            return ValidationResult.Failure("Manifest is missing required 'displayName'.");
        }

        if (string.IsNullOrWhiteSpace(EntryAssembly))
        {
            return ValidationResult.Failure("Manifest is missing required 'entryAssembly'.");
        }

        if (string.IsNullOrWhiteSpace(EntryType))
        {
            return ValidationResult.Failure("Manifest is missing required 'entryType'.");
        }

        if (!System.Version.TryParse(Version, out _))
        {
            return ValidationResult.Failure($"Manifest contains invalid version '{Version}'.");
        }

        if (!System.Version.TryParse(MinHostVersion, out _))
        {
            return ValidationResult.Failure($"Manifest contains invalid minHostVersion '{MinHostVersion}'.");
        }

        return ValidationResult.Success();
    }
}
