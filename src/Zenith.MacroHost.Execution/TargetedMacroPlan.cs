using System.Drawing;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using Zenith.Sessions.Core;

namespace Zenith.MacroHost.Execution;
public enum TargetedMacroScope
{
    OwnedWindowDiagnostic = 1
}

public enum TargetedMacroPlanProblem
{
    InvalidDocument,
    UnsupportedTarget,
    UnsupportedStep,
    UnsupportedInput,
    ResourceLimit,
    // Appended last so the existing numeric values do not change.
    Disabled
}

public sealed class TargetedMacroPlanException(TargetedMacroPlanProblem problem) : Exception(problem switch
{
    TargetedMacroPlanProblem.UnsupportedTarget => "The saved target policy is not supported by this diagnostic execution scope.",
    TargetedMacroPlanProblem.UnsupportedStep => "The entire macro must use supported flat input and delay actions before it can run.",
    TargetedMacroPlanProblem.UnsupportedInput => "The macro contains unsupported input or coordinate semantics.",
    TargetedMacroPlanProblem.ResourceLimit => "The macro exceeds the bounded execution plan limits.",
    TargetedMacroPlanProblem.Disabled => "The saved macro is disabled. Enable it in the editor before running it.",
    _ => "The macro document or execution settings are invalid."
})
{
    public TargetedMacroPlanProblem Problem { get; } = problem;
}

[Flags]
public enum TargetedMacroSignals
{
    None = 0,
    ClientMove = 1,
    LeftButton = 2,
    RightButton = 4,
    MiddleButton = 8,
    VerticalWheel = 16
}

/// <summary>A declared transport vocabulary, not a target/account eligibility decision.</summary>
public sealed class TargetedMacroInputSupport
{
    private readonly HashSet<int> _keys;
    public TargetedMacroSignals Signals { get; }
    public int MaximumHolds { get; }

    public TargetedMacroInputSupport(IReadOnlyCollection<int> virtualKeys, TargetedMacroSignals signals, int maximumHolds = 64)
    {
        ArgumentNullException.ThrowIfNull(virtualKeys);
        if (virtualKeys.Count > 254 || ((int)signals & ~31) != 0 || maximumHolds is < 1 or > 258)
            throw new ArgumentException("The supported input vocabulary is invalid.");
        _keys = new();
        foreach (int key in virtualKeys)
            if (key is < 1 or > 254 || !_keys.Add(key))
                throw new ArgumentException("The supported virtual-key set is invalid.");
        Signals = signals;
        MaximumHolds = maximumHolds;
    }

    internal bool Supports(TargetedMacroPlan plan) => plan.Keys.All(_keys.Contains) && (plan.Signals & ~Signals) == 0 && plan.PeakHolds <= MaximumHolds;
}

/// <summary>Privately frozen supported fields. No mutable source/model/step is retained or shared between runs.</summary>
public sealed class TargetedMacroPlan
{
    public const int MaximumSteps = 1024, MaximumRepeats = 1000, MaximumStepExecutions = 100_000, MaximumClientDimension = 8192;
    public static readonly TimeSpan MaximumRuntime = TimeSpan.FromHours(1);
    private readonly FrozenStep[] _steps;
    private readonly Settings _settings;
    internal IReadOnlyList<int> Keys { get; }
    internal TargetedMacroSignals Signals { get; }
    public Guid MacroId { get; }
    public TargetedMacroScope Scope { get; }
    public int StepCount => _steps.Length;
    public int PeakHolds { get; }
    public int TotalStepExecutions => checked(StepCount * _settings.Repeats);

    private TargetedMacroPlan(Guid id, TargetedMacroScope scope, FrozenStep[] steps, Settings settings, int[] keys, TargetedMacroSignals signals, int peak)
    {
        MacroId = id;
        Scope = scope;
        _steps = steps;
        _settings = settings;
        Keys = Array.AsReadOnly(keys);
        Signals = signals;
        PeakHolds = peak;
    }

