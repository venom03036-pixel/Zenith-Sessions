using System.Text.Json;
using FluentAssertions;
using MacroSuite.Infrastructure.Plugins;
using MacroSuite.SDK.Contracts;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Plugins;

public sealed class PluginLoaderTests : IDisposable
{
    private readonly string _tempRootDirectory;

    public PluginLoaderTests()
    {
        _tempRootDirectory = Path.Combine(Path.GetTempPath(), $"ZenithMacros_PluginsTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRootDirectory);
    }

    [Fact]
    public void LoadPluginFromFolder_MissingManifest_ReturnsFailed()
    {
        using var loader = new PluginLoader();
        string folder = Path.Combine(_tempRootDirectory, "empty_plugin");
        Directory.CreateDirectory(folder);

        var result = loader.LoadPluginFromFolder(folder);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Missing 'plugin.json'");
    }

    [Fact]
    public void LoadPluginFromFolder_InvalidManifest_ReturnsFailedWithValidationErrors()
    {
        using var loader = new PluginLoader();
        string folder = Path.Combine(_tempRootDirectory, "invalid_manifest_plugin");
        Directory.CreateDirectory(folder);

        var invalidManifest = new
        {
            pluginId = "",
            displayName = "",
            entryAssembly = ""
        };
        File.WriteAllText(Path.Combine(folder, "plugin.json"), JsonSerializer.Serialize(invalidManifest));

        var result = loader.LoadPluginFromFolder(folder);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Manifest validation failed");
    }

    [Fact]
    public void LoadPluginFromFolder_MissingAssembly_ReturnsFailed()
    {
        using var loader = new PluginLoader();
        string folder = Path.Combine(_tempRootDirectory, "missing_assembly_plugin");
        Directory.CreateDirectory(folder);

        var manifest = new PluginManifest
        {
            PluginId = "vendor.missing.assembly",
            DisplayName = "Missing Assembly",
            EntryAssembly = "NonExistentAssembly.dll",
            EntryType = "Vendor.Missing.PluginEntry"
        };
        File.WriteAllText(Path.Combine(folder, "plugin.json"), JsonSerializer.Serialize(manifest));

        var result = loader.LoadPluginFromFolder(folder);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Entry assembly file not found");
    }

    [Fact]
    public void LoadPluginsFromDirectory_MixedValidAndInvalid_HandlesGracefully()
    {
        using var loader = new PluginLoader();

        // 1. Create one directory with missing manifest
        string folder1 = Path.Combine(_tempRootDirectory, "bad_plugin_1");
        Directory.CreateDirectory(folder1);

        // 2. Create second directory with missing assembly
        string folder2 = Path.Combine(_tempRootDirectory, "bad_plugin_2");
        Directory.CreateDirectory(folder2);
        var manifest2 = new PluginManifest
        {
            PluginId = "bad.plugin.2",
            DisplayName = "Bad 2",
            EntryAssembly = "NonExistent.dll",
            EntryType = "BadType"
        };
        File.WriteAllText(Path.Combine(folder2, "plugin.json"), JsonSerializer.Serialize(manifest2));

        var results = loader.LoadPluginsFromDirectory(_tempRootDirectory);

        results.Should().HaveCount(2);
        results.All(r => !r.Success).Should().BeTrue();
    }

    [Fact]
    public void LoadPluginFromFolder_RootedEntryAssembly_IsRejected()
    {
        using var loader = new PluginLoader();
        string folder = Path.Combine(_tempRootDirectory, "evil_plugin");
        Directory.CreateDirectory(folder);
        var manifest = new PluginManifest
        {
            PluginId = "evil.path",
            DisplayName = "Evil",
            EntryAssembly = @"C:\evil.dll",
            EntryType = "Evil.Plugin"
        };
        File.WriteAllText(Path.Combine(folder, "plugin.json"), JsonSerializer.Serialize(manifest));

        var result = loader.LoadPluginFromFolder(folder, _tempRootDirectory);
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("relative");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRootDirectory))
            {
                Directory.Delete(_tempRootDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
