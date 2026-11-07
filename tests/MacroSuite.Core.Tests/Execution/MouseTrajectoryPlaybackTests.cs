using FluentAssertions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using Xunit;

namespace MacroSuite.Core.Tests.Execution;

public sealed class MouseTrajectoryPlaybackTests
{
    [Fact]
    public void Interpolate_Midpoint_IsLinear()
    {
        MouseTrajectorySample[] samples =
        [
            new(0, 0, 0),
            new(10, -10, 0),
            new(20, -20, -4)
        ];

        MouseTrajectoryPlayback.Interpolate(samples, 15, out double x, out double y);
        x.Should().BeApproximately(-15, 0.001);
        y.Should().BeApproximately(-2, 0.001);
    }

    [Fact]
    public void AccumulateDelta_KeepsFractionalRemainder()
    {
        double prevX = 0, prevY = 0, remX = 0, remY = 0;
        MouseTrajectoryPlayback.AccumulateDelta(0.4, 0, ref prevX, ref prevY, ref remX, ref remY, out int a, out _);
        MouseTrajectoryPlayback.AccumulateDelta(0.8, 0, ref prevX, ref prevY, ref remX, ref remY, out int b, out _);
        MouseTrajectoryPlayback.AccumulateDelta(1.0, 0, ref prevX, ref prevY, ref remX, ref remY, out int c, out _);
        (a + b + c).Should().Be(1);
    }

    [Fact]
    public void Build_PreservesPauseInTheMiddle()
    {
        var raw = new List<MouseTrajectorySample>
        {
            new(0, 0, 0),
            new(10, -20, 0),
            new(200, -20, 0),
            new(210, -40, 0)
        };

        var built = MouseTrajectoryPlayback.Build(raw, simplifyEpsilon: 1.0f);
        built.Length.Should().BeGreaterThanOrEqualTo(3);
        MouseTrajectoryPlayback.Interpolate(built, 100, out double x, out double y);
        x.Should().BeApproximately(-20, 0.6);
        y.Should().BeApproximately(0, 0.01);
    }

    [Fact]
    public void Build_KeepsFinalDisplacement()
    {
        var raw = new List<MouseTrajectorySample>
        {
            new(0, 0, 0),
            new(8, -3, 0),
            new(16, -7, 0),
            new(25, -12, -1),
            new(33, -16, -1),
            new(42, -19, -1)
        };

        var built = MouseTrajectoryPlayback.Build(raw);
        MouseTrajectoryPlayback.NetDisplacement(built, out int dx, out int dy);
        dx.Should().Be(-19);
        dy.Should().Be(-1);
    }

    [Fact]
    public void Stabilize_LookUp_DropsOscillatingX()
    {
        MouseTrajectorySample[] raw =
        [
            new(0, 0, 0),
            new(8, 3, -5),
            new(16, 0, -10),
            new(24, 3, -16),
            new(32, 0, -22),
            new(40, 2, -28)
        ];

        var clean = MouseTrajectoryPlayback.Stabilize(raw);
        MouseTrajectoryPlayback.ClassifyAxes(clean, out bool dropX, out bool dropY);
        dropX.Should().BeTrue();
        dropY.Should().BeFalse();
        MouseTrajectoryPlayback.NetDisplacement(clean, out int dx, out int dy);
        dx.Should().Be(0);
        dy.Should().Be(-28);
    }

    [Fact]
    public void ClassifyAxes_LookUpWithSnapBack_KeepsY()
    {
        MouseTrajectorySample[] raw =
        [
            new(0, 0, 0),
            new(8, 3, -12),
            new(16, -2, -4),
            new(24, 4, -20),
            new(32, 1, -6)
        ];

        MouseTrajectoryPlayback.ClassifyAxes(raw, out bool dropX, out bool dropY);
        dropX.Should().BeTrue();
        dropY.Should().BeFalse();
        MouseTrajectoryPlayback.HasMeaningfulLook(raw).Should().BeTrue();
    }