    public static TargetedMacroPlan Compile(MacroModel source, TargetedMacroScope scope)
    {
        if (source is null || scope != TargetedMacroScope.OwnedWindowDiagnostic)
            throw Bad(TargetedMacroPlanProblem.InvalidDocument);
        try
        {
            if (source.Id == Guid.Empty || source.SchemaVersion != MacroSchema.CurrentVersion || source.Settings is null || source.Steps is null)
                throw Bad(TargetedMacroPlanProblem.InvalidDocument);
            // A macro the user switched off is refused for that reason first: it is the most
            // actionable cause, and it is not a plan-limit or target-policy problem.
            if (!source.Settings.IsActive)
                throw Bad(TargetedMacroPlanProblem.Disabled);
            // Explicit unbound fixture consent is required; never strip a saved Screen,
            // active-window, application or title-based policy to make it executable here.
            if (source.Settings.Target is not null)
                throw Bad(TargetedMacroPlanProblem.UnsupportedTarget);
            var settings = Settings.Copy(source.Settings);
            int count = source.Steps.Count;
            if (count is < 1 or > MaximumSteps || (long)count * settings.Repeats > MaximumStepExecutions)
                throw Bad(TargetedMacroPlanProblem.ResourceLimit);
            var steps = new FrozenStep[count];
            var ids = new HashSet<Guid>();
            var keys = new HashSet<int>();
            TargetedMacroSignals signals = 0;
            TimeSpan previous = TimeSpan.Zero;
            for (int index = 0; index < count; index++)
            {
                var step = source.Steps[index];
                if (step is null || step.StepId == Guid.Empty || !ids.Add(step.StepId))
                    throw Bad(TargetedMacroPlanProblem.InvalidDocument);
                var frozen = FrozenStep.Copy(step);
                steps[index] = frozen;
                if (frozen.Timestamp > TimeSpan.Zero)
                {
                    if (frozen.Timestamp < previous)
                        throw Bad(TargetedMacroPlanProblem.UnsupportedInput);
                    previous = frozen.Timestamp;
                }

                if (frozen.Enabled)
                {
                    if (frozen.Type == StepKind.Key)
                        keys.Add(frozen.Key);
                    signals |= frozen.Required;
                }
            }

            // Known ownership errors are document incompatibilities, not runtime surprises
            // after an earlier action has already emitted. The total loop is capped100k.
            var holds = new HashSet<int>();
            int peak = 0;
            for (int repeat = 0; repeat < settings.Repeats; repeat++)
                foreach (var step in steps)
                    if (step.Enabled)
                        step.Simulate(holds, ref peak);
            return new(source.Id, scope, steps, settings, keys.Order().ToArray(), signals, peak);
        }
        catch (TargetedMacroPlanException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Bad(TargetedMacroPlanProblem.InvalidDocument);
        }
    }

    internal bool Fits(TargetedInputTarget target)
    {
        if (!target.IsValid || target.ContentBounds.Width > MaximumClientDimension || target.ContentBounds.Height > MaximumClientDimension)
            return false;
        foreach (var step in _steps)
        {
            if (!step.Enabled || step.Type is not (StepKind.Click or StepKind.Move))
                continue;
            int x = step.X + step.OffsetX, y = step.Y + step.OffsetY;
            if (step.RecordedWidth > 0)
            {
                x = (int)Math.Round(x * (target.ContentBounds.Width / (double)step.RecordedWidth));
                y = (int)Math.Round(y * (target.ContentBounds.Height / (double)step.RecordedHeight));
            }

            if (x < 0 || y < 0 || x >= target.ContentBounds.Width || y >= target.ContentBounds.Height)
                return false;
        }

        return true;
    }

