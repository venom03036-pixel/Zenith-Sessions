using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using Xunit;

namespace MacroSuite.Core.Tests.Storage;

public sealed class StepTypeRegistryTests
{
    [Fact]
    public void KnownDiscriminators_ResolveToMacroStepTypes()
    {
        StepTypeRegistry.KnownDiscriminators.Should().NotBeEmpty();
        foreach (string tag in StepTypeRegistry.KnownDiscriminators)
        {
            Type? type = StepTypeRegistry.ResolveClrType(tag);
            type.Should().NotBeNull($"'{tag}' must resolve to a CLR step type");
            type!.IsSubclassOf(typeof(MacroStep)).Should().BeTrue();
            StepTypeRegistry.GetCanonicalDiscriminator(type).Should().NotBeNull();
        }
    }

    [Fact]
    public void Serializer_WriteTags_MatchCanonicalRegistry()
    {
        var serializer = new MacroJsonSerializer();
        MacroStep[] samples =
        [
            new ClickStep(),
            new MouseMoveStep(),
            new MouseWheelStep(),
            new MouseSweepStep(),
            new MouseTrajectoryStep(),
            new KeyPressStep(),
            new DelayStep(),
            new WaitUntilStep(new ColorCondition(new Point(1, 1), 1, 2, 3)),
            new VisualWaitStep(),
            new IfThenElseStep(new ColorCondition(new Point(0, 0), 0, 0, 0), []),
            new LoopStep(1, []),
            new BreakStep(),
            new ContinueStep(),
            new AIStep(),
            new PluginStep()
        ];

        foreach (MacroStep step in samples)
        {
            string expected = StepTypeRegistry.GetCanonicalDiscriminator(step.GetType())!;
            string json = serializer.Serialize(new MacroModel
            {
                Name = "registry",
                SchemaVersion = MacroSchema.CurrentVersion,
                Steps = [step]
            });
            json.Should().Contain($"\"$type\": \"{expected}\"");
            StepTypeRegistry.ResolveClrType(expected).Should().Be(step.GetType());
        }
    }

    [Fact]
    public void ValidateKnownDiscriminators_DoesNotThrow()
    {
        var act = StepTypeRegistry.ValidateKnownDiscriminators;
        act.Should().NotThrow();
    }

    [Fact]
    public void Aliases_ResolveToCanonicalClrTypes()
    {
        StepTypeRegistry.ResolveClrType("mouse_hold").Should().Be(typeof(MouseSweepStep));
        StepTypeRegistry.ResolveClrType("camera_trajectory").Should().Be(typeof(MouseTrajectoryStep));
    }
}