    [Fact]
    public void MeasureLook_UsesPeakYWhenSnapBackShrinksNet()
    {
        MouseTrajectorySample[] raw =
        [
            new(0, 0, 0),
            new(8, 2, -12),
            new(16, 0, -4),
            new(24, 3, -18),
            new(32, 1, -6)
        ];

        MouseTrajectoryPlayback.MeasureLook(raw, out _, out float netY, out _, out float peakY);
        netY.Should().Be(-6);
        peakY.Should().Be(-18);
    }

    [Fact]
    public void ClassifyLook_LShapeHold_IsOmnidirectional()
    {
        MouseTrajectorySample[] raw =
        [
            new(0, 0, 0),
            new(16, 24, 0),
            new(32, 40, -2),
            new(48, 40, -18),
            new(64, 38, -30)
        ];

        var kind = MouseTrajectoryPlayback.ClassifyLook(raw, out bool dropX, out bool dropY);
        kind.Should().Be(CameraLookKind.Omnidirectional);
        dropX.Should().BeFalse();
        dropY.Should().BeFalse();
        var kept = MouseTrajectoryPlayback.Stabilize(raw);
        MouseTrajectoryPlayback.NetDisplacement(kept, out int dx, out int dy);
        dx.Should().Be(38);
        dy.Should().Be(-30);
    }

    [Fact]
    public void ClassifyLook_ReversingYawAndPitch_IsOmnidirectional()
    {
        MouseTrajectorySample[] raw =
        [
            new(0, 0, 0),
            new(12, 20, 0),
            new(24, 20, -18),
            new(36, 4, -18),
            new(48, 4, -2)
        ];

        var kind = MouseTrajectoryPlayback.ClassifyLook(raw, out bool dropX, out bool dropY);
        kind.Should().Be(CameraLookKind.Omnidirectional);
        dropX.Should().BeFalse();
        dropY.Should().BeFalse();
    }

    [Fact]
    public void CleanMixedPath_SpinThenFlip_StripsIdleAxisJitter()
    {
        MouseTrajectorySample[] raw =
        [
            new(0, 0, 0),
            new(10, 20, 2),
            new(20, 40, -1),
            new(30, 60, 2),
            new(40, 80, 0),
            new(50, 82, -12),
            new(60, 79, -24),
            new(70, 81, -36)
        ];

        var clean = MouseTrajectoryPlayback.CleanMixedPath(raw);
        MouseTrajectoryPlayback.Interpolate(clean, 30, out double spinX, out double spinY);
        spinX.Should().BeGreaterThan(50);
        Math.Abs(spinY).Should().BeLessThan(3);
        MouseTrajectoryPlayback.Interpolate(clean, 70, out double flipX, out double flipY);
        flipX.Should().BeApproximately(80, 6);
        flipY.Should().BeApproximately(-36, 4);
    }

    [Fact]
    public void SegmentPhases_SpinThenFlip_IsHorizontalThenVertical()
    {
        MouseTrajectorySample[] raw =
        [
            new(0, 0, 0),
            new(20, 40, 0),
            new(40, 80, 0),
            new(60, 80, -16),
            new(80, 80, -32)
        ];

        var clean = MouseTrajectoryPlayback.CleanMixedPath(raw);
        var phases = MouseTrajectoryPlayback.SegmentPhases(clean);
        MouseTrajectoryPlayback.HasSequentialAxes(phases).Should().BeTrue();
        phases[0].Kind.Should().Be(MouseTrajectoryPlayback.LookPhaseKind.Horizontal);
        phases[^1].Kind.Should().Be(MouseTrajectoryPlayback.LookPhaseKind.Vertical);

        MouseTrajectoryPlayback.EvaluatePhased(clean, phases, 20, out double spinX, out double spinY);
        spinX.Should().BeApproximately(40, 1);
        spinY.Should().BeApproximately(0, 0.01);

        MouseTrajectoryPlayback.EvaluatePhased(clean, phases, 80, out double flipX, out double flipY);
        flipX.Should().BeApproximately(80, 1);
        flipY.Should().BeApproximately(-32, 1);

        var polished = MouseTrajectoryPlayback.SmoothPhaseInteriors(clean, phases);
        MouseTrajectoryPlayback.NetDisplacement(polished, out int dx, out int dy);
        dx.Should().Be(80);
        dy.Should().Be(-32);
    }

