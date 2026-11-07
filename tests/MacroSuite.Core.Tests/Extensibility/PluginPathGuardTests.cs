using FluentAssertions;
using MacroSuite.Core.Extensibility;
using Xunit;

namespace MacroSuite.Core.Tests.Extensibility;

public sealed class PluginPathGuardTests
{
    [Fact]
    public void RootedEntryAssembly_IsRejected()
    {
        string root = Path.Combine(Path.GetTempPath(), "plugins");
        string folder = Path.Combine(root, "demo");
        PluginPathGuard.TryResolveEntryAssembly(root, folder, @"C:\evil.dll", out _, out string error)
            .Should().BeFalse();
        error.Should().Contain("relative");
    }

    [Fact]
    public void ParentSegments_AreRejected()
    {
        string root = Path.Combine(Path.GetTempPath(), "plugins");
        string folder = Path.Combine(root, "demo");
        PluginPathGuard.TryResolveEntryAssembly(root, folder, @"..\..\evil.dll", out _, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void RelativeDll_ResolvesUnderFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), $"plugins_{Guid.NewGuid():N}");
        string folder = Path.Combine(root, "demo");
        Directory.CreateDirectory(folder);
        try
        {
            PluginPathGuard.TryResolveEntryAssembly(root, folder, "plugin.dll", out string full, out _)
                .Should().BeTrue();
            full.Should().Be(Path.GetFullPath(Path.Combine(folder, "plugin.dll")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
