using FluentAssertions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using Xunit;

namespace MacroSuite.Core.Tests.Models;

public sealed class TrajectoryEditingTimingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditCloneAndRoundTrip_RetainTimestampConvention(bool timestampIsStart)
    {
        var path = new MouseTrajectoryStep([new(0, 0, 0), new(80, 20, 10)], TimeSpan.FromMilliseconds(200))
        { TimestampIsStart = timestampIsStart };
        var clone = (MouseTrajectoryStep)MacroActionFactory.Clone(path);
        var edited = (MouseTrajectoryStep)MacroActionFactory.UpdateTrajectory(path, 125, 2, 2, path.Mode);
        var serializer = new MacroJsonSerializer();
        var loaded = (MouseTrajectoryStep)serializer.Deserialize(serializer.Serialize(new MacroModel { Steps = [edited] })).Steps[0];

        clone.TimestampIsStart.Should().Be(timestampIsStart);
        loaded.TimestampIsStart.Should().Be(timestampIsStart);
        clone.RelativeTimestamp.Should().Be(path.RelativeTimestamp);
        loaded.RelativeTimestamp.Should().Be(path.RelativeTimestamp);
    }

    [Theory]
    [InlineData(false, 120)]
    [InlineData(true, 200)]
    public void ConvertingPathToSweep_KeepsRecordedStart(bool timestampIsStart, int expectedStart)
    {
        var path = new MouseTrajectoryStep([new(0, 0, 0), new(80, 20, 10)], TimeSpan.FromMilliseconds(200))
        { TimestampIsStart = timestampIsStart };

        var sweep = (MouseSweepStep)MacroActionFactory.Convert(path, EditableActionKind.MouseSweep);
        var restored = (MouseTrajectoryStep)MacroActionFactory.Convert(sweep, EditableActionKind.MouseTrajectory);

        sweep.RelativeTimestamp.Should().Be(TimeSpan.FromMilliseconds(expectedStart));
        restored.RelativeTimestamp.Should().Be(sweep.RelativeTimestamp);
        restored.TimestampIsStart.Should().BeTrue();
    }
}
