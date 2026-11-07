namespace MacroSuite.App.Common;

/// <summary>
/// Provides compile-time environment flags distinguishing between
/// the private Developer edition and the public Shippable release.
/// </summary>
public static class BuildEnvironment
{
    /// <summary>The displayed version comes from the build's single version declaration.</summary>
    public static string Version { get; } = typeof(BuildEnvironment).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion.Split('+')[0]
        ?? typeof(BuildEnvironment).Assembly.GetName().Version?.ToString(3) ?? "Unknown";
#if DEVELOPER_MODE
    /// <summary>
    /// True if compiled as the internal developer edition with master admin privileges.
    /// </summary>
    public static bool IsDeveloperBuild => true;

    /// <summary>
    /// Human-readable profile name.
    /// </summary>
    public const string BuildProfileName = "Developer Edition (Master Admin)";
#else
    /// <summary>
    /// False in public shippable releases.
    /// </summary>
    public static bool IsDeveloperBuild => false;

    /// <summary>
    /// Human-readable profile name.
    /// </summary>
    public const string BuildProfileName = "Public Release";
#endif
}
