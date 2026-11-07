using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Vision;
using Xunit;

namespace MacroSuite.Core.Tests.Storage;

public sealed class MacroSerializationTests
{
    [Fact]
    public void RoundTrip_AllBuiltInSteps_PreservesTypesAndValues()
    {
        var serializer = new MacroJsonSerializer();

        var model = new MacroModel
        {
            Name = "Full Test Macro",
            Description = "Comprehensive step serialization test",
            Settings = new MacroSettings
            {
                PlaybackSpeed = 1.5,
                RepeatCount = 2,
                RepeatUntilStopped = true,
                MaxRuntime = TimeSpan.FromMinutes(10)
            },
            Steps = new MacroStep[]
            {
                new ClickStep(new Point(120, 340), MouseButton.Right, clickCount: 2, delayAfterMs: 75),
                new KeyPressStep(65, delayAfterMs: 40), // VK_A
                new DelayStep(TimeSpan.FromMilliseconds(250)),
                new WaitUntilStep(new ColorCondition(new Point(10, 20), 255, 128, 0, tolerance: 5))
                {
                    Timeout = TimeSpan.FromSeconds(3)
                },
                new IfThenElseStep(
                    new ColorCondition(new Point(0, 0), 0, 0, 0),
                    new MacroStep[] { new DelayStep(TimeSpan.FromMilliseconds(50)) },
                    new MacroStep[] { new DelayStep(TimeSpan.FromMilliseconds(100)) }),
                new LoopStep(3, new MacroStep[] { new KeyPressStep(13) })
            }
        };

        string json = serializer.Serialize(model);
        var deserialized = serializer.Deserialize(json);

        deserialized.Name.Should().Be("Full Test Macro");
        deserialized.Settings.PlaybackSpeed.Should().Be(1.5);
        deserialized.Settings.RepeatCount.Should().Be(2);
        deserialized.Settings.RepeatUntilStopped.Should().BeTrue();
        deserialized.Steps.Should().HaveCount(6);

        deserialized.Steps[0].Should().BeOfType<ClickStep>();
        var click = (ClickStep)deserialized.Steps[0];
        click.Position.Should().Be(new Point(120, 340));
        click.Button.Should().Be(MouseButton.Right);
        click.ClickCount.Should().Be(2);
        click.DelayAfterMs.Should().Be(75);

        deserialized.Steps[1].Should().BeOfType<KeyPressStep>();
        var key = (KeyPressStep)deserialized.Steps[1];
        key.KeyCode.Should().Be(65);
        key.DelayAfterMs.Should().Be(40);

        deserialized.Steps[2].Should().BeOfType<DelayStep>();
        var delay = (DelayStep)deserialized.Steps[2];
        delay.Duration.Should().Be(TimeSpan.FromMilliseconds(250));

        deserialized.Steps[3].Should().BeOfType<WaitUntilStep>();
        deserialized.Steps[4].Should().BeOfType<IfThenElseStep>();
        deserialized.Steps[5].Should().BeOfType<LoopStep>();
    }

    [Fact]
    public void UnknownStep_PreservesTypeAndAttributesOnRoundTrip()
    {
        var serializer = new MacroJsonSerializer();

        string jsonWithUnknownStep = """
        {
          "schemaVersion": 1,
          "name": "Macro with Future Step",
          "settings": {},
          "steps": [
            {
              "$type": "future_quantum_click",
              "stepId": "00000000-0000-0000-0000-000000000001",
              "label": "Quantum Step",
              "isEnabled": true,
              "quantumCoordinate": 42.5,
              "entanglementMode": "spooky"
            }
          ]
        }
        """;

        var deserialized = serializer.Deserialize(jsonWithUnknownStep);

        deserialized.Steps.Should().HaveCount(1);
        deserialized.Steps[0].Should().BeOfType<UnknownStep>();

        var unknown = (UnknownStep)deserialized.Steps[0];
        unknown.RawType.Should().Be("future_quantum_click");
        unknown.Label.Should().Be("Quantum Step");
        unknown.RawData.Should().ContainKey("quantumCoordinate");
        unknown.RawData.Should().ContainKey("entanglementMode");

        // Now serialize back and verify properties are retained
        string reserializedJson = serializer.Serialize(deserialized);

        reserializedJson.Should().Contain("\"$type\": \"future_quantum_click\"");
        reserializedJson.Should().Contain("\"quantumCoordinate\": 42.5");
        reserializedJson.Should().Contain("\"entanglementMode\": \"spooky\"");
    }

