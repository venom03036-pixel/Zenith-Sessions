using FluentAssertions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Steps;
using Xunit;

namespace MacroSuite.Core.Tests.Storage;

public sealed class VisualWaitJsonTests
{
    [Fact]
    public void VisualWaitStep_RoundTripsWithoutBreakingUnknownMacros()
    {
        var serializer = new MacroJsonSerializer();
        var model = new MacroModel
        {
            Name = "visual",
            Steps =
            [
                new VisualWaitStep
                {
                    Mode = "Pixel",
                    X = 500,
                    Y = 300,
                    R = 63,
                    G = 167,
                    B = 214,
                    SimilarityPercent = 90,
                    PollIntervalMs = 150,
                    TimeoutMs = 10000,
                    OnTimeout = "Stop"
                }
            ]
        };

        string json = serializer.Serialize(model);
        json.Should().Contain("\"$type\": \"visualwait\"");

        var loaded = serializer.Deserialize(json);
        loaded.Steps.Should().ContainSingle();
        var step = loaded.Steps[0].Should().BeOfType<VisualWaitStep>().Subject;
        step.X.Should().Be(500);
        step.Y.Should().Be(300);
        step.R.Should().Be(63);
        step.SimilarityPercent.Should().Be(90);
        step.PollIntervalMs.Should().Be(150);
    }

    [Fact]
    public void LegacyClickMacro_StillLoads()
    {
        var serializer = new MacroJsonSerializer();
        const string json = """
            {"name":"old","steps":[{"$type":"click","position":{"x":10,"y":20}}]}
            """;
        var model = serializer.Deserialize(json);
        model.Steps.Should().ContainSingle();
        model.Steps[0].Should().BeOfType<ClickStep>();
    }
}
