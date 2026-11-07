using MacroSuite.SDK.Contracts;

namespace MacroSuite.SDK.Tests.Contracts;

/// <summary>
/// The manifest check runs before any plugin assembly is loaded, so every rejection must name the field
/// that is wrong and nothing may get through with a missing entry point or an unreadable version.
/// </summary>
public sealed class PluginManifestTests
{
    private static PluginManifest Complete(
        string pluginId = "vendor.step.sample",
        string displayName = "Sample step",
        string entryAssembly = "Vendor.Sample.dll",
        string entryType = "Vendor.Sample.SamplePlugin",
        string version = "1.2.0",
        string minHostVersion = "1.0.0") => new()
    {
        PluginId = pluginId,
        DisplayName = displayName,
        EntryAssembly = entryAssembly,
        EntryType = entryType,
        Version = version,
        MinHostVersion = minHostVersion,
    };

    [Fact]
    public void Validate_CompleteManifest_IsValid()
    {
        var result = Complete().Validate();

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void NewManifest_DefaultsToVersionOneAndEmptyRequiredFields_SoItIsRejected()
    {
        var manifest = new PluginManifest();

        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal("1.0.0", manifest.MinHostVersion);
        Assert.Empty(manifest.RequiredFeatures);
        var result = manifest.Validate();
        Assert.False(result.IsValid);
        Assert.Equal("Manifest is missing required 'pluginId'.", result.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankPluginId_NamesPluginId(string blank)
    {
        var result = Complete(pluginId: blank).Validate();

        Assert.False(result.IsValid);
        Assert.Equal("Manifest is missing required 'pluginId'.", result.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankDisplayName_NamesDisplayName(string blank)
    {
        var result = Complete(displayName: blank).Validate();

        Assert.False(result.IsValid);
        Assert.Equal("Manifest is missing required 'displayName'.", result.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankEntryAssembly_NamesEntryAssembly(string blank)
    {
        var result = Complete(entryAssembly: blank).Validate();

        Assert.False(result.IsValid);
        Assert.Equal("Manifest is missing required 'entryAssembly'.", result.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankEntryType_NamesEntryType(string blank)
    {
        var result = Complete(entryType: blank).Validate();

        Assert.False(result.IsValid);
        Assert.Equal("Manifest is missing required 'entryType'.", result.ErrorMessage);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1")]
    [InlineData("")]
    public void Validate_UnreadableVersion_QuotesTheValue(string version)
    {
        var result = Complete(version: version).Validate();

        Assert.False(result.IsValid);
        Assert.Equal($"Manifest contains invalid version '{version}'.", result.ErrorMessage);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1")]
    [InlineData("")]
    public void Validate_UnreadableMinHostVersion_QuotesTheValue(string minHostVersion)
    {
        var result = Complete(minHostVersion: minHostVersion).Validate();

        Assert.False(result.IsValid);
        Assert.Equal($"Manifest contains invalid minHostVersion '{minHostVersion}'.", result.ErrorMessage);
    }

    [Fact]
    public void Validate_SeveralProblems_ReportsTheFirstRequiredFieldFirst()
    {
        var result = Complete(displayName: "", entryType: "", version: "abc").Validate();

        Assert.False(result.IsValid);
        Assert.Equal("Manifest is missing required 'displayName'.", result.ErrorMessage);
    }
}