    [Fact]
    public void CleanMixedPath_DownWhileSpinning_KeepsPitch()
    {
        MouseTrajectorySample[] raw =
        [
            new(0, 0, 0),
            new(10, 2, -16),
            new(20, 4, -28),
            new(30, 20, -24),
            new(40, 40, -16),
            new(50, 60, -6)
        ];

        var clean = MouseTrajectoryPlayback.CleanMixedPath(raw);
        MouseTrajectoryPlayback.AxisReturns(clean, vertical: true).Should().BeTrue();
        MouseTrajectoryPlayback.Interpolate(clean, 50, out double x, out double y);
        x.Should().BeGreaterThan(40);
        y.Should().BeApproximately(-6, 4);
        Math.Abs(y).Should().BeLessThan(20);
    }

    [Fact]
    public void ShouldFollowRecordedPath_WhenPitchReturns()
    {
        MouseTrajectorySample[] raw =
        [
            new(0, 0, 0),
            new(16, 0, -24),
            new(32, 12, -24),
            new(48, 28, -8),
            new(64, 40, -2)
        ];

        var clean = MouseTrajectoryPlayback.CleanMixedPath(raw);
        var phases = MouseTrajectoryPlayback.SegmentPhases(clean);
        var kind = MouseTrajectoryPlayback.ClassifyLook(clean, out _, out _);
        MouseTrajectoryPlayback.ShouldFollowRecordedPath(kind, clean, phases).Should().BeTrue();
    }

    [Fact]
    public void Stabilize_KeepsTrueDiagonal()
    {
        MouseTrajectorySample[] raw =
        [
            new(0, 0, 0),
            new(10, -10, -10),
            new(20, -20, -20),
            new(30, -30, -30)
        ];

        var clean = MouseTrajectoryPlayback.Stabilize(raw);
        MouseTrajectoryPlayback.ClassifyAxes(clean, out bool dropX, out bool dropY);
        dropX.Should().BeFalse();
        dropY.Should().BeFalse();
        MouseTrajectoryPlayback.NetDisplacement(clean, out int dx, out int dy);
        dx.Should().Be(-30);
        dy.Should().Be(-30);
    }

    [Fact]
    public void MouseTrajectory_RoundTrip_PreservesSamples()
    {
        var serializer = new MacroJsonSerializer();
        var step = new MouseTrajectoryStep(
        [
            new MouseTrajectorySample(0, 0, 0),
            new MouseTrajectorySample(8, -3, 0),
            new MouseTrajectorySample(16, -7, -1)
        ])
        {
            PlaybackHz = 125
        };

        var model = new MacroModel { Name = "Path", Steps = [step] };
        var loaded = serializer.Deserialize(serializer.Serialize(model));
        var path = loaded.Steps[0].Should().BeOfType<MouseTrajectoryStep>().Subject;
        path.PlaybackHz.Should().Be(125);
        path.SensitivityY.Should().BeApproximately(MouseTrajectoryStep.DefaultLookGainY, 0.001);
        path.Samples.Should().HaveCount(3);
        path.Samples[2].X.Should().BeApproximately(-7, 0.01f);
        path.Samples[2].Y.Should().BeApproximately(-1, 0.01f);
    }

