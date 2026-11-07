using System.Drawing;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Vision;

namespace MacroSuite.Core.Models;

public enum EditableActionKind
{
    MouseClick = 0,
    MouseMove = 1,
    Wait = 2,
    KeyPress = 3,
    VisualWait = 4,
    ColorWait = 5,
    AiVision = 6,
    IfBranch = 7,
    WhileLoop = 8,
    RepeatUntil = 9,
    MouseSweep = 10,
    MouseTrajectory = 11,
    RepeatUntilGone = 12,
    ImageWait = 13
}

/// <summary>
/// Builds replacement steps at the same index. Compatible fields are copied; others get explicit defaults.
/// </summary>
public static class MacroActionFactory
{
    public static readonly string[] KindNames =
    [
        "Mouse Click",
        "Mouse Move",
        "Wait",
        "Key Press",
        "Visual Wait",
        "Color Wait",
        "AI Vision",
        "IF",
        "WHILE",
        "REPEAT UNTIL",
        "Camera Sweep",
        "Camera Path",
        "REPEAT UNTIL GONE",
        "Image Wait"
    ];

    public static EditableActionKind KindOf(MacroStep step) => step switch
    {
        ClickStep => EditableActionKind.MouseClick,
        MouseMoveStep => EditableActionKind.MouseMove,
        DelayStep => EditableActionKind.Wait,
        KeyPressStep => EditableActionKind.KeyPress,
        VisualWaitStep => EditableActionKind.VisualWait,
        WaitUntilStep wait => wait.Condition is ImageCondition
            ? EditableActionKind.ImageWait
            : EditableActionKind.ColorWait,
        AIStep => EditableActionKind.AiVision,
        IfThenElseStep => EditableActionKind.IfBranch,
        LoopStep loop => loop.RepeatUntil
            ? loop.WhileCondition is VisualPresenceCondition { Kind: VisualPresenceKind.ColorAbsent or VisualPresenceKind.ImageAbsent }
                ? EditableActionKind.RepeatUntilGone
                : EditableActionKind.RepeatUntil
            : EditableActionKind.WhileLoop,
        MouseSweepStep => EditableActionKind.MouseSweep,
        MouseTrajectoryStep => EditableActionKind.MouseTrajectory,
        _ => EditableActionKind.Wait
    };

    public static MacroStep Clone(MacroStep step) => step switch
    {
        MouseWheelStep wheel => new MouseWheelStep(wheel.DeltaY, wheel.DeltaX, wheel.RelativeTimestamp)
        {
            StepId = Guid.NewGuid(),
            IsEnabled = wheel.IsEnabled,
            Label = wheel.Label
        },
        MouseSweepStep sweep => CloneSweep(sweep, Guid.NewGuid()),
        MouseTrajectoryStep path => CloneTrajectory(path, Guid.NewGuid()),
        BreakStep brk => new BreakStep
        {
            StepId = Guid.NewGuid(),
            IsEnabled = brk.IsEnabled,
            Label = brk.Label
        },
        ContinueStep cont => new ContinueStep
        {
            StepId = Guid.NewGuid(),
            IsEnabled = cont.IsEnabled,
            Label = cont.Label
        },
        _ => Convert(step, KindOf(step), newStepId: true)
    };