    internal MacroModel CreateModel() => new()
    {
        Id = MacroId,
        Name = "Owned-window diagnostic macro",
        Settings = _settings.Create(),
        Steps = _steps.Select(step => step.Create()).ToArray()
    };
    internal static TargetedMacroPlanException Bad(TargetedMacroPlanProblem problem) => new(problem);
    private sealed record Settings(double Speed, int Repeats, TimeSpan Runtime, int Iterations, int Depth)
    {
        public static Settings Copy(MacroSettings source)
        {
            var value = new Settings(source.PlaybackSpeed, source.RepeatCount, source.MaxRuntime, source.MaxIterations, source.MaxLoopDepth);
            if (source.RepeatUntilStopped || !double.IsFinite(value.Speed) || value.Speed is < 0.1 or > 10 || value.Repeats is < 1 or > MaximumRepeats || value.Runtime <= TimeSpan.Zero || value.Runtime > MaximumRuntime || value.Iterations is < 1 or > MaximumStepExecutions || value.Depth is < 1 or > 8)
                throw Bad(TargetedMacroPlanProblem.ResourceLimit);
            return value;
        }

        public MacroSettings Create() => new()
        {
            PlaybackSpeed = Speed,
            RepeatCount = Repeats,
            RepeatUntilStopped = false,
            MaxRuntime = Runtime,
            MaxIterations = Iterations,
            MaxLoopDepth = Depth,
            IsActive = true
        };
    }

    private enum StepKind
    {
        Key,
        Delay,
        Click,
        Move,
        Wheel
    }

    private sealed record FrozenStep(Guid Id, bool Enabled, StepKind Type, int Key, string Stroke, TimeSpan Timestamp, int DelayMs, TimeSpan Duration, int X = 0, int Y = 0, int OffsetX = 0, int OffsetY = 0, MouseButton Button = MouseButton.Left, int Clicks = 1, int RecordedWidth = 0, int RecordedHeight = 0, Point? ScreenPosition = null, int Wheel = 0)
    {
        public void Simulate(HashSet<int> holds, ref int peak)
        {
            int id = Type == StepKind.Key ? Key : Type == StepKind.Click ? Button switch
            {
                MouseButton.Left => 256,
                MouseButton.Right => 257,
                _ => 258
            } : 0;
            if (id == 0)
                return;
            if (Stroke == "Up")
            {
                if (!holds.Remove(id))
                    throw Bad(TargetedMacroPlanProblem.UnsupportedInput);
            }
            else
            {
                holds.Add(id);
                peak = Math.Max(peak, holds.Count);
                if (Stroke == "Press")
                    holds.Remove(id);
            }
        }

        public TargetedMacroSignals Required => Type switch
        {
            StepKind.Move => TargetedMacroSignals.ClientMove,
            StepKind.Wheel => TargetedMacroSignals.VerticalWheel,
            StepKind.Click => (Button switch
            {
                MouseButton.Left => TargetedMacroSignals.LeftButton,
                MouseButton.Right => TargetedMacroSignals.RightButton,
                _ => TargetedMacroSignals.MiddleButton
            }) | (Stroke == "Press" ? TargetedMacroSignals.ClientMove : 0),
            _ => 0
        };

