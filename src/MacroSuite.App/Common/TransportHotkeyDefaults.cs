namespace MacroSuite.App.Common;

/// <summary>
/// The one place that names the shipped default shortcuts. Settings first run, settings repair,
/// the transport reset, the editor's mirrored binds and the auto clicker all read these values,
/// and a test keeps Defaults\settings.json in step with them.
/// </summary>
internal static class TransportHotkeyDefaults
{
    public const string Record = "F6";
    public const string Stop = "F8";
    public const string Play = "F4";
    public const string Pause = "F5";
    public const string Panic = "F12";
    public const string AutoClicker = "F1";
    public const string MiniClickThrough = "Ctrl+Alt+F10";

    /// <summary>
    /// Saved as <c>shortcutDefaultsVersion</c> to record which generation of defaults wrote the
    /// settings file. Nothing branches on it yet. A file without the key loads as 0, which marks
    /// it as written before this generation for any future migration, so the key stays in the
    /// saved format even though no code reads it to decide anything today.
    /// </summary>
    public const int Version = 2;
}
