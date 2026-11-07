using FluentAssertions;
using MacroSuite.Core.Execution;
using Xunit;

namespace MacroSuite.Core.Tests.Execution;

public sealed class MacroVariablesTests
{
    [Fact]
    public void SetAndGet_RetrievesTypedValues()
    {
        var vars = new MacroVariables();

        vars.Set("counter", 42);
        vars.Set("message", "hello world");

        vars.Get<int>("counter").Should().Be(42);
        vars.Get<string>("message").Should().Be("hello world");
    }

    [Fact]
    public void TryGet_WithCompatibleTypeConversion_ReturnsTrue()
    {
        var vars = new MacroVariables();
        vars.Set("stringInt", "123");

        bool success = vars.TryGet<int>("stringInt", out int value);

        success.Should().BeTrue();
        value.Should().Be(123);
    }

    [Fact]
    public void ContainsAndRemove_FunctionsCorrectly()
    {
        var vars = new MacroVariables();
        vars.Set("temp", "val");

        vars.Contains("temp").Should().BeTrue();
        vars.Remove("temp").Should().BeTrue();
        vars.Contains("temp").Should().BeFalse();
    }

    [Fact]
    public void Snapshot_ProvidesIsolatedCopy()
    {
        var vars = new MacroVariables();
        vars.Set("key", "initial");

        var snapshot = vars.ToSnapshot();
        vars.Set("key", "modified");

        snapshot["key"].Should().Be("initial");
        vars.Get<string>("key").Should().Be("modified");
    }
}
