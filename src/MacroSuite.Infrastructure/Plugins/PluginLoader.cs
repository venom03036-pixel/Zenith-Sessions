using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using MacroSuite.Core.Extensibility;
using MacroSuite.SDK.Contracts;

namespace MacroSuite.Infrastructure.Plugins;

/// <summary>
/// Result of attempting to discover, validate, and load an isolated plugin assembly.
/// </summary>
public sealed class PluginLoadResult
{
    public bool Success { get; }
    public IPluginDescriptor? Plugin { get; }
    public PluginManifest? Manifest { get; }
    public string? ErrorMessage { get; }

    private PluginLoadResult(bool success, IPluginDescriptor? plugin, PluginManifest? manifest, string? errorMessage)
    {
        Success = success;
        Plugin = plugin;
        Manifest = manifest;
        ErrorMessage = errorMessage;
    }

    public static PluginLoadResult Loaded(IPluginDescriptor plugin, PluginManifest manifest) =>
        new(true, plugin, manifest, null);

    public static PluginLoadResult Failed(string errorMessage, PluginManifest? manifest = null) =>
        new(false, null, manifest, errorMessage);
}

/// <summary>
/// Dedicated collectible <see cref="AssemblyLoadContext"/> providing isolation for an individual plugin folder.
/// </summary>
internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginAssemblyLoadContext(string pluginMainAssemblyPath)
        : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginMainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath != null)
        {
            return LoadFromAssemblyPath(assemblyPath);
        }

        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (libraryPath != null)
        {
            return LoadUnmanagedDllFromPath(libraryPath);
        }

        return IntPtr.Zero;
    }
}

/// <summary>
/// Discovers, validates, and loads third-party plugins using per-plugin <see cref="AssemblyLoadContext"/> isolation (Rule 18).
/// </summary>
public sealed class PluginLoader : IDisposable
{
    private readonly List<PluginAssemblyLoadContext> _contexts = new();
    private bool _isDisposed;

    /// <summary>
    /// Scans a root directory for plugin subfolders containing <c>plugin.json</c> and loads them.
    /// Faulty plugins are skipped and surfaced as failed results without aborting valid plugins.
    /// </summary>
    public IReadOnlyList<PluginLoadResult> LoadPluginsFromDirectory(string rootPluginsDirectory)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (!Directory.Exists(rootPluginsDirectory))
        {
            return Array.Empty<PluginLoadResult>();
        }

        var results = new List<PluginLoadResult>();
        var subDirs = Directory.GetDirectories(rootPluginsDirectory);

        foreach (var dir in subDirs)
        {
            var result = LoadPluginFromFolder(dir, rootPluginsDirectory);
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Loads an individual plugin folder by parsing its manifest and instantiating the entry type in an isolated ALC.
    /// </summary>
    public PluginLoadResult LoadPluginFromFolder(string pluginFolder)
        => LoadPluginFromFolder(pluginFolder, pluginFolder);

    public PluginLoadResult LoadPluginFromFolder(string pluginFolder, string pluginRoot)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        string manifestPath = Path.Combine(pluginFolder, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            return PluginLoadResult.Failed($"Missing 'plugin.json' manifest in '{pluginFolder}'.");
        }

        PluginManifest? manifest;
        try
        {
            string json = File.ReadAllText(manifestPath);
            manifest = JsonSerializer.Deserialize<PluginManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            return PluginLoadResult.Failed($"Failed to parse 'plugin.json': {ex.Message}");
        }

        if (manifest is null)
        {
            return PluginLoadResult.Failed("Manifest deserialized to null.");
        }

        var validation = manifest.Validate();
        if (!validation.IsValid)
        {
            return PluginLoadResult.Failed($"Manifest validation failed: {validation.ErrorMessage}", manifest);
        }

        if (!PluginPathGuard.TryResolveEntryAssembly(pluginRoot, pluginFolder, manifest.EntryAssembly, out string assemblyPath, out string pathError))
        {
            return PluginLoadResult.Failed(pathError, manifest);
        }

        if (!File.Exists(assemblyPath))
        {
            return PluginLoadResult.Failed($"Entry assembly file not found: '{assemblyPath}'", manifest);
        }

        try
        {
            var alc = new PluginAssemblyLoadContext(assemblyPath);
            _contexts.Add(alc);

            var assembly = alc.LoadFromAssemblyPath(assemblyPath);
            Type? entryType = assembly.GetType(manifest.EntryType, throwOnError: false, ignoreCase: true);

            if (entryType is null)
            {
                return PluginLoadResult.Failed($"Entry type '{manifest.EntryType}' not found in assembly '{manifest.EntryAssembly}'.", manifest);
            }

            if (!typeof(IPluginDescriptor).IsAssignableFrom(entryType))
            {
                return PluginLoadResult.Failed($"Entry type '{manifest.EntryType}' does not implement {nameof(IPluginDescriptor)}.", manifest);
            }

            var pluginInstance = (IPluginDescriptor?)Activator.CreateInstance(entryType);
            if (pluginInstance is null)
            {
                return PluginLoadResult.Failed($"Failed to instantiate entry type '{manifest.EntryType}'.", manifest);
            }

            return PluginLoadResult.Loaded(pluginInstance, manifest);
        }
        catch (Exception ex)
        {
            return PluginLoadResult.Failed($"Exception occurred while loading assembly: {ex.Message}", manifest);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        foreach (var alc in _contexts)
        {
            try
            {
                alc.Unload();
            }
            catch
            {
                // Best effort unload
            }
        }

        _contexts.Clear();
    }
}