        public static FrozenStep Copy(MacroStep source)
        {
            FrozenStep value = source switch
            {
                KeyPressStep key when !key.IsUnicode && key.KeyCode is >= 1 and <= 254 => new(source.StepId, source.IsEnabled, StepKind.Key, key.KeyCode, StrokeValue(key.Stroke), key.RelativeTimestamp, key.DelayAfterMs, TimeSpan.Zero),
                DelayStep delay => new(source.StepId, source.IsEnabled, StepKind.Delay, 0, "", TimeSpan.Zero, 0, delay.Duration),
                ClickStep click when click.IsClientRelative && Empty(click.TargetWindowName) && Empty(click.XVariable) && Empty(click.YVariable) && click.Button is MouseButton.Left or MouseButton.Right or MouseButton.Middle && click.ClickCount is 1 or 2 => new(source.StepId, source.IsEnabled, StepKind.Click, 0, StrokeValue(click.Stroke), click.RelativeTimestamp, click.DelayAfterMs, TimeSpan.Zero, click.Position.X, click.Position.Y, click.OffsetX, click.OffsetY, click.Button, click.ClickCount, click.RecordedClientWidth, click.RecordedClientHeight),
                MouseMoveStep move when move.IsClientRelative && !move.IsRelative && move.DurationMs == 0 && Empty(move.TargetWindowName) && Empty(move.XVariable) && Empty(move.YVariable) => new(source.StepId, source.IsEnabled, StepKind.Move, 0, "", move.RelativeTimestamp, 0, TimeSpan.Zero, move.Position.X, move.Position.Y, RecordedWidth: move.RecordedClientWidth, RecordedHeight: move.RecordedClientHeight, ScreenPosition: move.ScreenPosition),
                MouseWheelStep wheel when wheel.DeltaX == 0 && wheel.DeltaY is >= -12000 and <= 12000 && wheel.DeltaY != 0 => new(source.StepId, source.IsEnabled, StepKind.Wheel, 0, "", wheel.RelativeTimestamp, 0, TimeSpan.Zero, Wheel: wheel.DeltaY),
                KeyPressStep or ClickStep or MouseMoveStep or MouseWheelStep => throw Bad(TargetedMacroPlanProblem.UnsupportedInput),
                _ => throw Bad(TargetedMacroPlanProblem.UnsupportedStep)};
            if (value.Timestamp < TimeSpan.Zero || value.Timestamp > MaximumRuntime || value.DelayMs is < 0 or > 60000 || value.Duration < TimeSpan.Zero || value.Duration > MaximumRuntime || value.X is < 0 or >= MaximumClientDimension || value.Y is < 0 or >= MaximumClientDimension || value.OffsetX is < -MaximumClientDimension or > MaximumClientDimension || value.OffsetY is < -MaximumClientDimension or > MaximumClientDimension || value.RecordedWidth is < 0 or > MaximumClientDimension || value.RecordedHeight is < 0 or > MaximumClientDimension || (value.RecordedWidth == 0) != (value.RecordedHeight == 0))
                throw Bad(TargetedMacroPlanProblem.ResourceLimit);
            int x = value.X + value.OffsetX, y = value.Y + value.OffsetY;
            if (x < 0 || y < 0 || x >= MaximumClientDimension || y >= MaximumClientDimension || value.RecordedWidth > 0 && (x >= value.RecordedWidth || y >= value.RecordedHeight))
                throw Bad(TargetedMacroPlanProblem.UnsupportedInput);
            return value;
        }

        private static bool Empty(string? value) => string.IsNullOrEmpty(value);
        private static string StrokeValue(string? value) => value is "Press" or "Down" or "Up" ? value : throw Bad(TargetedMacroPlanProblem.UnsupportedInput);
        public MacroStep Create() => Type switch
        {
            StepKind.Key => new KeyPressStep
            {
                StepId = Id,
                IsEnabled = Enabled,
                KeyCode = Key,
                Stroke = Stroke,
                RelativeTimestamp = Timestamp,
                DelayAfterMs = DelayMs
            },
            StepKind.Delay => new DelayStep
            {
                StepId = Id,
                IsEnabled = Enabled,
                Duration = Duration
            },
            StepKind.Click => new ClickStep
            {
                StepId = Id,
                IsEnabled = Enabled,
                Position = new(X, Y),
                OffsetX = OffsetX,
                OffsetY = OffsetY,
                Button = Button,
                ClickCount = Clicks,
                Stroke = Stroke,
                IsClientRelative = true,
                RecordedClientWidth = RecordedWidth,
                RecordedClientHeight = RecordedHeight,
                RelativeTimestamp = Timestamp,
                DelayAfterMs = DelayMs
            },
            StepKind.Move => new MouseMoveStep
            {
                StepId = Id,
                IsEnabled = Enabled,
                Position = new(X, Y),
                ScreenPosition = ScreenPosition,
                IsClientRelative = true,
                RecordedClientWidth = RecordedWidth,
                RecordedClientHeight = RecordedHeight,
                RelativeTimestamp = Timestamp
            },
            _ => new MouseWheelStep
            {
                StepId = Id,
                IsEnabled = Enabled,
                DeltaY = Wheel,
                RelativeTimestamp = Timestamp
            }
        };
    }
}