    [Fact]
    public void Normalize_KeepsShallowDiagonal()
    {
        MouseTrajectorySample[] raw =
        [
            new(0, 0, 0),
            new(20, 50, 2),
            new(40, 100, 5)
        ];

        var built = MouseTrajectoryPlayback.Normalize(raw);
        MouseTrajectoryPlayback.NetDisplacement(built, out int dx, out int dy);
        dx.Should().Be(100);
        dy.Should().Be(5);
    }

    [Fact]
    public void RecordedTrajectory_LookUpThenDown_DoesNotStayAtPeak()
    {
        MouseTrajectorySample[] raw =
        [
            new(0, 0, 0),
            new(20, 4, -24),
            new(40, 20, -24),
            new(60, 40, -6)
        ];

        var path = RecordedTrajectory.FromSamples(raw, TrajectoryGapPolicy.Interpolate);
        var mid = path.EvaluateMs(20);
        var end = path.EvaluateMs(60);
        mid.Y.Should().BeApproximately(-24, 0.01);
        end.Y.Should().BeApproximately(-6, 0.01);
        end.X.Should().BeApproximately(40, 0.01);
    }

    [Fact]
    public void PacketsFromSamples_PreservesExcursions()
    {
        MouseTrajectorySample[] raw =
        [
            new(0, 0, 0),
            new(10, 20, 0),
            new(20, 0, 0)
        ];

        var packets = MouseTrajectoryPlayback.PacketsFromSamples(raw);
        packets.Should().HaveCount(2);
        (packets[0].Dx + packets[1].Dx).Should().Be(0);
        Math.Abs(packets[0].Dx).Should().Be(20);
    }

    [Fact]
    public void MouseTrajectory_RoundTrip_PreservesModeAndPackets()
    {
        var serializer = new MacroJsonSerializer();
        var step = new MouseTrajectoryStep(
        [
            new MouseTrajectorySample(0, 0, 0),
            new MouseTrajectorySample(16, 12, 4)
        ])
        {
            Mode = TrajectoryPlaybackMode.PacketReference,
            Packets = [new MouseTrajectoryPacket(16, 12, 4, 0)]
        };

        var loaded = serializer.Deserialize(serializer.Serialize(new MacroModel { Name = "P", Steps = [step] }));
        var path = loaded.Steps[0].Should().BeOfType<MouseTrajectoryStep>().Subject;
        path.Mode.Should().Be(TrajectoryPlaybackMode.PacketReference);
        path.Packets.Should().NotBeNull();
        path.Packets![0].Dx.Should().Be(12);
    }

    [Fact]
    public void SmoothKnotsBounded_KeepsEndpointsAndLimitsError()
    {
        MouseTrajectorySample[] raw =
        [
            new(0, 0, 0),
            new(10, 10, 0),
            new(20, 12, 6),
            new(30, 30, 0)
        ];

        var smooth = RecordedTrajectory.SmoothKnotsBounded(raw, 1.5f);
        smooth[0].X.Should().Be(0);
        smooth[^1].X.Should().Be(30);
        smooth[2].Y.Should().Be(6);
        Math.Abs(smooth[2].X - 12).Should().BeLessThanOrEqualTo(1.5f);
    }

    [Fact]
    public void ResolveGains_FourAndEight_BecomesOne()
    {
        var step = new MouseTrajectoryStep([new(0, 0, 0), new(10, 10, 0)])
        {
            Sensitivity = 4.0,
            SensitivityY = 8.0
        };
        step.ResolveGains(out double gx, out double gy);
        gx.Should().Be(1.0);
        gy.Should().Be(1.0);
    }

    [Fact]
    public void ResolveGains_OneAndOne_Kept()
    {
        var step = new MouseTrajectoryStep([new(0, 0, 0), new(10, 10, 0)])
        {
            Sensitivity = 1.0,
            SensitivityY = 1.0
        };
        step.ResolveGains(out double gx, out double gy);
        gx.Should().Be(1.0);
        gy.Should().Be(1.0);
    }

