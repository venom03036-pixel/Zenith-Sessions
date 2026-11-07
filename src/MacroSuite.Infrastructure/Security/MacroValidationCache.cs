using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacroSuite.Infrastructure.Security;

/// <summary>
/// A single persisted validation cache entry for one macro file.
/// </summary>
public sealed class MacroValidationCacheEntry
{
    /// <summary>The macro's unique identifier (Guid as string).</summary>
    public string MacroId { get; set; } = string.Empty;

    /// <summary>CRC32 of the raw file bytes at the time of validation (hex string).</summary>
    public string FileHash { get; set; } = string.Empty;

    /// <summary><c>true</c> if validation passed with no blocking violations.</summary>
    public bool IsValid { get; set; }

    /// <summary>Human-readable violation messages from the last validation pass.</summary>
    public List<string> Violations { get; set; } = new();

    /// <summary>UTC timestamp of the last validation run.</summary>
    public DateTimeOffset ValidatedAt { get; set; }

    /// <summary>
    /// UTC timestamp when the user explicitly approved this macro for execution.
    /// <c>null</c> means not yet approved.
    /// </summary>
    public DateTimeOffset? ApprovedAt { get; set; }

    /// <summary>
    /// Hash of the file bytes at the time the user approved the macro.
    /// Used to detect whether the file changed after approval.
    /// </summary>
    public string? ApprovedFileHash { get; set; }
}

/// <summary>
/// Persists and retrieves <see cref="MacroValidationCacheEntry"/> records so that
/// macro security validation only needs to run when a file has changed.
/// </summary>
/// <remarks>
/// <para>
/// The cache is stored as a single JSON file at
/// <c>%APPDATA%\MacroSuite\Security\validation_cache.json</c>.
/// All writes are atomic (write to <c>.tmp</c>, then rename). Thread-safety is
/// guaranteed by a <see cref="SemaphoreSlim"/> (Rule 12).
/// </para>
/// <para>
/// Hash matching uses CRC32 of raw file bytes — cheap and good enough for
/// change detection (not a cryptographic guarantee). Per Rule 7, the hash check
/// must be allocation-minimal on the hot path.
/// </para>
/// </remarks>
public sealed class MacroValidationCache : IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _cachePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, MacroValidationCacheEntry> _entries = new(StringComparer.Ordinal);
    private bool _dirty;

    public MacroValidationCache(string cachePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        _cachePath = cachePath;
    }

    /// <summary>
    /// Loads (or creates) the cache from disk. Must be called once before use.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_cachePath))
            {
                _entries = new Dictionary<string, MacroValidationCacheEntry>(StringComparer.Ordinal);
                return;
            }

            string json = await File.ReadAllTextAsync(_cachePath, ct).ConfigureAwait(false);
            var list = JsonSerializer.Deserialize<List<MacroValidationCacheEntry>>(json, s_jsonOptions)
                       ?? new List<MacroValidationCacheEntry>();

            _entries = new Dictionary<string, MacroValidationCacheEntry>(list.Count, StringComparer.Ordinal);
            foreach (var entry in list)
            {
                if (!string.IsNullOrEmpty(entry.MacroId))
                {
                    _entries[entry.MacroId] = entry;
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Checks whether a valid, up-to-date cache entry exists for <paramref name="macroId"/>
    /// with the given <paramref name="currentFileHash"/>.
    /// </summary>
    /// <returns>
    /// <c>true</c> and populates <paramref name="entry"/> if the cached hash matches.
    /// <c>false</c> if no entry exists or the hash differs (file changed since last validation).
    /// </returns>
    public bool TryGetResult(
        string macroId,
        uint currentFileHash,
        out MacroValidationCacheEntry? entry)
    {
        string hashHex = currentFileHash.ToString("X8");

        _lock.Wait();
        try
        {
            if (_entries.TryGetValue(macroId, out var cached) &&
                cached.FileHash.Equals(hashHex, StringComparison.OrdinalIgnoreCase))
            {
                entry = cached;
                return true;
            }
        }
        finally
        {
            _lock.Release();
        }

        entry = null;
        return false;
    }

    /// <summary>Stores or updates a validation result in the cache and schedules a flush.</summary>
    public async Task SetResultAsync(
        string macroId,
        uint fileHash,
        bool isValid,
        IReadOnlyList<string> violations,
        CancellationToken ct = default)
    {
        string hashHex = fileHash.ToString("X8");

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_entries.TryGetValue(macroId, out var existing))
            {
                // Preserve approval data if hash is unchanged
                existing.FileHash = hashHex;
                existing.IsValid = isValid;
                existing.Violations = new List<string>(violations);
                existing.ValidatedAt = DateTimeOffset.UtcNow;

                // Invalidate prior approval if the file hash changed
                if (existing.ApprovedFileHash != null &&
                    !existing.ApprovedFileHash.Equals(hashHex, StringComparison.OrdinalIgnoreCase))
                {
                    existing.ApprovedAt = null;
                    existing.ApprovedFileHash = null;
                }
            }
            else
            {
                _entries[macroId] = new MacroValidationCacheEntry
                {
                    MacroId = macroId,
                    FileHash = hashHex,
                    IsValid = isValid,
                    Violations = new List<string>(violations),
                    ValidatedAt = DateTimeOffset.UtcNow,
                };
            }

            _dirty = true;
        }
        finally
        {
            _lock.Release();
        }

        await FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Records that the user approved a macro for execution.</summary>
    public async Task RecordApprovalAsync(string macroId, uint fileHash, CancellationToken ct = default)
    {
        string hashHex = fileHash.ToString("X8");

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_entries.TryGetValue(macroId, out var entry))
            {
                entry.ApprovedAt = DateTimeOffset.UtcNow;
                entry.ApprovedFileHash = hashHex;
                _dirty = true;
            }
        }
        finally
        {
            _lock.Release();
        }

        if (_dirty)
        {
            await FlushAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns <c>true</c> if the user has previously approved the macro and
    /// the file has not changed since that approval.
    /// </summary>
    public bool IsApproved(string macroId, uint currentFileHash)
    {
        string hashHex = currentFileHash.ToString("X8");

        _lock.Wait();
        try
        {
            return _entries.TryGetValue(macroId, out var entry) &&
                   entry.ApprovedAt.HasValue &&
                   entry.ApprovedFileHash != null &&
                   entry.ApprovedFileHash.Equals(hashHex, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private async Task FlushAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_dirty) return;

            string? dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tmpPath = _cachePath + ".tmp";
            var list = new List<MacroValidationCacheEntry>(_entries.Values);
            string json = JsonSerializer.Serialize(list, s_jsonOptions);

            await File.WriteAllTextAsync(tmpPath, json, ct).ConfigureAwait(false);
            File.Move(tmpPath, _cachePath, overwrite: true);

            _dirty = false;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_dirty)
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        _lock.Dispose();
    }
}