    public static MacroStep Convert(MacroStep source, EditableActionKind target, bool newStepId = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        Guid id = newStepId ? Guid.NewGuid() : source.StepId;
        bool enabled = source.IsEnabled;
        TryGetPoint(source, out int x, out int y);
        var button = source is ClickStep c ? c.Button : MouseButton.Left;
        int clickCount = source is ClickStep c2 ? c2.ClickCount : 1;
        TimeSpan ts = source switch
        {
            ClickStep click => click.RelativeTimestamp,
            MouseMoveStep move => move.RelativeTimestamp,
            KeyPressStep key => key.RelativeTimestamp,
            MouseWheelStep wheel => wheel.RelativeTimestamp,
            MouseSweepStep sweep => sweep.RelativeTimestamp,
            MouseTrajectoryStep path => path.RelativeTimestamp,
            _ => TimeSpan.Zero
        };

        return target switch
        {
            EditableActionKind.MouseClick => new ClickStep(new Point(x, y), button, clickCount,
                source is ClickStep originalClick ? originalClick.DelayAfterMs : 50)
            {
                StepId = id,
                IsEnabled = enabled,
                RelativeTimestamp = ts,
                IsClientRelative = source is ClickStep { IsClientRelative: true } or MouseMoveStep { IsClientRelative: true },
                TargetWindowName = source is ClickStep clickTarget ? clickTarget.TargetWindowName : (source as MouseMoveStep)?.TargetWindowName,
                RecordedClientWidth = source is ClickStep clickWidth ? clickWidth.RecordedClientWidth : (source as MouseMoveStep)?.RecordedClientWidth ?? 0,
                RecordedClientHeight = source is ClickStep clickHeight ? clickHeight.RecordedClientHeight : (source as MouseMoveStep)?.RecordedClientHeight ?? 0,
                XVariable = (source as ClickStep)?.XVariable,
                YVariable = (source as ClickStep)?.YVariable,
                OffsetX = (source as ClickStep)?.OffsetX ?? 0,
                OffsetY = (source as ClickStep)?.OffsetY ?? 0,
                Stroke = source is ClickStep c3 ? c3.Stroke : "Press"
            },
            EditableActionKind.MouseMove => new MouseMoveStep(new Point(x, y), source is MouseMoveStep m && m.IsClientRelative, ts)
            {
                StepId = id,
                IsEnabled = enabled,
                ScreenPosition = new Point(x, y),
                IsRelative = source is MouseMoveStep rel && rel.IsRelative,
                XVariable = source is MouseMoveStep mv ? mv.XVariable : (source as ClickStep)?.XVariable,
                YVariable = source is MouseMoveStep mv2 ? mv2.YVariable : (source as ClickStep)?.YVariable
            },
            EditableActionKind.Wait => new DelayStep(source is DelayStep d ? d.Duration : TimeSpan.FromMilliseconds(250))
            {
                StepId = id,
                IsEnabled = enabled
            },
            EditableActionKind.KeyPress => new KeyPressStep(source is KeyPressStep k ? k.KeyCode : 13, source is KeyPressStep k2 ? k2.DelayAfterMs : 30)
            {
                StepId = id,
                IsEnabled = enabled,
                RelativeTimestamp = ts,
                Stroke = source is KeyPressStep k3 ? k3.Stroke : "Press",
                IsUnicode = source is KeyPressStep { IsUnicode: true }
            },
            EditableActionKind.VisualWait => source is VisualWaitStep v
                ? CloneVisual(v, id, enabled)
                : new VisualWaitStep
                {
                    StepId = id,
                    IsEnabled = enabled,
                    Label = "Wait for Visual Match",
                    Mode = "Pixel",
                    SearchMode = "Fixed",
                    MatchingMethod = "ColorImage",
                    Quality = "Balanced",
                    OnTimeout = "Stop",
                    X = x,
                    Y = y,
                    Width = 1,
                    Height = 1,
                    R = 63,
                    G = 167,
                    B = 214,
                    SimilarityPercent = 90,
                    NeighborhoodSize = 1,
                    RequiredPixelFraction = 0.85,
                    PollIntervalMs = 150,
                    TimeoutMs = source is DelayStep delay ? Math.Max(100, (int)delay.Duration.TotalMilliseconds) : 10_000,
                    RetryCount = 3,
                    SearchRadius = 50
                },
            EditableActionKind.ColorWait => CreateColorWait(source, id, enabled, x, y),
            EditableActionKind.ImageWait => CreateImageWait(source, id, enabled),
            EditableActionKind.AiVision => source is AIStep ai
                ? new AIStep(ai.Prompt, ai.TargetRegion, ai.ModelId, ai.ProviderId)
                {
                    StepId = id,
                    IsEnabled = enabled,
                    Timeout = ai.Timeout,
                    ExpectedWindow = ai.ExpectedWindow,
                    MinConfidence = ai.MinConfidence
                }
                : new AIStep("Describe the on-screen target.")
                {
                    StepId = id,
                    IsEnabled = enabled
                },
            EditableActionKind.IfBranch => source is IfThenElseStep iff
                ? new IfThenElseStep(iff.Condition,
                    newStepId ? CloneList(iff.ThenSteps) : iff.ThenSteps,
                    newStepId ? CloneList(iff.ElseSteps) : iff.ElseSteps)
                {
                    StepId = id,
                    IsEnabled = enabled,
                    Label = iff.Label,
                    Routing = iff.Routing
                }
                : new IfThenElseStep(DefaultPresence(x, y), [], [])
                {
                    StepId = id,
                    IsEnabled = enabled
                },
            EditableActionKind.WhileLoop => ConvertLoop(source, id, enabled, x, y, repeatUntil: false, untilGone: false, cloneChildren: newStepId),
            EditableActionKind.RepeatUntil => ConvertLoop(source, id, enabled, x, y, repeatUntil: true, untilGone: false, cloneChildren: newStepId),
            EditableActionKind.RepeatUntilGone => ConvertLoop(source, id, enabled, x, y, repeatUntil: true, untilGone: true, cloneChildren: newStepId),
            EditableActionKind.MouseSweep => source is MouseSweepStep existing
                ? CloneSweep(existing, id)
                : source is MouseTrajectoryStep fromPath
                    ? SweepFromTrajectory(fromPath, id, enabled)
                    : new MouseSweepStep(x, y, 400, ts) { StepId = id, IsEnabled = enabled },
            EditableActionKind.MouseTrajectory => source is MouseTrajectoryStep existingPath
                ? CloneTrajectory(existingPath, id)
                : TrajectoryFromSweep(source, id, enabled, ts, x, y),
            _ => new DelayStep(TimeSpan.FromMilliseconds(250)) { StepId = id, IsEnabled = enabled }
        };
    }

