using FluentAssertions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Vision;
using Xunit;

namespace MacroSuite.Core.Tests.Execution;

public sealed class ExecutionStatusSinkTests
{
    [Fact]
    public void TryWrite_DropsOldestWhenFull()
    {
        var sink = new BoundedExecutionStatusSink(capacity: 2);
        sink.TryWrite(new ExecutionStatusSnapshot { Phase = "one", Checks = 1 }).Should().BeTrue();
        sink.TryWrite(new ExecutionStatusSnapshot { Phase = "two", Checks = 2 }).Should().BeTrue();
        sink.TryWrite(new ExecutionStatusSnapshot { Phase = "three", Checks = 3 }).Should().BeTrue();
        sink.Complete();

        sink.Reader.TryRead(out ExecutionStatusSnapshot? first).Should().BeTrue();
        first!.Phase.Should().Be("two");
        sink.Reader.TryRead(out ExecutionStatusSnapshot? second).Should().BeTrue();
        second!.Phase.Should().Be("three");
        sink.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public void ColorDetectionResult_CarriesCaptureMetadata()
    {
        var result = new ColorDetectionResult
        {
            CaptureSource = MacroSuite.Core.Models.CaptureSource.Gdi,
            FrameWidth = 80,
            FrameHeight = 40,
            DetectionMethod = "Hsv"
        };
        result.CaptureSource.Should().Be(MacroSuite.Core.Models.CaptureSource.Gdi);
        result.FrameWidth.Should().Be(80);
        result.FrameHeight.Should().Be(40);
    }
}
