using System.Diagnostics;
using System.Text.Json.Serialization;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Steps;

/// <summary>
/// Recorded camera path. Surrounding Down / Up still own the hold.
/// Playback sends relative look packets from the recorded path.
/// Lock jitter is filtered; the 2D shape is not classified or replaced.
/// </summary>
public sealed class MouseTrajectoryStep : MacroStep
{
    public MouseTrajectorySample[] Samples { get; init; } = [];
    public MouseTrajectoryPacket[]? Packets { get; init; }
    public TrajectoryButtonTransition[] ButtonTransitions { get; init; } = [];
    public const double DefaultLookGainX = 1.0;
    public const double DefaultLookGainY = 1.0;
    public const double MinLookGain = 0.25;
    public const double MaxLookGain = 10.0;

    public static double ClampLookGain(double value)
        => !double.IsFinite(value) || value < 0.05 ? DefaultLookGainX : Math.Clamp(value, MinLookGain, MaxLookGain);

    public int PlaybackHz { get; init; } = MouseTrajectoryPlayback.DefaultHz;
    public double Sensitivity { get; init; } = DefaultLookGainX;
    public double SensitivityY { get; init; } = DefaultLookGainY;
    public TrajectoryPlaybackMode Mode { get; init; } = TrajectoryPlaybackMode.Reconstructed;
    public TrajectoryGapPolicy GapPolicy { get; init; } = TrajectoryGapPolicy.PreservePause;
    public double ReconstructionTolerancePx { get; init; } = 1.5;
    public TimeSpan RelativeTimestamp { get; init; } = TimeSpan.Zero;
    /// <summary>New recordings store the path start; older files stored its end.</summary>
    public bool TimestampIsStart { get; init; }

    [JsonIgnore]
    public TimeSpan RecordedDuration => Packets is { Length: > 0 }
        ? TimeSpan.FromTicks(Packets[^1].OffsetTicks ?? (long)Packets[^1].T * TimeSpan.TicksPerMillisecond)
        : TimeSpan.FromMilliseconds(DurationMs);

    [JsonIgnore]
    public int DurationMs => MouseTrajectoryPlayback.DurationMs(Samples);

    [JsonIgnore]
    private RecordedTrajectory? _reconstructed;

    private sealed record PreparedPlayback(MouseTrajectorySample[] Source,
        MouseTrajectorySample[] Samples, int Hz, int[] EmitTimes);
    private PreparedPlayback? _prepared;

    private PreparedPlayback PreparePlayback()
    {
        int hz = MouseTrajectoryPlayback.ClampHz(PlaybackHz);
        var cached = Volatile.Read(ref _prepared);
        // Samples is publicly mutable. Check its values before reusing a plan.
        if (cached is not null && cached.Hz == hz && SamplesMatch(cached.Source))
            return cached;
        var source = Samples.ToArray();
        var samples = LookPathSmoother.Prepare(source);
        var prepared = new PreparedPlayback(source, samples, hz, RecordedTrajectory.BuildEmitTimesMs(samples, hz));
        Volatile.Write(ref _prepared, prepared);
        return prepared;
    }

    private bool SamplesMatch(MouseTrajectorySample[] source)
    {
        if (Samples.Length != source.Length) return false;
        for (int i = 0; i < source.Length; i++)
        {
            if (Samples[i].T != source[i].T || Samples[i].X != source[i].X || Samples[i].Y != source[i].Y)
                return false;
        }
        return true;
    }

    public MouseTrajectoryStep()
    {
        Label = "Camera Path";
    }

    public MouseTrajectoryStep(MouseTrajectorySample[] samples, TimeSpan relativeTimestamp = default)
    {
        Samples = samples ?? [];
        RelativeTimestamp = relativeTimestamp;
        MouseTrajectoryPlayback.MeasureExtents(Samples, out float minX, out float maxX, out float minY, out float maxY);
        Label = $"Path {Samples.Length}pt x={MouseTrajectoryPlayback.FormatExtent(minX, maxX)} y={MouseTrajectoryPlayback.FormatExtent(minY, maxY)} {DurationMs}ms";
    }

    public override Task ExecuteAsync(MacroContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsEnabled || context.IsAborted || ct.IsCancellationRequested)
            return Task.CompletedTask;
        if (context.InputPlayer is null || Samples is null || Samples.Length < 2)
            return Task.CompletedTask;

        if (Mode == TrajectoryPlaybackMode.PacketReference)
        {
            PlayPackets(context, context.InputPlayer, ct);
            return Task.CompletedTask;
        }

