using FluentAssertions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;
using Xunit;

namespace MacroSuite.Core.Tests.Execution;

public sealed class MouseSweepMotionTests
{
    [Fact]
    public void Sample_IntegerDestination_EndsExactlyAtT1()
    {
        const int destX = -127;
        const int destY = 12;
        double lastX = 0;
        double lastY = 0;
        double fracX = 0;
        double fracY = 0;
        int sentX = 0;
        int sentY = 0;

        for (int i = 1; i <= 240; i++)
        {
            double t = i / 240.0;
            double progress = MouseSweepMotion.Progress(t, MouseInterpolation.SmootherStep, 20);
            MouseSweepMotion.Sample(destX, destY, progress, ref lastX, ref lastY, ref fracX, ref fracY, out int mx, out int my);
            sentX += mx;
            sentY += my;
        }

        sentX.Should().Be(destX);
        sentY.Should().Be(destY);
        Math.Abs(fracX).Should().BeLessThan(1);
        Math.Abs(fracY).Should().BeLessThan(1);
    }

    [Fact]
    public void Sample_DoesNotRoundTinyFractionsAway()
    {
        double lastX = 0, lastY = 0, fracX = 0, fracY = 0;
        MouseSweepMotion.Sample(1, 0, 0.4, ref lastX, ref lastY, ref fracX, ref fracY, out int a, out _);
        MouseSweepMotion.Sample(1, 0, 0.8, ref lastX, ref lastY, ref fracX, ref fracY, out int b, out _);
        MouseSweepMotion.Sample(1, 0, 1.0, ref lastX, ref lastY, ref fracX, ref fracY, out int c, out _);
        (a + b + c).Should().Be(1);
    }

    [Fact]
    public void Sample_ZeroY_NeverEmitsVertical()
    {
        double lastX = 0, lastY = 0, fracX = 0, fracY = 0;
        int sentY = 0;
        for (int i = 1; i <= 120; i++)
        {
            double progress = MouseSweepMotion.Progress(i / 120.0, MouseInterpolation.SmootherStep, 20);
            MouseSweepMotion.Sample(-300, 0, progress, ref lastX, ref lastY, ref fracX, ref fracY, out _, out int my);
            sentY += my;
        }

        sentY.Should().Be(0);
    }

    [Fact]
    public void ApplyAxisDeadzone_SuppressesTinyVerticalNoise()
    {
        int x = -300;
        int y = 2;
        MouseSweepMotion.ApplyAxisDeadzone(ref x, ref y, filterVertical: true, filterHorizontal: false);
        x.Should().Be(-300);
        y.Should().Be(0);
    }

    [Fact]
    public void ApplyAxisDeadzone_KeepsRealVerticalLook()
    {
        int x = -300;
        int y = 80;
        MouseSweepMotion.ApplyAxisDeadzone(ref x, ref y, filterVertical: true, filterHorizontal: false);
        x.Should().Be(-300);
        y.Should().Be(80);
    }

    [Theory]
    [InlineData(0, -8)]
    [InlineData(2, -8)]
    [InlineData(-20, -6)]
    [InlineData(-40, -3)]
    [InlineData(1, 2)]
    public void ApplyAxisDeadzone_KeepsSlowVerticalLook(int x0, int y0)
    {
        int x = x0;
        int y = y0;
        MouseSweepMotion.ApplyAxisDeadzone(ref x, ref y, filterVertical: true, filterHorizontal: false);
        x.Should().Be(x0);
        y.Should().Be(y0);
    }

    [Fact]
    public void ApplyAxisDeadzone_OffDoesNotChangeY()
    {
        int x = -300;
        int y = 2;
        MouseSweepMotion.ApplyAxisDeadzone(ref x, ref y, filterVertical: false, filterHorizontal: false);
        y.Should().Be(2);
    }

    [Theory]
    [InlineData(MouseInterpolation.Linear, 0)]
    [InlineData(MouseInterpolation.Smooth, 20)]
    [InlineData(MouseInterpolation.EaseInOut, 70)]
    [InlineData(MouseInterpolation.Bezier, 25)]
    [InlineData(MouseInterpolation.SmootherStep, 75)]
    public void Progress_StartsAtZeroAndEndsAtOne(MouseInterpolation interpolation, int smoothing)
    {
        MouseSweepMotion.Progress(0, interpolation, smoothing).Should().Be(0);
        MouseSweepMotion.Progress(1, interpolation, smoothing).Should().Be(1);
    }

    [Fact]
    public void ResolveDestination_UsesLegacySharedSensitivity()
    {
        MouseSweepMotion.ResolveDestination(-100, 10, sensitivity: 2.0, sensitivityX: 1.0, sensitivityY: 1.0, out int x, out int y);
        x.Should().Be(-200);
        y.Should().Be(20);
    }

    [Fact]
    public void ResolveDestination_UsesIndependentAxes()
    {
        MouseSweepMotion.ResolveDestination(-100, 10, sensitivity: 1.0, sensitivityX: 0.5, sensitivityY: 2.0, out int x, out int y);
        x.Should().Be(-50);
        y.Should().Be(20);
    }

    [Fact]
    public void InferProfile_RecognizesPresets()
    {
        MouseSweepMotion.InferProfile(240, MouseInterpolation.Linear, 0, 0, true).Should().Be(MouseSweepProfile.Precise);
        MouseSweepMotion.InferProfile(240, MouseInterpolation.SmootherStep, 20, 75, true).Should().Be(MouseSweepProfile.Smooth);
        MouseSweepMotion.InferProfile(120, MouseInterpolation.SmootherStep, 35, 80, true).Should().Be(MouseSweepProfile.Cinematic);
        MouseSweepMotion.InferProfile(144, MouseInterpolation.Bezier, 10, 10, true).Should().Be(MouseSweepProfile.Custom);
        MouseSweepMotion.InferProfile(240, MouseInterpolation.SmootherStep, 20, 75, false).Should().Be(MouseSweepProfile.Custom);
    }
}