    [Fact]
    public void Prepare_TwoSpins_KeepsPathLength()
    {
        var raw = new List<MouseTrajectorySample> { new(0, 0, 0) };
        for (int i = 1; i <= 80; i++)
        {
            double a = i * (Math.PI * 4 / 80);
            raw.Add(new MouseTrajectorySample(i * 8, (float)(Math.Sin(a) * 40), 0));
        }

        for (int i = 1; i <= 20; i++)
            raw.Add(new MouseTrajectorySample(640 + i * 16, 0, i * 1.2f));

        double before = LookPathSmoother.PathLength(raw);
        var path = LookPathSmoother.Prepare(raw);
        LookPathSmoother.PathLength(path).Should().BeGreaterThan(before * 0.85);
        path.Length.Should().BeGreaterThan(60);
    }

    [Fact]
    public void RemoveLockSpikes_DropsTinyReversal_KeepsLookDownPeak()
    {
        MouseTrajectorySample[] raw =
        [
            new(0, 0, 0),
            new(8, 1, 0),
            new(16, 0, 0),
            new(40, 4, 20),
            new(60, 10, 6)
        ];

        var clean = LookPathSmoother.RemoveLockSpikes(raw);
        clean.Should().HaveCount(4);
        clean[2].Y.Should().Be(20);
    }

