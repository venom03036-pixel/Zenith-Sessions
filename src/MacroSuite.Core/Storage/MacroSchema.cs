namespace MacroSuite.Core.Storage;

/// <summary>
/// Defines the schema versioning contract for MacroSuite macro files.
/// </summary>
public static class MacroSchema
{
    // ── Inner JSON payload schema ──────────────────────────────────────────
    /// <summary>
    /// The current macro JSON payload schema version.
    /// Incremented whenever the serialization format undergoes breaking changes.
    /// </summary>
    public const int CurrentVersion = 3;

    // ── Binary container format ────────────────────────────────────────────
    /// <summary>
    /// Magic bytes that identify a <c>.zmacro</c> binary container file.
    /// ASCII: "ZMCR" (Zenith Macro Container Record).
    /// </summary>
    public static ReadOnlySpan<byte> MagicHeader => new byte[] { 0x5A, 0x4D, 0x43, 0x52 };

    /// <summary>
    /// Current binary container version. Bumped only when the container
    /// framing (not the JSON payload) changes.
    /// </summary>
    public const byte ContainerVersion = 1;

    /// <summary>File extension used for binary macro container files.</summary>
    public const string BinaryExtension = ".zmacro";

    /// <summary>File extension used for legacy plain-JSON macro files.</summary>
    public const string LegacyJsonExtension = ".json";
}
