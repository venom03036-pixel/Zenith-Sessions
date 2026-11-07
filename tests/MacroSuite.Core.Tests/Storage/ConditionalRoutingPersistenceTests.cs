using FluentAssertions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using Xunit;

namespace MacroSuite.Core.Tests.Storage;

public sealed class ConditionalRoutingPersistenceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void OmittedPolicy_UsesNewDefault_WhileExplicitStopIsPreserved(int version)
    {
        string json = $$"""
        { "schemaVersion": {{version}}, "steps": [
          { "$type":"ifthenelse", "condition":{"$type":"visualpresence"}, "thenSteps":[] },
          { "$type":"loop", "maxIterations":2, "whileCondition":{"$type":"visualpresence","failurePolicy":0}, "bodySteps":[] }
        ] }
        """;
        var serializer = new MacroJsonSerializer();
        MacroModel first = serializer.Deserialize(json);
        var omitted = (VisualPresenceCondition)((IfThenElseStep)first.Steps[0]).Condition;
        var explicitStop = (VisualPresenceCondition)((LoopStep)first.Steps[1]).WhileCondition!;
        omitted.FailurePolicy.Should().Be(ConditionFailurePolicy.Default);
        explicitStop.FailurePolicy.Should().Be(ConditionFailurePolicy.Stop);
        MacroModel second = serializer.Deserialize(serializer.Serialize(first));
        ((VisualPresenceCondition)((IfThenElseStep)second.Steps[0]).Condition).FailurePolicy.Should().Be(ConditionFailurePolicy.Default);
        ((VisualPresenceCondition)((LoopStep)second.Steps[1]).WhileCondition!).FailurePolicy.Should().Be(ConditionFailurePolicy.Stop);
    }

    [Fact]
    public void Routes_RoundTripAndFactoryReplacement_PreserveTargetsChildrenAndPolicies()
    {
        var target = new DelayStep(TimeSpan.Zero);
        var options = new BlockRoutingOptions
        {
            OnNotSatisfied = FlowRoute.Jump, OnFailure = FlowRoute.Stop,
            TargetStepId = target.StepId, RetryCount = 7, RetryDelayMs = 333
        };
        var child = new DelayStep(TimeSpan.FromMilliseconds(5));
        var branch = new IfThenElseStep(new VisualPresenceCondition(), [child]) { Routing = options };
        var loop = new LoopStep(2, [branch], new VisualPresenceCondition()) { Routing = options };
        var serializer = new MacroJsonSerializer();
        var saved = serializer.Deserialize(serializer.Serialize(new MacroModel { Steps = [loop, target] }));
        var loaded = (LoopStep)saved.Steps[0];
        loaded.Routing.Should().BeEquivalentTo(options);
        ((IfThenElseStep)loaded.BodySteps[0]).Routing.Should().BeEquivalentTo(options);
        ((IfThenElseStep)loaded.BodySteps[0]).ThenSteps[0].StepId.Should().Be(child.StepId);
        var editedLoop = (LoopStep)MacroActionFactory.Convert(loop, EditableActionKind.WhileLoop);
        editedLoop.BodySteps[0].StepId.Should().Be(branch.StepId);
        editedLoop.Routing.Should().BeEquivalentTo(options);
        var editedIf = MacroActionFactory.WithIfCondition(branch, new VisualPresenceCondition());
        editedIf.ThenSteps[0].StepId.Should().Be(child.StepId);
        editedIf.Routing.Should().BeEquivalentTo(options);
        var clone = (LoopStep)MacroActionFactory.Clone(loop);
        clone.StepId.Should().NotBe(loop.StepId);
        clone.BodySteps[0].StepId.Should().NotBe(branch.StepId);
        clone.Routing.Should().BeEquivalentTo(options);
    }
}