    [Fact]
    public void Prepare_KeepsLookDownThenUp()
    {
        MouseTrajectorySample[] raw =
        [
            new(0, 0, 0),
            new(20, 2, 16),
            new(40, 4, 28),
            new(60, 6, 16),
            new(80, 8, 2)
        ];

        var path = LookPathSmoother.Prepare(raw);
        float peak = 0;
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i].Y > peak)
                peak = path[i].Y;
        }

        peak.Should().BeGreaterThan(20);
        path[^1].Y.Should().BeApproximately(2, 2);
    }

    [Fact]
    public void Prepare_KeepsYawReverseSecondHalf()
    {
        var raw = new List<MouseTrajectorySample> { new(0, 0, 0) };
        for (int i = 1; i <= 20; i++)
            raw.Add(new MouseTrajectorySample(i * 8, i * 2f, 0));
        for (int i = 1; i <= 20; i++)
            raw.Add(new MouseTrajectorySample(160 + i * 8, 40 - i * 2f, 0));

        var path = LookPathSmoother.Prepare(raw);
        float maxX = 0;
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i].X > maxX)
                maxX = path[i].X;
        }

        maxX.Should().BeGreaterThan(30);
        path[^1].X.Should().BeApproximately(0, 4);
    }

    [Fact]
    public void MeasureExtents_KeepsBothDirectionsOnEachAxis()
    {
        MouseTrajectorySample[] raw =
        [
            new(0, 0, 0),
            new(10, 40, -20),
            new(20, -25, 30),
            new(30, 5, -8)
        ];

        MouseTrajectoryPlayback.MeasureExtents(raw, out float minX, out float maxX, out float minY, out float maxY);
        minX.Should().Be(-25);
        maxX.Should().Be(40);
        minY.Should().Be(-20);
        maxY.Should().Be(30);
        MouseTrajectoryPlayback.FormatExtent(minX, maxX).Should().Be("-25/+40");
    }

    [Fact]
    public void ScaleByExtents_ScalesEachSignedSideIndependently()
    {
        MouseTrajectorySample[] raw =
        [
            new(0, 0, 0),
            new(10, 20, 10),
            new(20, -10, -8),
            new(30, 0, 0)
        ];

        var scaled = MouseTrajectoryPlayback.ScaleByExtents(raw, -20, 40, -8, 10);
        ((double)scaled[1].X).Should().BeApproximately(40, 0.01);
        ((double)scaled[1].Y).Should().BeApproximately(10, 0.01);
        ((double)scaled[2].X).Should().BeApproximately(-20, 0.01);
        ((double)scaled[2].Y).Should().BeApproximately(-8, 0.01);
    }

    [Fact]
    public void ScaleByExtents_LeavesMissingDirectionUnchanged()
    {
        MouseTrajectorySample[] raw =
        [
            new(0, 0, 0),
            new(10, 0, 20),
            new(20, 0, 0)
        ];

        var scaled = MouseTrajectoryPlayback.ScaleByExtents(raw, -40, 40, 0, 20);
        scaled[1].X.Should().Be(0);
        ((double)scaled[1].Y).Should().BeApproximately(20, 0.01);
    }

    [Fact]
    public void ClampLookGain_CapsAtTen()
    {
        MouseTrajectoryStep.ClampLookGain(16).Should().Be(10);
        MouseTrajectoryStep.ClampLookGain(3).Should().Be(3);
        MouseTrajectoryStep.ClampLookGain(0).Should().Be(1);
    }

    [Fact]
    public void IsCursorSnapBack_IncludesOneAndTwoPixelRecenter()
    {
        MouseTrajectoryPlayback.IsCursorSnapBack(1, 0).Should().BeTrue();
        MouseTrajectoryPlayback.IsCursorSnapBack(2, 0).Should().BeTrue();
        MouseTrajectoryPlayback.IsCursorSnapBack(2, 1).Should().BeTrue();
        MouseTrajectoryPlayback.IsCursorSnapBack(8, 1).Should().BeTrue();
        MouseTrajectoryPlayback.IsCursorSnapBack(0, 1).Should().BeFalse();
        MouseTrajectoryPlayback.IsCursorSnapBack(2, 2).Should().BeFalse();
        MouseTrajectoryPlayback.IsCursorSnapBack(8, 8).Should().BeFalse();
    }

    [Fact]
    public void Prepare_DampsLockChatterOnIdleAxis()
    {
        MouseTrajectorySample[] raw =
        [
            new(0, 0, 0),
            new(8, 8, 2),
            new(16, 16, -2),
            new(24, 24, 2),
            new(32, 32, -2),
            new(40, 40, 0)
        ];

        var path = LookPathSmoother.Prepare(raw);
        float maxAbsY = 0;
        for (int i = 0; i < path.Length; i++)
        {
            float ay = Math.Abs(path[i].Y);
            if (ay > maxAbsY)
                maxAbsY = ay;
        }

        maxAbsY.Should().BeLessThan(2f);
        path[^1].X.Should().BeApproximately(40, 2);
    }

    [Fact]
    public void SplitStep_KeepsDirectionWhenCapped()
    {
        LookPathSmoother.SplitStep(40, -10, 12, out int sx, out int sy);
        Math.Abs(sx).Should().Be(12);
        sy.Should().Be(-3);
        (sx > 0 && sy < 0).Should().BeTrue();
    }

    [Fact]
    public void AccumulateDelta_RawSamples_StayRelativeAndSumToNet()
    {
        MouseTrajectorySample[] samples =
        [
            new(0, 0, 0),
            new(10, 12, -4),
            new(20, 12, -20),
            new(30, 4, -8)
        ];

        double prevX = 0, prevY = 0, remX = 0, remY = 0;
        int sx = 0, sy = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            MouseTrajectoryPlayback.AccumulateDelta(
                samples[i].X, samples[i].Y,
                ref prevX, ref prevY, ref remX, ref remY,
                out int mx, out int my);
            sx += mx;
            sy += my;
        }

        sx.Should().Be(4);
        sy.Should().Be(-8);
    }

    [Fact]
    public void BuildEmitTimes_IncludesLookDownPeak()
    {
        MouseTrajectorySample[] knots =
        [
            new(0, 0, 0),
            new(20, 4, 24),
            new(40, 20, 24),
            new(60, 40, 6)
        ];

        var times = RecordedTrajectory.BuildEmitTimesMs(knots, 125);
        times.Should().Contain(20);
        var path = new RecordedTrajectory(knots);
        path.EvaluateMs(20).Y.Should().BeApproximately(24, 0.01);
    }
}
