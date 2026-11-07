using FluentAssertions;
using MacroSuite.Core.Storage;
using Xunit;

namespace MacroSuite.Core.Tests.Storage;

public sealed class MacroMigrationTests
{
    [Fact]
    public void MigrateV0ToV1_PopulatesMissingSchemaVersionAndSettings()
    {
        var serializer = new MacroJsonSerializer();

        // Legacy V0 JSON without schemaVersion or settings
        string v0Json = """
        {
          "name": "Legacy V0 Macro",
          "steps": [
            {
              "$type": "delay",
              "duration": "00:00:01"
            }
          ]
        }
        """;

        var macro = serializer.Deserialize(v0Json, applyMigrations: true);

        macro.SchemaVersion.Should().Be(MacroSchema.CurrentVersion);
        macro.Name.Should().Be("Legacy V0 Macro");
        macro.Settings.Should().NotBeNull();
        macro.Settings.PlaybackSpeed.Should().Be(1.0);
        macro.Settings.RepeatCount.Should().Be(1);
        macro.Steps.Should().HaveCount(1);
    }

    [Fact]
    public void FutureSchemaVersion_ThrowsInvalidOperationException()
    {
        var serializer = new MacroJsonSerializer();

        string futureJson = """
        {
          "schemaVersion": 999,
          "name": "From the Future",
          "steps": []
        }
        """;

        var act = () => serializer.Deserialize(futureJson, applyMigrations: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*newer version*");
    }
}
