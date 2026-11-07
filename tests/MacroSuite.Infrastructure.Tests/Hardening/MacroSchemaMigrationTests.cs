using System.Text.Json.Nodes;
using FluentAssertions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Vision;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Hardening;

public class MacroSchemaMigrationTests
{
    [Fact]
    public void MigrateLegacyV0Json_AddsSchemaVersionAndSettings_RoundTripsSuccessfully()
    {
        // Legacy format without schemaVersion or modern settings
        string legacyJson = """
        {
          "name": "Legacy Daily Login",
          "description": "Created in version 0 pre-release",
          "steps": [
            {
              "$type": "click",
              "Position": { "X": 500, "Y": 350 },
              "Button": 0,
              "ClickCount": 1,
              "DelayAfterMs": 50
            },
            {
              "$type": "delay",
              "Duration": "00:00:01"
            }
          ]
        }
        """;

        var engine = new MacroMigrationEngine();
        var node = JsonNode.Parse(legacyJson);
        node.Should().NotBeNull();

        int originalVersion = MacroMigrationEngine.ExtractSchemaVersion(node!);
        originalVersion.Should().Be(0);

        // Migrate to CurrentVersion (v0 → v1 → v2)
        var migratedNode = engine.Migrate(node!, MacroSchema.CurrentVersion);
        migratedNode.Should().NotBeNull();

        int migratedVersion = MacroMigrationEngine.ExtractSchemaVersion(migratedNode);
        migratedVersion.Should().Be(MacroSchema.CurrentVersion);

        // Verify injected settings
        migratedNode["settings"].Should().NotBeNull();
        migratedNode["settings"]!["playbackSpeed"]!.GetValue<double>().Should().Be(1.0);
        migratedNode["settings"]!["maxLoopDepth"]!.GetValue<int>().Should().Be(8);

        // Deserialize migrated JSON into MacroModel
        var serializer = new MacroJsonSerializer();
        var model = serializer.Deserialize(migratedNode.ToJsonString());

        model.Should().NotBeNull();
        model!.Name.Should().Be("Legacy Daily Login");
        model.Steps.Should().HaveCount(2);
        model.Steps[0].Should().BeOfType<ClickStep>();
        model.Steps[1].Should().BeOfType<DelayStep>();

        // Re-serialize and verify schemaVersion stays current
        string reserialized = serializer.Serialize(model);
        reserialized.Should().Contain($"\"schemaVersion\": {MacroSchema.CurrentVersion}");
    }

    [Fact]
    public void Migrate_FutureSchemaVersion_ThrowsInvalidOperationException()
    {
        string futureJson = """
        {
          "schemaVersion": 99,
          "name": "Future Macro",
          "steps": []
        }
        """;

        var engine = new MacroMigrationEngine();
        var node = JsonNode.Parse(futureJson)!;

        var act = () => engine.Migrate(node, targetVersion: 1);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*created with a newer version*");
    }

