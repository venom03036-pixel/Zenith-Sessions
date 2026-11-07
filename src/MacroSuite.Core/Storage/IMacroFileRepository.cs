using MacroSuite.Core.Models;

namespace MacroSuite.Core.Storage;

/// <summary>
/// Defines the contract for reading and writing macro files to persistent storage.
/// </summary>
/// <remarks>
/// Implementations are responsible for file-format selection, atomic writes,
/// and concurrency control. The engine and UI layers depend only on this interface
/// (Rule 3 — platform isolation, Rule 4 — single input pipeline analogy applied to storage).
/// </remarks>
public interface IMacroFileRepository
{
    /// <summary>
    /// Saves a single macro asynchronously using an atomic write
    /// (write to temp file, then rename to final path).
    /// </summary>
    /// <param name="model">The macro to persist. Must not be null.</param>
    /// <param name="ct">Cancellation token (Rule 12).</param>
    Task SaveAsync(MacroModel model, CancellationToken ct = default);

    /// <summary>
    /// Saves a collection of macros concurrently with a bounded concurrency limit
    /// so that bulk creation of hundreds of macros does not saturate I/O or freeze the UI.
    /// </summary>
    /// <param name="models">The macros to persist. Must not be null.</param>
    /// <param name="concurrencyLimit">
    /// Maximum number of simultaneous file-write operations. Defaults to <c>4</c>.
    /// </param>
    /// <param name="ct">Cancellation token (Rule 12).</param>
    Task SaveBatchAsync(
        IReadOnlyList<MacroModel> models,
        int concurrencyLimit = 4,
        CancellationToken ct = default);

    /// <summary>
    /// Synchronously loads a macro by its unique identifier.
    /// Prefers the <c>.zmacro</c> binary format; falls back to legacy <c>.json</c>.
    /// </summary>
    /// <param name="macroId">The macro's <see cref="MacroModel.Id"/> as a string.</param>
    /// <returns>The deserialized <see cref="MacroModel"/>.</returns>
    /// <exception cref="FileNotFoundException">Thrown if no file exists for the given ID.</exception>
    MacroModel Load(string macroId);

    /// <summary>
    /// Loads all macros from the macros directory.
    /// Files that fail to parse are skipped with a logged warning (not thrown).
    /// </summary>
    IReadOnlyList<MacroModel> LoadAll();
}