    private static LoopStep ConvertLoop(MacroStep source, Guid id, bool enabled, int x, int y, bool repeatUntil, bool untilGone, bool cloneChildren)
    {
        VisualPresenceKind desiredKind(VisualPresenceKind kind) => kind is VisualPresenceKind.ImagePresent or VisualPresenceKind.ImageAbsent
            ? untilGone ? VisualPresenceKind.ImageAbsent : VisualPresenceKind.ImagePresent
            : untilGone ? VisualPresenceKind.ColorAbsent : VisualPresenceKind.ColorPresent;
        if (source is LoopStep loop)
        {
            ICondition? condition = loop.WhileCondition;
            if (repeatUntil && condition is VisualPresenceCondition vis)
                condition = ClonePresence(vis, desiredKind(vis.Kind));

            return new LoopStep(loop.MaxIterations, cloneChildren ? CloneList(loop.BodySteps) : loop.BodySteps, condition)
            {
                StepId = id,
                IsEnabled = enabled,
                MaxDurationMs = loop.MaxDurationMs,
                CheckIntervalMs = loop.CheckIntervalMs,
                RepeatUntil = repeatUntil,
                MinBodyDelayMs = loop.MinBodyDelayMs,
                Routing = loop.Routing,
                Label = repeatUntil
                    ? untilGone
                        ? "Repeat until gone (run while present)"
                        : "Repeat until present (run while absent)"
                    : loop.Label
            };
        }

        VisualPresenceCondition presence = source switch
        {
            IfThenElseStep { Condition: VisualPresenceCondition vis } => ClonePresence(vis, repeatUntil ? desiredKind(vis.Kind) : vis.Kind),
            WaitUntilStep { Condition: ColorCondition color } => PresenceFromColor(color, x, y),
            _ => DefaultPresence(x, y)
        };
        if (repeatUntil)
            presence = ClonePresence(presence, desiredKind(presence.Kind));

        return new LoopStep(10_000, [], presence)
        {
            StepId = id,
            IsEnabled = enabled,
            MaxDurationMs = 30_000,
            CheckIntervalMs = Math.Max(10, presence.PollIntervalMs),
            RepeatUntil = repeatUntil,
            Label = repeatUntil
                ? untilGone
                    ? "Repeat until gone (run while present)"
                    : "Repeat until present (run while absent)"
                : $"While ({presence.Description}) [max 10000]"
        };
    }

    private static VisualPresenceCondition PresenceFromColor(ColorCondition color, int x, int y)
    {
        var region = color.Region ?? (color.Location is { } loc
            ? new Rectangle(loc.X, loc.Y, 1, 1)
            : new Rectangle(x, y, 80, 40));
        return new VisualPresenceCondition
        {
            Kind = VisualPresenceKind.ColorPresent,
            X = region.X,
            Y = region.Y,
            Width = Math.Max(1, region.Width),
            Height = Math.Max(1, region.Height),
            R = color.R,
            G = color.G,
            B = color.B,
            SimilarityPercent = 90,
            RequiredPixelFraction = color.MinMatchPercentage,
            AdditionalTargets = color.AdditionalTargets,
            ComparisonMode = color.ComparisonMode,
            SampleStride = color.SampleStride,
            PollIntervalMs = color.PollIntervalMs,
            RequiredConfirmations = color.RequiredConfirmations,
            RequiredFalseConfirmations = color.RequiredFalseConfirmations,
            StabilityDurationMs = color.StabilityDurationMs,
            TimeoutMs = color.TimeoutMs,
            FailurePolicy = color.FailurePolicy
        };
    }

    public static VisualPresenceCondition DefaultPresence(int x, int y)
        => new()
        {
            Kind = VisualPresenceKind.ColorPresent,
            X = x,
            Y = y,
            Width = 80,
            Height = 40,
            R = 220,
            G = 40,
            B = 40,
            SimilarityPercent = 90,
            RequiredPixelFraction = 0.08,
            ComparisonMode = ColorComparisonMode.Cie76,
            SampleStride = 1,
            PollIntervalMs = ConditionReliability.DefaultVisualPollIntervalMs,
            RequiredConfirmations = ConditionReliability.DefaultConfirmations,
            RequiredFalseConfirmations = ConditionReliability.DefaultConfirmations,
            TimeoutMs = ConditionReliability.DefaultTimeoutMs
        };

    public static VisualPresenceCondition ClonePresence(
        VisualPresenceCondition source,
        VisualPresenceKind? kind = null)
        => new()
        {
            Kind = kind ?? source.Kind,
            X = source.X,
            Y = source.Y,
            Width = source.Width,
            Height = source.Height,
            R = source.R,
            G = source.G,
            B = source.B,
            SimilarityPercent = source.SimilarityPercent,
            RequiredPixelFraction = source.RequiredPixelFraction,
            AdditionalTargets = source.AdditionalTargets,
            SearchMode = source.SearchMode,
            TemplatePath = source.TemplatePath,
            TemplateBytes = source.TemplateBytes,
            MinConfidence = source.MinConfidence,
            ComparisonMode = source.ComparisonMode,
            SampleStride = source.SampleStride,
            PollIntervalMs = source.PollIntervalMs,
            RequiredConfirmations = source.RequiredConfirmations,
            RequiredFalseConfirmations = source.RequiredFalseConfirmations,
            StabilityDurationMs = source.StabilityDurationMs,
            TimeoutMs = source.TimeoutMs,
            FailurePolicy = source.FailurePolicy
        };