    [Fact]
    public void MigrateV1ToV2_PreservesNestedBranchesPalettesRegionsAndUnknownSteps()
    {
        const string v1 = """
        {
          "schemaVersion": 1,
          "name": "Nested vision",
          "settings": { "maxLoopDepth": 8, "maxIterations": 100000 },
          "steps": [
            {
              "$type": "ifthenelse",
              "condition": {
                "$type": "color",
                "region": { "x": -300, "y": 200, "width": 40, "height": 30 },
                "r": 255, "g": 10, "b": 5,
                "tolerance": 18,
                "minMatchPercentage": 0.08,
                "comparisonMode": 1,
                "sampleStride": 2,
                "additionalTargets": [ { "r": 0, "g": 255, "b": 0 } ]
              },
              "thenSteps": [
                {
                  "$type": "loop",
                  "maxIterations": 50,
                  "maxDurationMs": 12000,
                  "checkIntervalMs": 40,
                  "whileCondition": {
                    "$type": "visualpresence",
                    "kind": 0,
                    "x": 10, "y": 20, "width": 30, "height": 15,
                    "r": 1, "g": 2, "b": 3,
                    "requiredPixelFraction": 0.2
                  },
                  "bodySteps": [
                    {
                      "$type": "future_quantum_click",
                      "quantumCoordinate": 42.5,
                      "entanglementMode": "spooky"
                    }
                  ]
                }
              ],
              "elseSteps": [
                {
                  "$type": "visualwait",
                  "mode": "Pixel",
                  "x": 8, "y": 9,
                  "r": 9, "g": 8, "b": 7,
                  "onTimeout": "Skip"
                }
              ]
            }
          ]
        }
        """;

        var engine = new MacroMigrationEngine();
        var migrated = engine.Migrate(JsonNode.Parse(v1)!);
        MacroMigrationEngine.ExtractSchemaVersion(migrated).Should().Be(MacroSchema.CurrentVersion);

        var color = migrated["steps"]![0]!["condition"]!.AsObject();
        color["pollIntervalMs"]!.GetValue<int>().Should().Be(50);
        color["requiredFalseConfirmations"]!.GetValue<int>().Should().Be(1);
        color["additionalTargets"]!.AsArray().Should().HaveCount(1);
        color["region"]!["x"]!.GetValue<int>().Should().Be(-300);

        migrated["steps"]![0]!["elseSteps"]![0]!["failurePolicy"]!.GetValue<int>().Should().Be(1);

        var loop = migrated["steps"]![0]!["thenSteps"]![0]!.AsObject();
        loop["maxDurationMs"]!.GetValue<int>().Should().Be(12000);
        loop["checkIntervalMs"]!.GetValue<int>().Should().Be(40);
        loop["minBodyDelayMs"]!.GetValue<int>().Should().Be(0);
        loop["whileCondition"]!["comparisonMode"]!.GetValue<int>().Should().Be(2);
        loop["bodySteps"]![0]!["$type"]!.GetValue<string>().Should().Be("future_quantum_click");
        loop["bodySteps"]![0]!["quantumCoordinate"]!.GetValue<double>().Should().Be(42.5);

        migrated["steps"]![0]!["elseSteps"]![0]!["failurePolicy"]!.GetValue<int>().Should().Be(1);

        var model = new MacroJsonSerializer().Deserialize(migrated.ToJsonString());
        model.SchemaVersion.Should().Be(MacroSchema.CurrentVersion);
        var iff = model.Steps[0].Should().BeOfType<IfThenElseStep>().Subject;
        var loadedColor = iff.Condition.Should().BeOfType<ColorCondition>().Subject;
        loadedColor.ComparisonMode.Should().Be(ColorComparisonMode.Hsv);
        loadedColor.SampleStride.Should().Be(2);
        loadedColor.Region.Should().Be(new System.Drawing.Rectangle(-300, 200, 40, 30));
        loadedColor.AdditionalTargets.Should().ContainSingle(c => c.R == 0 && c.G == 255 && c.B == 0);

        var nestedLoop = iff.ThenSteps[0].Should().BeOfType<LoopStep>().Subject;
        nestedLoop.MaxDurationMs.Should().Be(12000);
        nestedLoop.CheckIntervalMs.Should().Be(40);
        nestedLoop.BodySteps[0].Should().BeOfType<UnknownStep>();
        ((UnknownStep)nestedLoop.BodySteps[0]).RawType.Should().Be("future_quantum_click");

        var visual = iff.ElseSteps[0].Should().BeOfType<VisualWaitStep>().Subject;
        visual.FailurePolicy.Should().Be(ConditionFailurePolicy.Skip);

        string roundTrip = new MacroJsonSerializer().Serialize(model);
        roundTrip.Should().Contain($"\"schemaVersion\": {MacroSchema.CurrentVersion}");
        roundTrip.Should().Contain("future_quantum_click");
        roundTrip.Should().Contain("quantumCoordinate");
    }

    [Fact]
    public void MigrateV1ColorWait_AddsWaitWhileFalseAndReliabilityFields()
    {
        const string v1 = """
        {
          "schemaVersion": 1,
          "name": "color wait",
          "steps": [
            {
              "$type": "waituntil",
              "timeout": "00:00:05",
              "condition": {
                "$type": "color",
                "location": { "x": 12, "y": 18 },
                "r": 10, "g": 20, "b": 30,
                "tolerance": 8
              }
            }
          ]
        }
        """;

        var migrated = new MacroMigrationEngine().Migrate(System.Text.Json.Nodes.JsonNode.Parse(v1)!);
        MacroMigrationEngine.ExtractSchemaVersion(migrated).Should().Be(MacroSchema.CurrentVersion);
        migrated["steps"]![0]!["waitWhile"]!.GetValue<bool>().Should().BeFalse();
        migrated["steps"]![0]!["requiredConfirmations"]!.GetValue<int>().Should().Be(1);
        migrated["steps"]![0]!["condition"]!["pollIntervalMs"]!.GetValue<int>().Should().Be(50);

        var model = new MacroJsonSerializer().Deserialize(migrated.ToJsonString());
        var wait = model.Steps[0].Should().BeOfType<WaitUntilStep>().Subject;
        wait.WaitWhile.Should().BeFalse();
        wait.Condition.Should().BeOfType<ColorCondition>().Which.PollIntervalMs.Should().Be(50);
    }
}
