using MacroSuite.Core.Storage;

namespace MacroSuite.Core.Models;

/// <summary>
/// Top-level serializable model for a MacroSuite automation macro.
/// Includes schema version, metadata, configuration, and sequential steps.
/// </summary>
public sealed class MacroModel
{
    public RecordingCommitMetadata? RecordingCommit { get; init; }
    /// <summary>Schema version of this macro file format.</summary>
    public int SchemaVersion { get; init; } = MacroSchema.CurrentVersion;

    /// <summary>Unique identifier for this macro.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Human-readable display name of the macro.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Optional user-provided description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Timestamp when the macro was created, used for deterministic creation ordering.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Execution settings and limits.</summary>
    public MacroSettings Settings { get; init; } = new();

    /// <summary>Whether this macro is marked as a favourite by the user.</summary>
    public bool IsFavorite { get; init; } = false;

    /// <summary>Sequential steps comprising this macro.</summary>
    public IReadOnlyList<MacroStep> Steps { get; init; } = Array.Empty<MacroStep>();
}