    [Fact]
    public void Deserialize_AcceptsLegacyPascalCasePropertiesAndDiscriminator()
    {
        const string json = """
        {
          "SchemaVersion": 1,
          "Name": "Legacy casing",
          "Settings": {},
          "Steps": [
            {
              "$TYPE": "delay",
              "StepId": "00000000-0000-0000-0000-000000000011",
              "IsEnabled": true,
              "Duration": "00:00:00.2500000"
            }
          ]
        }
        """;

        MacroModel loaded = new MacroJsonSerializer().Deserialize(json);

        loaded.Name.Should().Be("Legacy casing");
        var delay = loaded.Steps.Should().ContainSingle().Which.Should().BeOfType<DelayStep>().Subject;
        delay.StepId.Should().Be(Guid.Parse("00000000-0000-0000-0000-000000000011"));
        delay.Duration.Should().Be(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void MouseSweep_RoundTrip_PreservesMotionFields()
    {
        var serializer = new MacroJsonSerializer();
        var model = new MacroModel
        {
            Name = "Sweep",
            Steps = new MacroStep[]
            {
                new MouseSweepStep(-250, 12, 850)
                {
                    Interpolation = MouseInterpolation.Bezier,
                    Profile = MouseSweepProfile.Custom,
                    PlaybackHz = 240,
                    JitterReduction = 25,
                    HorizontalSmoothing = 20,
                    VerticalSmoothing = 75,
                    FilterVerticalJitter = true,
                    SensitivityX = 1.1,
                    SensitivityY = 0.85
                }
            }
        };

        var deserialized = serializer.Deserialize(serializer.Serialize(model));
        var sweep = deserialized.Steps[0].Should().BeOfType<MouseSweepStep>().Subject;
        sweep.DeltaX.Should().Be(-250);
        sweep.DeltaY.Should().Be(12);
        sweep.DurationMs.Should().Be(850);
        sweep.Interpolation.Should().Be(MouseInterpolation.Bezier);
        sweep.PlaybackHz.Should().Be(240);
        sweep.JitterReduction.Should().Be(25);
        sweep.HorizontalSmoothing.Should().Be(20);
        sweep.VerticalSmoothing.Should().Be(75);
        sweep.FilterVerticalJitter.Should().BeTrue();
        sweep.SensitivityX.Should().BeApproximately(1.1, 0.0001);
        sweep.SensitivityY.Should().BeApproximately(0.85, 0.0001);
    }

    [Fact]
    public void VisionAndLoopSettings_RoundTripWithoutLoss()
    {
        var condition = new ColorCondition(
            new Rectangle(-300, 200, 40, 30),
            null,
            255,
            10,
            5,
            tolerance: 18,
            minMatchPercentage: 0.08,
            comparisonMode: ColorComparisonMode.Hsv,
            sampleStride: 2,
            additionalTargets: [new PixelColor(0, 255, 0), new PixelColor(0, 0, 255)]);
        var model = new MacroModel
        {
            Name = "Control",
            Steps =
            [
                new WaitUntilStep(condition)
                {
                    PollInterval = TimeSpan.FromMilliseconds(50),
                    RequiredConfirmations = 3,
                    Timeout = TimeSpan.FromSeconds(30)
                },
                new LoopStep(1000, [new ContinueStep(), new BreakStep()], condition)
                {
                    MaxDurationMs = 30_000,
                    CheckIntervalMs = 50,
                    RepeatUntil = true
                }
            ]
        };

        var loaded = new MacroJsonSerializer().Deserialize(new MacroJsonSerializer().Serialize(model));
        var wait = loaded.Steps[0].Should().BeOfType<WaitUntilStep>().Subject;
        var loadedColor = wait.Condition.Should().BeOfType<ColorCondition>().Subject;
        loadedColor.Region.Should().Be(new Rectangle(-300, 200, 40, 30));
        loadedColor.ComparisonMode.Should().Be(ColorComparisonMode.Hsv);
        loadedColor.MinMatchPercentage.Should().BeApproximately(0.08, 0.0001);
        loadedColor.SampleStride.Should().Be(2);
        loadedColor.AdditionalTargets.Should().Equal(new PixelColor(0, 255, 0), new PixelColor(0, 0, 255));
        wait.RequiredConfirmations.Should().Be(3);

        var loop = loaded.Steps[1].Should().BeOfType<LoopStep>().Subject;
        loop.RepeatUntil.Should().BeTrue();
        loop.CheckIntervalMs.Should().Be(50);
        loop.BodySteps[0].Should().BeOfType<ContinueStep>();
        loop.BodySteps[1].Should().BeOfType<BreakStep>();
    }

    [Fact]
    public void ColorCondition_V2ReliabilityFields_RoundTrip()
    {
        var condition = new ColorCondition(
            new Rectangle(12, 34, 50, 20),
            null,
            10,
            20,
            30,
            tolerance: 7,
            minMatchPercentage: 0.25,
            comparisonMode: ColorComparisonMode.CieDe2000,
            sampleStride: 3,
            additionalTargets: [new PixelColor(1, 2, 3)],
            pollIntervalMs: 80,
            requiredConfirmations: 4,
            requiredFalseConfirmations: 2,
            stabilityDurationMs: 120,
            timeoutMs: 4500,
            failurePolicy: ConditionFailurePolicy.Skip);
        var model = new MacroModel
        {
            Name = "v2-color",
            Steps =
            [
                new IfThenElseStep(
                    new VisualPresenceCondition
                    {
                        Kind = VisualPresenceKind.ColorPresent,
                        X = 1, Y = 2, Width = 3, Height = 4,
                        R = 5, G = 6, B = 7,
                        ComparisonMode = ColorComparisonMode.Hsv,
                        SampleStride = 2,
                        AdditionalTargets = [new PixelColor(9, 8, 7)],
                        PollIntervalMs = 90,
                        RequiredConfirmations = 3,
                        RequiredFalseConfirmations = 2,
                        StabilityDurationMs = 50,
                        TimeoutMs = 8000,
                        FailurePolicy = ConditionFailurePolicy.Retry
                    },
                    [new WaitUntilStep(condition)],
                    [new VisualWaitStep
                    {
                        X = 11, Y = 22, Width = 5, Height = 6,
                        R = 7, G = 8, B = 9,
                        ComparisonMode = ColorComparisonMode.Cie76,
                        SampleStride = 4,
                        AdditionalTargets = [new PixelColor(11, 12, 13)],
                        RequiredFalseConfirmations = 5,
                        StabilityDurationMs = 200,
                        FailurePolicy = ConditionFailurePolicy.Skip,
                        OnTimeout = "Skip"
                    }])
            ]
        };

        var loaded = new MacroJsonSerializer().Deserialize(new MacroJsonSerializer().Serialize(model));
        loaded.SchemaVersion.Should().Be(MacroSchema.CurrentVersion);
        var iff = loaded.Steps[0].Should().BeOfType<IfThenElseStep>().Subject;
        var presence = iff.Condition.Should().BeOfType<VisualPresenceCondition>().Subject;
        presence.ComparisonMode.Should().Be(ColorComparisonMode.Hsv);
        presence.SampleStride.Should().Be(2);
        presence.RequiredFalseConfirmations.Should().Be(2);
        presence.StabilityDurationMs.Should().Be(50);
        presence.TimeoutMs.Should().Be(8000);
        presence.FailurePolicy.Should().Be(ConditionFailurePolicy.Retry);
        presence.AdditionalTargets.Should().ContainSingle(c => c.R == 9 && c.G == 8 && c.B == 7);

        var wait = iff.ThenSteps[0].Should().BeOfType<WaitUntilStep>().Subject;
        var color = wait.Condition.Should().BeOfType<ColorCondition>().Subject;
        color.PollIntervalMs.Should().Be(80);
        color.RequiredConfirmations.Should().Be(4);
        color.RequiredFalseConfirmations.Should().Be(2);
        color.StabilityDurationMs.Should().Be(120);
        color.TimeoutMs.Should().Be(4500);
        color.FailurePolicy.Should().Be(ConditionFailurePolicy.Skip);
        color.SampleStride.Should().Be(3);
        color.ComparisonMode.Should().Be(ColorComparisonMode.CieDe2000);

        var visual = iff.ElseSteps[0].Should().BeOfType<VisualWaitStep>().Subject;
        visual.ComparisonMode.Should().Be(ColorComparisonMode.Cie76);
        visual.SampleStride.Should().Be(4);
        visual.RequiredFalseConfirmations.Should().Be(5);
        visual.StabilityDurationMs.Should().Be(200);
        visual.FailurePolicy.Should().Be(ConditionFailurePolicy.Skip);
        visual.AdditionalTargets.Should().ContainSingle(c => c.R == 11);
    }

    [Fact]
    public void WaitWhile_RoundTripsOnWaitUntilStep()
    {
        var serializer = new MacroJsonSerializer();
        var model = new MacroModel
        {
            Name = "wait while",
            Steps =
            [
                new WaitUntilStep(new ColorCondition(new Point(3, 4), 1, 2, 3))
                {
                    WaitWhile = true,
                    Timeout = TimeSpan.FromSeconds(2),
                    Label = WaitUntilStep.FormatLabel(new ColorCondition(new Point(3, 4), 1, 2, 3), waitWhile: true)
                }
            ]
        };

        var loaded = serializer.Deserialize(serializer.Serialize(model));
        var wait = loaded.Steps[0].Should().BeOfType<WaitUntilStep>().Subject;
        wait.WaitWhile.Should().BeTrue();
        wait.Label.Should().StartWith("Wait while:");
    }

    [Fact]
    public void RoundTrip_ImageWait_PreservesImageCondition()
    {
        var serializer = new MacroJsonSerializer();
        var model = new MacroModel
        {
            Name = "Find Image",
            Steps =
            [
                new WaitUntilStep(new ImageCondition("hero.png", 0.85))
                {
                    Timeout = TimeSpan.FromSeconds(12),
                    PollInterval = TimeSpan.FromMilliseconds(80),
                    ThrowOnTimeout = true
                }
            ]
        };

        var loaded = serializer.Deserialize(serializer.Serialize(model));
        var wait = loaded.Steps[0].Should().BeOfType<WaitUntilStep>().Subject;
        wait.Condition.Should().BeOfType<ImageCondition>().Which.TemplatePath.Should().Be("hero.png");
        wait.Timeout.Should().Be(TimeSpan.FromSeconds(12));
        wait.ThrowOnTimeout.Should().BeTrue();
    }
}