    public static ColorCondition CloneColor(ColorCondition source)
        => new(
            source.Region,
            source.Location,
            source.R,
            source.G,
            source.B,
            source.Tolerance,
            source.MinMatchPercentage,
            source.ComparisonMode,
            source.SampleStride,
            source.AdditionalTargets,
            source.PollIntervalMs,
            source.RequiredConfirmations,
            source.RequiredFalseConfirmations,
            source.StabilityDurationMs,
            source.TimeoutMs,
            source.FailurePolicy);

    public static IfThenElseStep WithIfBodies(IfThenElseStep source, IReadOnlyList<MacroStep> thenSteps, IReadOnlyList<MacroStep> elseSteps)
        => new(source.Condition, thenSteps, elseSteps)
        {
            StepId = source.StepId,
            IsEnabled = source.IsEnabled,
            Routing = source.Routing,
            Label = source.Label
        };

    public static IfThenElseStep WithIfCondition(IfThenElseStep source, ICondition condition, BlockRoutingOptions? routing = null)
        => new(condition, source.ThenSteps, source.ElseSteps)
        {
            StepId = source.StepId,
            IsEnabled = source.IsEnabled,
            Label = source.Label,
            Routing = routing ?? source.Routing
        };

    public static LoopStep WithLoopBody(
        LoopStep source,
        IReadOnlyList<MacroStep> body,
        ICondition? condition = null,
        int? maxDurationMs = null,
        int? checkIntervalMs = null,
        int? minBodyDelayMs = null,
        BlockRoutingOptions? routing = null)
        => new(source.MaxIterations, body, condition ?? source.WhileCondition)
        {
            StepId = source.StepId,
            IsEnabled = source.IsEnabled,
            MaxDurationMs = maxDurationMs ?? source.MaxDurationMs,
            CheckIntervalMs = Math.Clamp(checkIntervalMs ?? source.CheckIntervalMs, 10, 10_000),
            RepeatUntil = source.RepeatUntil,
            MinBodyDelayMs = Math.Clamp(minBodyDelayMs ?? source.MinBodyDelayMs, 0, 10_000),
            Routing = routing ?? source.Routing,
            Label = source.Label
        };

    private static List<MacroStep> CloneList(IReadOnlyList<MacroStep> steps)
        => steps.Select(s => Clone(s)).ToList();

    public static MacroStep UpdateClick(
        ClickStep source,
        int x,
        int y,
        MouseButton button,
        int clickCount,
        string? xVariable = null,
        string? yVariable = null,
        int? offsetX = null,
        int? offsetY = null,
        bool? isClientRelative = null)
        => new ClickStep(new Point(x, y), button, Math.Clamp(clickCount, 1, 2), source.DelayAfterMs)
        {
            StepId = source.StepId,
            IsEnabled = source.IsEnabled,
            RelativeTimestamp = source.RelativeTimestamp,
            IsClientRelative = isClientRelative ?? source.IsClientRelative,
            TargetWindowName = source.TargetWindowName,
            RecordedClientWidth = source.RecordedClientWidth,
            RecordedClientHeight = source.RecordedClientHeight,
            XVariable = xVariable is null ? source.XVariable : string.IsNullOrWhiteSpace(xVariable) ? null : xVariable,
            YVariable = yVariable is null ? source.YVariable : string.IsNullOrWhiteSpace(yVariable) ? null : yVariable,
            OffsetX = offsetX ?? source.OffsetX,
            OffsetY = offsetY ?? source.OffsetY,
            Stroke = source.Stroke
        };

    public static MacroStep UpdateSweep(
        MouseSweepStep source,
        int dx,
        int dy,
        int durationMs,
        double sensitivityX,
        double sensitivityY,
        int playbackHz,
        MouseInterpolation interpolation,
        int horizontalSmoothing,
        int verticalSmoothing,
        bool filterVerticalJitter,
        bool filterHorizontalJitter,
        MouseSweepProfile profile)
    {
        int ms = Math.Max(16, durationMs);
        int vert = MouseSweepMotion.ClampJitter(verticalSmoothing);
        return new MouseSweepStep(dx, dy, ms, source.RelativeTimestamp)
        {
            StepId = source.StepId,
            IsEnabled = source.IsEnabled,
            Interpolation = interpolation,
            Profile = profile,
            PlaybackHz = MouseSweepMotion.ClampHz(playbackHz),
            HorizontalSmoothing = MouseSweepMotion.ClampJitter(horizontalSmoothing),
            VerticalSmoothing = vert,
            JitterReduction = vert,
            FilterVerticalJitter = filterVerticalJitter,
            FilterHorizontalJitter = filterHorizontalJitter,
            Sensitivity = 1.0,
            SensitivityX = MouseSweepMotion.ClampSensitivity(sensitivityX),
            SensitivityY = MouseSweepMotion.ClampSensitivity(sensitivityY),
            Label = $"Sweep dx={dx} dy={dy} {ms}ms"
        };
    }