        PlayPath(context, context.InputPlayer, ct);
        return Task.CompletedTask;
    }

    internal RecordedTrajectory Trajectory
        => _reconstructed ??= RecordedTrajectory.FromSamples(Samples, GapPolicy);

    private void PlayPath(MacroContext context, IInputPlayer player, CancellationToken ct)
    {
        var prepared = PreparePlayback();
        MouseTrajectorySample[] samples = prepared.Samples;
        if (samples.Length < 2)
            return;

        ResolveGains(out double gainX, out double gainY);
        int recordedDurationMs = MouseTrajectoryPlayback.DurationMs(samples);
        TimeSpan scaled = StepTimeline.Scale(TimeSpan.FromMilliseconds(recordedDurationMs), context.PlaybackSpeed);
        double durationMs = scaled.TotalMilliseconds;
        double timeScale = recordedDurationMs <= 0 || durationMs <= 0 ? 1 : recordedDurationMs / durationMs;

        int[] emitAt = prepared.EmitTimes;
        if (emitAt.Length == 0)
            return;

        using IDisposable? period = context.TimerResolution?.Request(1);
        bool highRes = period != null;
        long freq = Stopwatch.Frequency;
        long start = context.PlaybackSession?.ActiveTimestamp ?? Stopwatch.GetTimestamp();

        double prevX = 0;
        double prevY = 0;
        double remX = 0;
        double remY = 0;

        for (int i = 0; i < emitAt.Length; i++)
        {
            if (context.IsAborted || ct.IsCancellationRequested)
                return;

            double playMs = emitAt[i] / timeScale;
            if (!HighResolutionScheduler.WaitForTimeline(
                ref start, (long)(playMs / 1000.0 * freq), highRes, context, ct))
                return;

            MouseTrajectoryPlayback.Interpolate(samples, emitAt[i], out double x, out double y);
            MouseTrajectoryPlayback.AccumulateDelta(
                x * gainX,
                y * gainY,
                ref prevX,
                ref prevY,
                ref remX,
                ref remY,
                out int mx,
                out int my);
            LookPathSmoother.SendCapped(player, mx, my, context, ct);
        }
    }

    private void PlayPackets(MacroContext context, IInputPlayer player, CancellationToken ct)
    {
        MouseTrajectoryPacket[] packets = Packets is { Length: > 0 }
            ? Packets
            : MouseTrajectoryPlayback.PacketsFromSamples(Samples);
        if (packets.Length == 0)
        {
            PlayPath(context, player, ct);
            return;
        }

        // Validate the whole trace before emitting any packet. Do not normalize away
        // equal-time reversals or resample a faithful packet reference.
        long previousOffset = -1;
        for (int i = 0; i < packets.Length; i++)
        {
            long offset = packets[i].OffsetTicks ?? (long)packets[i].T * TimeSpan.TicksPerMillisecond;
            if (offset < 0 || offset < previousOffset)
                throw new InvalidOperationException("Camera packet timestamps must be nonnegative and in recording order.");
            previousOffset = offset;
        }

        using IDisposable? period = context.TimerResolution?.Request(1);
        bool highRes = period != null;
        long freq = Stopwatch.Frequency;
        long start = context.PlaybackSession?.ActiveTimestamp ?? Stopwatch.GetTimestamp();
        ResolveGains(out double gainX, out double gainY);
        double prevX = 0;
        double prevY = 0;
        double remX = 0;
        double remY = 0;
        double cumX = 0;
        double cumY = 0;

        for (int i = 0; i < packets.Length; i++)
        {
            if (context.IsAborted || ct.IsCancellationRequested)
                return;

            long offset = packets[i].OffsetTicks ?? (long)packets[i].T * TimeSpan.TicksPerMillisecond;
            double playMs = StepTimeline.Scale(TimeSpan.FromTicks(offset), context.PlaybackSpeed).TotalMilliseconds;
            if (!HighResolutionScheduler.WaitForTimeline(
                ref start, (long)(playMs / 1000.0 * freq), highRes, context, ct))
                return;

            cumX += packets[i].Dx;
            cumY += packets[i].Dy;
            MouseTrajectoryPlayback.AccumulateDelta(
                cumX * gainX,
                cumY * gainY,
                ref prevX,
                ref prevY,
                ref remX,
                ref remY,
                out int mx,
                out int my);
            if (mx != 0 || my != 0)
                player.SendRelativeMove(mx, my, dualBackend: false);
        }
    }

    public void ResolveGains(out double gainX, out double gainY)
    {
        if (Math.Abs(Sensitivity - 4.0) < 0.0001 && Math.Abs(SensitivityY - 8.0) < 0.0001)
        {
            gainX = DefaultLookGainX;
            gainY = DefaultLookGainY;
            return;
        }

        gainX = ClampLookGain(Sensitivity);
        gainY = ClampLookGain(SensitivityY);
    }

    private static int[] MergeProtectedTimes(
        IReadOnlyList<int> pauseTimes,
        TrajectoryButtonTransition[] buttons)
    {
        int extra = buttons is null ? 0 : buttons.Length;
        if ((pauseTimes is null || pauseTimes.Count == 0) && extra == 0)
            return [];

        var list = new List<int>((pauseTimes?.Count ?? 0) + extra);
        if (pauseTimes != null)
        {
            for (int i = 0; i < pauseTimes.Count; i++)
                list.Add(pauseTimes[i]);
        }

        if (buttons != null)
        {
            for (int i = 0; i < buttons.Length; i++)
                list.Add(buttons[i].T);
        }

        list.Sort();
        int w = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (w == 0 || list[i] != list[w - 1])
                list[w++] = list[i];
        }

        if (w == list.Count)
            return list.ToArray();
        var compact = new int[w];
        for (int i = 0; i < w; i++)
            compact[i] = list[i];
        return compact;
    }
}
