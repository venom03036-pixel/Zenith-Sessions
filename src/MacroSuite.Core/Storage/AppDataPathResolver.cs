namespace MacroSuite.Core.Storage;

/// <summary>
/// Resolves standard application data directory paths under <c>%APPDATA%\MacroSuite\</c>.
/// Supports custom root injection for testing and portable execution modes.
/// </summary>
public sealed class AppDataPathResolver
{
    /// <summary>Default singleton instance resolving against user's %APPDATA%.</summary>
    public static AppDataPathResolver Default { get; } = new();

    /// <summary>Root directory for all application data.</summary>
    public string RootDirectory { get; }

    /// <summary>Directory where .json macro definition files are stored.</summary>
    public string MacrosDirectory => Path.Combine(RootDirectory, "Macros");

    /// <summary>Directory where image template files (.png, .jpg) are stored.</summary>
    public string TemplatesDirectory => Path.Combine(RootDirectory, "Templates");

    /// <summary>Directory where application configuration is stored.</summary>
    public string ConfigDirectory => Path.Combine(RootDirectory, "Config");

    /// <summary>Directory where license files are stored.</summary>
    public string LicenseDirectory => Path.Combine(RootDirectory, "License");

    /// <summary>Directory where DPAPI-encrypted credentials are stored.</summary>
    public string SecurityDirectory => Path.Combine(RootDirectory, "Security");

    /// <summary>Directory where audit database and thumbnails are stored.</summary>
    public string AuditDirectory => Path.Combine(RootDirectory, "Audit");

    /// <summary>Directory where usage database is stored.</summary>
    public string UsageDirectory => Path.Combine(RootDirectory, "Usage");

    /// <summary>Static convenience helper to get default Usage directory.</summary>
    public static string GetUsageDirectory() => Default.UsageDirectory;

    /// <summary>Static convenience helper to get default License directory.</summary>
    public static string GetLicenseDirectory() => Default.LicenseDirectory;

    /// <summary>Directory where third-party plugins reside.</summary>
    public string PluginsDirectory => Path.Combine(RootDirectory, "Plugins");

    /// <summary>Directory for transient caches.</summary>
    public string CacheDirectory => Path.Combine(RootDirectory, "Cache");

    /// <summary>Directory for application logs.</summary>
    public string LogsDirectory => Path.Combine(RootDirectory, "Logs");

    /// <summary>
    /// Constructs a path resolver with an optional custom root path.
    /// </summary>
    public AppDataPathResolver(string? rootDirectory = null)
    {
        RootDirectory = !string.IsNullOrWhiteSpace(rootDirectory)
            ? rootDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroSuite");
    }

    /// <summary>
    /// Translates a relative template path (e.g. "Templates\button.png" or "button.png")
    /// into an absolute filesystem path.
    /// </summary>
    public string ResolveTemplatePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathRooted(relativePath))
        {
            return relativePath;
        }

        return Path.Combine(TemplatesDirectory, relativePath.TrimStart('\\', '/'));
    }

    /// <summary>
    /// Translates an absolute template filesystem path to a relative path normalized to <see cref="TemplatesDirectory"/>.
    /// </summary>
    public string ToRelativeTemplatePath(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        if (!Path.IsPathRooted(absolutePath))
        {
            return absolutePath;
        }

        return Path.GetRelativePath(TemplatesDirectory, absolutePath);
    }

    /// <summary>
    /// Resolves the absolute <em>read</em> path for a macro by ID or file name.
    /// Prefers the <c>.zmacro</c> binary format; falls back to the legacy <c>.json</c> file
    /// if the binary file does not exist.
    /// </summary>
    public string ResolveMacroPath(string macroId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macroId);

        // Strip any extension the caller may have included
        string stem = Path.GetFileNameWithoutExtension(macroId);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = macroId;
        }

        string binaryPath = Path.Combine(MacrosDirectory, stem + MacroSchema.BinaryExtension);
        if (File.Exists(binaryPath))
        {
            return binaryPath;
        }

        return Path.Combine(MacrosDirectory, stem + MacroSchema.LegacyJsonExtension);
    }

    /// <summary>
    /// Resolves the absolute <em>write</em> path for a macro by ID.
    /// Always returns the <c>.zmacro</c> path — new saves always use the binary format.
    /// </summary>
    public string ResolveMacroWritePath(string macroId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macroId);

        string stem = Path.GetFileNameWithoutExtension(macroId);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = macroId;
        }

        return Path.Combine(MacrosDirectory, stem + MacroSchema.BinaryExtension);
    }
}