    private static MouseSweepStep CloneSweep(MouseSweepStep source, Guid id)
        => new(source.DeltaX, source.DeltaY, source.DurationMs, source.RelativeTimestamp)
        {
            StepId = id,
            IsEnabled = source.IsEnabled,
            Label = source.Label,
            Interpolation = source.Interpolation,
            Profile = source.Profile,
            PlaybackHz = source.PlaybackHz,
            JitterReduction = source.JitterReduction,
            HorizontalSmoothing = source.HorizontalSmoothing,
            VerticalSmoothing = source.VerticalSmoothing,
            FilterVerticalJitter = source.FilterVerticalJitter,
            FilterHorizontalJitter = source.FilterHorizontalJitter,
            Sensitivity = source.Sensitivity,
            SensitivityX = source.SensitivityX,
            SensitivityY = source.SensitivityY
        };

    private static MouseTrajectoryStep CloneTrajectory(MouseTrajectoryStep source, Guid id)
        => new(source.Samples.Length == 0 ? [] : source.Samples.ToArray(), source.RelativeTimestamp)
        {
            StepId = id,
            TimestampIsStart = source.TimestampIsStart,
            IsEnabled = source.IsEnabled,
            Label = source.Label,
            PlaybackHz = source.PlaybackHz,
            Sensitivity = source.Sensitivity,
            SensitivityY = source.SensitivityY,
            Packets = source.Packets is null ? null : source.Packets.ToArray(),
            ButtonTransitions = source.ButtonTransitions.Length == 0 ? [] : source.ButtonTransitions.ToArray(),
            Mode = source.Mode,
            GapPolicy = source.GapPolicy,
            ReconstructionTolerancePx = source.ReconstructionTolerancePx
        };

    private static MouseSweepStep SweepFromTrajectory(MouseTrajectoryStep source, Guid id, bool enabled)
    {
        MouseTrajectoryPlayback.NetDisplacement(source.Samples, out int dx, out int dy);
        TimeSpan start = source.TimestampIsStart ? source.RelativeTimestamp : source.RelativeTimestamp - source.RecordedDuration;
        return new MouseSweepStep(dx, dy, source.DurationMs, start < TimeSpan.Zero ? TimeSpan.Zero : start)
        {
            StepId = id,
            IsEnabled = enabled
        };
    }

    private static MouseTrajectoryStep TrajectoryFromSweep(MacroStep source, Guid id, bool enabled, TimeSpan ts, int x, int y)
    {
        int dx = x;
        int dy = y;
        int ms = 400;
        if (source is MouseSweepStep sweep)
        {
            dx = sweep.DeltaX;
            dy = sweep.DeltaY;
            ms = sweep.DurationMs;
            ts = sweep.RelativeTimestamp;
        }

        var samples = new[]
        {
            new MouseTrajectorySample(0, 0, 0),
            new MouseTrajectorySample(ms, dx, dy)
        };
        return new MouseTrajectoryStep(samples, ts)
        {
            StepId = id,
            TimestampIsStart = true,
            IsEnabled = enabled,
            PlaybackHz = MouseTrajectoryPlayback.DefaultHz,
            Sensitivity = MouseTrajectoryStep.DefaultLookGainX,
            SensitivityY = MouseTrajectoryStep.DefaultLookGainY
        };
    }

    public static MacroStep UpdateTrajectory(
        MouseTrajectoryStep source,
        int playbackHz,
        double sensitivity,
        double sensitivityY,
        TrajectoryPlaybackMode mode = TrajectoryPlaybackMode.Reconstructed,
        MouseTrajectorySample[]? samples = null,
        MouseTrajectoryPacket[]? packets = null)
        => new MouseTrajectoryStep(samples ?? source.Samples, source.RelativeTimestamp)
        {
            StepId = source.StepId,
            TimestampIsStart = source.TimestampIsStart,
            IsEnabled = source.IsEnabled,
            PlaybackHz = MouseTrajectoryPlayback.ClampHz(playbackHz),
            Sensitivity = MouseTrajectoryStep.ClampLookGain(sensitivity),
            SensitivityY = MouseTrajectoryStep.ClampLookGain(sensitivityY),
            Mode = mode,
            GapPolicy = source.GapPolicy,
            Packets = packets ?? source.Packets,
            ButtonTransitions = source.ButtonTransitions,
            ReconstructionTolerancePx = source.ReconstructionTolerancePx
        };

    public static MacroStep UpdateMove(MouseMoveStep source, int x, int y, string? xVariable = null, string? yVariable = null)
        => new MouseMoveStep(new Point(x, y), source.IsClientRelative, source.RelativeTimestamp)
        {
            StepId = source.StepId,
            IsEnabled = source.IsEnabled,
            ScreenPosition = new Point(x, y),
            TargetWindowName = source.TargetWindowName,
            RecordedClientWidth = source.RecordedClientWidth,
            RecordedClientHeight = source.RecordedClientHeight,
            DurationMs = source.DurationMs,
            IsRelative = source.IsRelative,
            XVariable = xVariable is null ? source.XVariable : string.IsNullOrWhiteSpace(xVariable) ? null : xVariable,
            YVariable = yVariable is null ? source.YVariable : string.IsNullOrWhiteSpace(yVariable) ? null : yVariable
        };

