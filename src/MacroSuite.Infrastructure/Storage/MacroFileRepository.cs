using MacroSuite.Core.Models;
using MacroSuite.Core.Storage;

namespace MacroSuite.Infrastructure.Storage;

/// <summary>
/// File-system implementation of <see cref="IMacroFileRepository"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Saves use atomic write-to-tmp-then-rename so a crash mid-save cannot corrupt a file (Rule 13).</item>
///   <item>New saves always produce <c>.zmacro</c> binary containers; legacy <c>.json</c> files are read
///         transparently and migrated to <c>.zmacro</c> on the next save.</item>
///   <item>Bulk saves use a <see cref="SemaphoreSlim"/>-bounded concurrency pattern so that creating
///         hundreds of macros at once does not saturate I/O or freeze the UI (Rule 7).</item>
///   <item>Every background operation receives a <see cref="CancellationToken"/> (Rule 12).</item>
/// </list>
/// </remarks>
public sealed class MacroFileRepository : IMacroFileRepository
{
    private readonly AppDataPathResolver _paths;
    private readonly MacroJsonSerializer _serializer;

    /// <param name="paths">Path resolver; defaults to <see cref="AppDataPathResolver.Default"/>.</param>
    /// <param name="serializer">JSON serializer; a default instance is created if not provided.</param>
    public MacroFileRepository(
        AppDataPathResolver? paths = null,
        MacroJsonSerializer? serializer = null)
    {
        _paths = paths ?? AppDataPathResolver.Default;
        _serializer = serializer ?? new MacroJsonSerializer();
    }

    /// <inheritdoc/>
    public async Task SaveAsync(MacroModel model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ct.ThrowIfCancellationRequested();

        string finalPath = _paths.ResolveMacroWritePath(model.Id.ToString());
        await WriteAtomicAsync(model, finalPath, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SaveBatchAsync(
        IReadOnlyList<MacroModel> models,
        int concurrencyLimit = 4,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (concurrencyLimit <= 0) throw new ArgumentOutOfRangeException(nameof(concurrencyLimit), "Must be > 0.");

        // Bounded concurrency: at most `concurrencyLimit` concurrent file-write tasks.
        using var semaphore = new SemaphoreSlim(concurrencyLimit, concurrencyLimit);
        var tasks = new Task[models.Count];

        for (int i = 0; i < models.Count; i++)
        {
            MacroModel model = models[i]; // capture for closure
            tasks[i] = SaveWithSemaphoreAsync(model, semaphore, ct);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public MacroModel Load(string macroId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macroId);

        string path = _paths.ResolveMacroPath(macroId);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No macro file found for ID '{macroId}'.", path);
        }

        return ReadFromFile(path);
    }

    /// <inheritdoc/>
    public IReadOnlyList<MacroModel> LoadAll()
    {
        string dir = _paths.MacrosDirectory;
        if (!Directory.Exists(dir))
        {
            return Array.Empty<MacroModel>();
        }

        // Collect all candidate files; prefer .zmacro over .json for same stem
        var binaryFiles = Directory.GetFiles(dir, $"*{MacroSchema.BinaryExtension}");
        var jsonFiles = Directory.GetFiles(dir, $"*{MacroSchema.LegacyJsonExtension}");

        // Index existing binary stems so we skip legacy files that already have a binary twin
        var binaryStems = new HashSet<string>(
            binaryFiles.Select(f => Path.GetFileNameWithoutExtension(f)),
            StringComparer.OrdinalIgnoreCase);

        var candidates = new List<string>(binaryFiles.Length + jsonFiles.Length);
        candidates.AddRange(binaryFiles);
        foreach (var jf in jsonFiles)
        {
            if (!binaryStems.Contains(Path.GetFileNameWithoutExtension(jf)))
            {
                candidates.Add(jf);
            }
        }

        var results = new List<MacroModel>(candidates.Count);
        foreach (var file in candidates)
        {
            try
            {
                results.Add(ReadFromFile(file));
            }
            catch (Exception ex)
            {
                // Rule 18: surface failures but don't propagate — one bad file must not block loading all others.
                // Callers can inspect audit logs for skipped files.
                System.Diagnostics.Debug.WriteLine(
                    $"[MacroFileRepository] Skipping '{file}': {ex.GetType().Name}: {ex.Message}");
            }
        }

        return results;
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private async Task SaveWithSemaphoreAsync(MacroModel model, SemaphoreSlim semaphore, CancellationToken ct)
    {
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string finalPath = _paths.ResolveMacroWritePath(model.Id.ToString());
            await WriteAtomicAsync(model, finalPath, ct).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task WriteAtomicAsync(MacroModel model, string finalPath, CancellationToken ct)
    {
        // Ensure the target directory exists
        string? dir = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tmpPath = finalPath + ".tmp";
        try
        {
            string json = _serializer.Serialize(model);

            // Write to .tmp file
            await using (var fs = new FileStream(
                tmpPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 65536,
                useAsync: true))
            {
                MacroFileContainer.Write(fs, json);
            }

            // Atomically rename .tmp → final
            File.Move(tmpPath, finalPath, overwrite: true);
        }
        catch
        {
            // Clean up orphaned tmp file on failure (Rule 13)
            try { File.Delete(tmpPath); } catch { /* best-effort */ }
            throw;
        }
    }

    private MacroModel ReadFromFile(string path)
    {
        using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65536);

        string json = MacroFileContainer.HasMagicHeader(fs)
            ? MacroFileContainer.ReadJson(fs)
            : MacroFileContainer.ReadLegacyJson(fs);

        return _serializer.Deserialize(json);
    }
}
