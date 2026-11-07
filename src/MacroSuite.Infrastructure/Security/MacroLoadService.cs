using MacroSuite.Core.Models;
using MacroSuite.Core.Storage;
using MacroSuite.Infrastructure.Storage;

namespace MacroSuite.Infrastructure.Security;

/// <summary>
/// Orchestrates the full macro load pipeline:
/// read file → compute CRC32 → conditional validation → deserialize.
/// </summary>
/// <remarks>
/// <para>
/// The validation check runs only when:
/// <list type="bullet">
///   <item>No cache entry exists for the macro ID, or</item>
///   <item>The cached CRC32 hash does not match the current file bytes (file changed).</item>
/// </list>
/// On every load, only the cheap CRC32 computation is performed. The full 7-check
/// validation scan runs only on cache misses (Rule 7).
/// </para>
/// <para>
/// This class does <em>not</em> decide whether to block execution based on the approval
/// status; that responsibility belongs to <c>MacroApprovalService</c> in the App layer
/// (Rule 1 — UI must not perform automation work directly).
/// </para>
/// </remarks>
public sealed class MacroLoadService
{
    private readonly MacroFileValidator _validator;
    private readonly MacroValidationCache _cache;
    private readonly MacroJsonSerializer _serializer;

    public MacroLoadService(
        MacroFileValidator validator,
        MacroValidationCache cache,
        MacroJsonSerializer? serializer = null)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(cache);
        _validator = validator;
        _cache = cache;
        _serializer = serializer ?? new MacroJsonSerializer();
    }

    /// <summary>
    /// Loads a macro from <paramref name="filePath"/>, running validation only if needed.
    /// </summary>
    /// <param name="filePath">Absolute path to a <c>.zmacro</c> or legacy <c>.json</c> file.</param>
    /// <param name="macroId">
    /// The macro's ID string (used as the cache key). Pass <c>null</c> to derive it from the file name.
    /// </param>
    /// <param name="enforceOnViolation">
    /// When <c>true</c> (default), throws <see cref="MacroSecurityException"/> if any blocking violations
    /// are found. Set to <c>false</c> to load the model even with violations (e.g. for display/review purposes).
    /// </param>
    /// <param name="ct">Cancellation token (Rule 12).</param>
    /// <returns>The loaded <see cref="MacroModel"/> and the validation result.</returns>
    /// <exception cref="MacroSecurityException">
    /// Thrown when validation finds blocking violations and <paramref name="enforceOnViolation"/> is <c>true</c>.
    /// </exception>
    public async Task<(MacroModel Model, MacroValidationResult ValidationResult)> LoadAsync(
        string filePath,
        string? macroId = null,
        bool enforceOnViolation = true,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Macro file not found: '{filePath}'", filePath);
        }

        string cacheKey = macroId ?? Path.GetFileNameWithoutExtension(filePath);

        // 1. Read raw bytes (needed for CRC32 and for deserialisation)
        byte[] rawBytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);

        // 2. Cheap CRC32 of raw file bytes — determines if the cache is still valid
        uint fileHash = Crc32Helper.Compute(rawBytes);

        // 3. Check cache — O(1) dictionary lookup
        MacroValidationResult validationResult;
        if (_cache.TryGetResult(cacheKey, fileHash, out var cachedEntry) && cachedEntry != null)
        {
            // Cache hit — reuse stored result, skip full validation
            validationResult = new MacroValidationResult(cachedEntry.IsValid, cachedEntry.Violations);
        }
        else
        {
            // Cache miss — extract JSON and run full validation
            string json = ExtractJson(rawBytes, filePath);
            validationResult = _validator.Validate(json, Path.GetFileName(filePath));

            // Store result asynchronously (fire-and-forget with tracked exception)
            _ = _cache.SetResultAsync(cacheKey, fileHash, validationResult.IsValid,
                                      validationResult.Violations, ct)
                      .ContinueWith(t => System.Diagnostics.Debug.WriteLine(
                          $"[MacroLoadService] Cache write failed: {t.Exception?.Message}"),
                          TaskContinuationOptions.OnlyOnFaulted);
        }

        // 4. Enforce: block load if there are blocking violations
        if (enforceOnViolation && !validationResult.IsValid)
        {
            throw new MacroSecurityException(
                $"Macro '{cacheKey}' failed security validation and cannot be loaded. " +
                "See ValidationResult.Violations for details.",
                validationResult);
        }

        // 5. Deserialise
        string jsonForDeserialisation = ExtractJson(rawBytes, filePath);
        MacroModel model = _serializer.Deserialize(jsonForDeserialisation);

        return (model, validationResult);
    }

    /// <summary>
    /// Returns the CRC32 of the raw file bytes — a cheap hash for change detection.
    /// Does not parse or validate the file.
    /// </summary>
    public static uint ComputeFileHash(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        byte[] bytes = File.ReadAllBytes(filePath);
        return Crc32Helper.Compute(bytes);
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private static string ExtractJson(byte[] rawBytes, string filePath)
    {
        using var ms = new MemoryStream(rawBytes);
        return MacroFileContainer.HasMagicHeader(ms)
            ? MacroFileContainer.ReadJson(ms)
            : MacroFileContainer.ReadLegacyJson(ms);
    }
}

/// <summary>
/// Thrown when a macro file fails security validation and loading is blocked.
/// </summary>
public sealed class MacroSecurityException : Exception
{
    /// <summary>The full validation result, including all violation messages.</summary>
    public MacroValidationResult ValidationResult { get; }

    public MacroSecurityException(string message, MacroValidationResult result)
        : base(message)
    {
        ValidationResult = result;
    }
}