    public static MacroStep UpdateWait(DelayStep source, int milliseconds)
        => new DelayStep(TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)))
        {
            StepId = source.StepId,
            IsEnabled = source.IsEnabled
        };

    public static MacroStep UpdateKey(KeyPressStep source, int keyCode, string? stroke = null)
        => new KeyPressStep(keyCode, source.DelayAfterMs)
        {
            StepId = source.StepId,
            IsEnabled = source.IsEnabled,
            RelativeTimestamp = source.RelativeTimestamp,
            Stroke = stroke ?? source.Stroke,
            IsUnicode = source.IsUnicode
        };

    public static MacroStep UpdateVisual(VisualWaitStep source, VisualWaitStep values)
        => new VisualWaitStep
        {
            StepId = source.StepId,
            IsEnabled = source.IsEnabled,
            Label = values.Label ?? source.Label,
            Mode = values.Mode,
            SearchMode = values.SearchMode,
            MatchingMethod = values.MatchingMethod,
            Quality = values.Quality,
            OnTimeout = values.OnTimeout,
            X = values.X,
            Y = values.Y,
            Width = values.Width,
            Height = values.Height,
            R = values.R,
            G = values.G,
            B = values.B,
            SimilarityPercent = values.SimilarityPercent,
            NeighborhoodSize = values.NeighborhoodSize,
            RequiredPixelFraction = values.RequiredPixelFraction,
            AdditionalTargets = values.AdditionalTargets ?? source.AdditionalTargets,
            PollIntervalMs = values.PollIntervalMs,
            RequiredConfirmations = values.RequiredConfirmations,
            RequiredFalseConfirmations = values.RequiredFalseConfirmations,
            StabilityDurationMs = values.StabilityDurationMs,
            TimeoutMs = values.TimeoutMs,
            RetryCount = values.RetryCount,
            SearchRadius = values.SearchRadius,
            AiMinIntervalMs = values.AiMinIntervalMs,
            ComparisonMode = values.ComparisonMode,
            SampleStride = values.SampleStride,
            FailurePolicy = values.FailurePolicy,
            TemplateBgra = values.TemplateBgra ?? source.TemplateBgra,
            TemplateWidth = values.TemplateWidth != 0 ? values.TemplateWidth : source.TemplateWidth,
            TemplateHeight = values.TemplateHeight != 0 ? values.TemplateHeight : source.TemplateHeight
        };

    public static MacroStep UpdateColorWait(
        WaitUntilStep source,
        int x,
        int y,
        int width,
        int height,
        byte r,
        byte g,
        byte b,
        int tolerance,
        double minimumMatchPercentage,
        ColorComparisonMode comparisonMode,
        int timeoutMs,
        int pollIntervalMs,
        int requiredConfirmations,
        IReadOnlyList<PixelColor>? additionalTargets = null,
        int sampleStride = 1,
        int requiredFalseConfirmations = 1,
        int stabilityDurationMs = 0,
        ConditionFailurePolicy failurePolicy = ConditionFailurePolicy.Default,
        bool? waitWhile = null)
    {
        int timeout = ConditionReliability.ClampTimeoutMs(timeoutMs);
        int poll = ConditionReliability.ClampPollIntervalMs(pollIntervalMs);
        int trueCount = ConditionReliability.ClampConfirmations(requiredConfirmations);
        int falseCount = ConditionReliability.ClampConfirmations(requiredFalseConfirmations);
        int stride = ConditionReliability.ClampSampleStride(sampleStride);
        ColorCondition cond = width > 1 || height > 1
            ? new ColorCondition(
                new Rectangle(x, y, Math.Max(1, width), Math.Max(1, height)),
                null,
                r,
                g,
                b,
                tolerance,
                minimumMatchPercentage,
                comparisonMode,
                stride,
                additionalTargets,
                poll,
                trueCount,
                falseCount,
                stabilityDurationMs,
                timeout,
                failurePolicy)
            : new ColorCondition(
                null,
                new Point(x, y),
                r,
                g,
                b,
                tolerance,
                1,
                comparisonMode,
                stride,
                additionalTargets,
                poll,
                trueCount,
                falseCount,
                stabilityDurationMs,
                timeout,
                failurePolicy);
        bool inverted = waitWhile ?? source.WaitWhile;
        return new WaitUntilStep(cond)
        {
            StepId = source.StepId,
            IsEnabled = source.IsEnabled,
            Timeout = TimeSpan.FromMilliseconds(timeout),
            PollInterval = TimeSpan.FromMilliseconds(poll),
            RequiredConfirmations = trueCount,
            RequiredFalseConfirmations = falseCount,
            StabilityDurationMs = stabilityDurationMs,
            ThrowOnTimeout = failurePolicy == ConditionFailurePolicy.Skip ? false : source.ThrowOnTimeout,
            WaitWhile = inverted,
            Label = WaitUntilStep.FormatLabel(cond, inverted)
        };
    }

    public static MacroStep UpdateImageWait(
        WaitUntilStep source,
        int timeoutMs,
        int pollIntervalMs,
        int requiredConfirmations,
        int requiredFalseConfirmations = 1,
        int stabilityDurationMs = 0,
        bool? waitWhile = null,
        ImageCondition? replacement = null,
        bool? repeatWhileVisible = null,
        int? repeatDelayMs = null,
        AppearanceRedirectOptions? appearanceRedirect = null,
        bool replaceAppearanceRedirect = false)
    {
        ImageCondition image = replacement
            ?? source.Condition as ImageCondition
            ?? throw new InvalidOperationException("UpdateImageWait requires an ImageCondition. Convert kind explicitly to change subtype.");

        int timeout = ConditionReliability.ClampTimeoutMs(timeoutMs);
        int poll = ConditionReliability.ClampPollIntervalMs(pollIntervalMs);
        int trueCount = ConditionReliability.ClampConfirmations(requiredConfirmations);
        int falseCount = ConditionReliability.ClampConfirmations(requiredFalseConfirmations);
        bool inverted = waitWhile ?? source.WaitWhile;
        bool repeat = repeatWhileVisible ?? source.RepeatWhileVisible;
        if (repeatWhileVisible == true) inverted = false;
        if (inverted) repeat = false;
        AppearanceRedirectOptions? redirect = replaceAppearanceRedirect ? appearanceRedirect : source.AppearanceRedirect;
        if (repeat || inverted) redirect = null;
        if (redirect is not null) { repeat = false; inverted = false; }
        return new WaitUntilStep(image)
        {
            StepId = source.StepId,
            IsEnabled = source.IsEnabled,
            Timeout = TimeSpan.FromMilliseconds(timeout),
            PollInterval = TimeSpan.FromMilliseconds(poll),
            RequiredConfirmations = trueCount,
            RequiredFalseConfirmations = falseCount,
            StabilityDurationMs = stabilityDurationMs,
            ThrowOnTimeout = source.ThrowOnTimeout,
            WaitWhile = inverted,
            RepeatWhileVisible = repeat,
            RepeatDelayMs = Math.Clamp(repeatDelayMs ?? source.RepeatDelayMs, 50, 60_000),
            AppearanceRedirect = redirect,
            Label = redirect is not null ? $"Jump when image appears: {image.Description}"
                : repeat ? $"Repeat while visible: {image.Description}" : WaitUntilStep.FormatLabel(image, inverted)
        };
    }

    public static MacroStep UpdateAi(AIStep source, string prompt, int timeoutMs)
        => UpdateAi(source, prompt, timeoutMs, source.ExpectedWindow, source.MinConfidence);

    public static MacroStep UpdateAi(AIStep source, string prompt, int timeoutMs, string? expectedWindow, double? minConfidence)
        => new AIStep(prompt, source.TargetRegion, source.ModelId, source.ProviderId)
        {
            StepId = source.StepId,
            IsEnabled = source.IsEnabled,
            Timeout = TimeSpan.FromMilliseconds(Math.Max(0, timeoutMs)),
            ExpectedWindow = string.IsNullOrWhiteSpace(expectedWindow) ? null : expectedWindow.Trim(),
            MinConfidence = minConfidence
        };

    private static VisualWaitStep CloneVisual(VisualWaitStep v, Guid id, bool enabled)
        => new()
        {
            StepId = id,
            IsEnabled = enabled,
            Label = v.Label,
            Mode = v.Mode,
            SearchMode = v.SearchMode,
            MatchingMethod = v.MatchingMethod,
            Quality = v.Quality,
            OnTimeout = v.OnTimeout,
            X = v.X,
            Y = v.Y,
            Width = v.Width,
            Height = v.Height,
            R = v.R,
            G = v.G,
            B = v.B,
            SimilarityPercent = v.SimilarityPercent,
            NeighborhoodSize = v.NeighborhoodSize,
            RequiredPixelFraction = v.RequiredPixelFraction,
            AdditionalTargets = v.AdditionalTargets,
            PollIntervalMs = v.PollIntervalMs,
            RequiredConfirmations = v.RequiredConfirmations,
            RequiredFalseConfirmations = v.RequiredFalseConfirmations,
            StabilityDurationMs = v.StabilityDurationMs,
            TimeoutMs = v.TimeoutMs,
            RetryCount = v.RetryCount,
            SearchRadius = v.SearchRadius,
            AiMinIntervalMs = v.AiMinIntervalMs,
            ComparisonMode = v.ComparisonMode,
            SampleStride = v.SampleStride,
            FailurePolicy = v.FailurePolicy,
            TemplateBgra = v.TemplateBgra,
            TemplateWidth = v.TemplateWidth,
            TemplateHeight = v.TemplateHeight
        };

    private static MacroStep CreateColorWait(MacroStep source, Guid id, bool enabled, int x, int y)
    {
        byte r = 255, g = 255, b = 255;
        int tol = 20;
        TimeSpan timeout = TimeSpan.FromSeconds(10);
        TimeSpan pollInterval = TimeSpan.FromMilliseconds(50);
        int confirmations = 1;
        ColorComparisonMode mode = ColorComparisonMode.Rgb;
        double minimumMatchPercentage = 1;
        int sampleStride = 1;
        int falseConfirmations = 1;
        int stabilityMs = 0;
        ConditionFailurePolicy failurePolicy = ConditionFailurePolicy.Default;
        IReadOnlyList<PixelColor>? additionalTargets = null;
        Rectangle? region = null;
        if (source is WaitUntilStep wait)
        {
            timeout = wait.Timeout;
            pollInterval = wait.PollInterval;
            confirmations = wait.RequiredConfirmations;
            if (wait.Condition is ColorCondition color)
            {
                r = color.R;
                g = color.G;
                b = color.B;
                tol = color.Tolerance;
                mode = color.ComparisonMode;
                minimumMatchPercentage = color.MinMatchPercentage;
                additionalTargets = color.AdditionalTargets;
                region = color.Region;
                sampleStride = color.SampleStride;
                falseConfirmations = color.RequiredFalseConfirmations;
                stabilityMs = color.StabilityDurationMs;
                failurePolicy = color.FailurePolicy;
                if (color.Location.HasValue)
                {
                    x = color.Location.Value.X;
                    y = color.Location.Value.Y;
                }
            }
        }

        ColorCondition condition = region.HasValue
            ? new ColorCondition(
                region,
                null,
                r,
                g,
                b,
                tol,
                minimumMatchPercentage,
                mode,
                sampleStride,
                additionalTargets,
                (int)pollInterval.TotalMilliseconds,
                confirmations,
                falseConfirmations,
                stabilityMs,
                (int)timeout.TotalMilliseconds,
                failurePolicy)
            : new ColorCondition(
                null,
                new Point(x, y),
                r,
                g,
                b,
                tol,
                1,
                mode,
                sampleStride,
                additionalTargets,
                (int)pollInterval.TotalMilliseconds,
                confirmations,
                falseConfirmations,
                stabilityMs,
                (int)timeout.TotalMilliseconds,
                failurePolicy);
        return new WaitUntilStep(condition)
        {
            StepId = id,
            IsEnabled = enabled,
            Timeout = timeout,
            PollInterval = pollInterval,
            RequiredConfirmations = confirmations,
            RequiredFalseConfirmations = falseConfirmations,
            StabilityDurationMs = stabilityMs,
            ThrowOnTimeout = source is WaitUntilStep w && w.ThrowOnTimeout,
            WaitWhile = source is WaitUntilStep waitWhile && waitWhile.WaitWhile,
            Label = WaitUntilStep.FormatLabel(condition, source is WaitUntilStep waitLabel && waitLabel.WaitWhile)
        };
    }

    private static MacroStep CreateImageWait(MacroStep source, Guid id, bool enabled)
    {
        TimeSpan timeout = TimeSpan.FromSeconds(15);
        TimeSpan pollInterval = TimeSpan.FromMilliseconds(100);
        int confirmations = 1;
        int falseConfirmations = 1;
        int stabilityMs = 0;
        bool waitWhile = false;
        bool throwOnTimeout = true;
        bool repeatWhileVisible = false;
        int repeatDelayMs = 500;
        AppearanceRedirectOptions? appearanceRedirect = null;
        ImageCondition condition;
        if (source is WaitUntilStep wait)
        {
            timeout = wait.Timeout;
            pollInterval = wait.PollInterval;
            confirmations = wait.RequiredConfirmations;
            falseConfirmations = wait.RequiredFalseConfirmations;
            stabilityMs = wait.StabilityDurationMs;
            waitWhile = wait.WaitWhile;
            throwOnTimeout = wait.ThrowOnTimeout;
            repeatWhileVisible = wait.Condition is ImageCondition && wait.RepeatWhileVisible;
            repeatDelayMs = Math.Clamp(wait.RepeatDelayMs, 50, 60_000);
            appearanceRedirect = wait.Condition is ImageCondition ? wait.AppearanceRedirect : null;
            condition = wait.Condition as ImageCondition
                ?? new ImageCondition(wait.Condition is ImageCondition img ? img.TemplatePath : null, null);
        }
        else
        {
            condition = new ImageCondition((string?)null, null);
        }

        return new WaitUntilStep(condition)
        {
            StepId = id,
            IsEnabled = enabled,
            Timeout = timeout,
            PollInterval = pollInterval,
            RequiredConfirmations = confirmations,
            RequiredFalseConfirmations = falseConfirmations,
            StabilityDurationMs = stabilityMs,
            ThrowOnTimeout = throwOnTimeout,
            WaitWhile = waitWhile,
            RepeatWhileVisible = repeatWhileVisible,
            RepeatDelayMs = repeatDelayMs,
            AppearanceRedirect = appearanceRedirect,
            Label = appearanceRedirect is not null ? $"Jump when image appears: {condition.Description}"
                : repeatWhileVisible ? $"Repeat while visible: {condition.Description}" : WaitUntilStep.FormatLabel(condition, waitWhile)
        };
    }

    private static void TryGetPoint(MacroStep source, out int x, out int y)
    {
        switch (source)
        {
            case ClickStep click:
                x = click.Position.X;
                y = click.Position.Y;
                return;
            case MouseMoveStep move:
                x = move.Position.X;
                y = move.Position.Y;
                return;
            case VisualWaitStep visual:
                x = visual.X;
                y = visual.Y;
                return;
            case WaitUntilStep { Condition: ColorCondition { Location: { } loc } }:
                x = loc.X;
                y = loc.Y;
                return;
            default:
                x = 0;
                y = 0;
                return;
        }
    }
}
