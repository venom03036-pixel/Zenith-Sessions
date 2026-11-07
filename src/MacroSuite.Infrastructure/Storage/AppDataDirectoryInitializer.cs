namespace MacroSuite.Infrastructure.Storage;

/// <summary>
/// Creates the canonical directory tree under <c>%APPDATA%\MacroSuite\</c> at application
/// startup. All directories are created idempotently (no error if they already exist).
/// </summary>
/// <remarks>
/// Call <see cref="Initialize"/> once during application startup, before any subsystem
/// attempts to read or write files. This ensures all paths are valid before use.
/// </remarks>
public static class AppDataDirectoryInitializer
{
    /// <summary>
    /// The root directory for all MacroSuite user data.
    /// Example: <c>C:\Users\&lt;user&gt;\AppData\Roaming\MacroSuite</c>
    /// </summary>
    public static string RootPath { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MacroSuite");

    // All subdirectory names (relative to RootPath)
    private static readonly string[] s_subdirectories =
    [
        "Macros",
        "Templates",
        "Config",
        "Security",
        "Audit",
        Path.Combine("Audit", "screenshots"),
        "Usage",
        "Plugins",
        "Cache",
        "Logs",
    ];

    /// <summary>
    /// Creates the MacroSuite data directory and all required subdirectories.
    /// Safe to call multiple times.
    /// </summary>
    /// <exception cref="IOException">
    /// Thrown if directory creation fails due to a permissions or I/O error.
    /// The exception is not swallowed — the caller must decide whether to abort or warn.
    /// </exception>
    public static void Initialize()
    {
        Directory.CreateDirectory(RootPath);

        foreach (var sub in s_subdirectories)
        {
            Directory.CreateDirectory(Path.Combine(RootPath, sub));
        }
    }

    /// <summary>
    /// Returns the canonical absolute path to a named subdirectory under
    /// <see cref="RootPath"/>. Does not guarantee the directory exists; call
    /// <see cref="Initialize"/> first.
    /// </summary>
    public static string GetPath(string subdirectory) =>
        Path.Combine(RootPath, subdirectory);

    /// <summary>
    /// Returns the canonical absolute path for a file under a named subdirectory.
    /// Does not guarantee the directory or file exists; call <see cref="Initialize"/> first.
    /// </summary>
    public static string GetFilePath(string subdirectory, string fileName) =>
        Path.Combine(RootPath, subdirectory, fileName);
}
