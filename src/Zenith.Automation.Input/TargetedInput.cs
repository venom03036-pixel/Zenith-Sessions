namespace Zenith.Sessions.Core;

/// <summary>A registered native-window incarnation, not proof of an account or permission to automate it.</summary>
public sealed record TargetedInputTarget(Guid RegistrationId, int ProcessId, long ProcessStartUtcTicks, nint Window,
    PixelRectangle ContentBounds, long GeometryGeneration)
{
    public bool IsValid => RegistrationId != Guid.Empty && ProcessId > 0 && ProcessStartUtcTicks > 0 && ProcessStartUtcTicks <= DateTime.MaxValue.Ticks
        && Window != 0 && Window != (nint)0xffff && ContentBounds.IsValid && GeometryGeneration > 0;
}

// KeepAwake is appended (R-instances-17): one F13 press per client every few minutes, through this owner like any other input.
public enum TargetedInputPurpose { MacroPlayback, BroadcastFollower, OwnedWindowDiagnostic, KeepAwake }
public enum TargetedInputKind { KeyDown, KeyUp, LeftDown, LeftUp, RightDown, RightUp, MiddleDown, MiddleUp, Move, Wheel }
public enum TargetedInputRunState { Running, Pausing, Paused, Resuming, Stopping, Stopped, Faulted, Clearing }
public enum TargetedInputValidation { Current, GeometryChanged, Gone, Unavailable }
public enum TargetedInputSubmission { Queued, Rejected, Uncertain }
public enum TargetedInputDispatchStatus { Submitted, Busy, Invalid, Inactive, Cancelled, Paused, Faulted }
/// <summary>
/// Why a run's owner stops it and releases its lease. The set is closed and the owner keeps each reason's text, so
/// another assembly can say why without writing the explanation or reaching the owner's internals.
/// </summary>
public enum TargetedInputStopReason { User, EmergencyStop, Completed, RuntimeLimit, AfterFailure, CompanionStopped, RunnerClosed, NotStarted, KeepAwakeSent }

/// <summary>Client-content physical coordinates; no screen-coordinate reinterpretation or pointer/camera conversion.</summary>
public readonly record struct TargetedInputSignal(TargetedInputKind Kind, int Key = 0, int X = 0, int Y = 0, int WheelDelta = 0)
{
    public bool IsValid(TargetedInputTarget target) => Enum.IsDefined(Kind) &&
        (Kind is TargetedInputKind.KeyDown or TargetedInputKind.KeyUp
            ? Key is >= 1 and <= 254 && X == 0 && Y == 0 && WheelDelta == 0
            : Key == 0 && X >= 0 && Y >= 0 && X < target.ContentBounds.Width && Y < target.ContentBounds.Height
                && (Kind == TargetedInputKind.Wheel ? WheelDelta is >= -12000 and <= 12000 && WheelDelta != 0 : WheelDelta == 0));
    internal int HoldId => Kind is TargetedInputKind.KeyDown or TargetedInputKind.KeyUp ? Key : Kind switch
    { TargetedInputKind.LeftDown or TargetedInputKind.LeftUp => 256, TargetedInputKind.RightDown or TargetedInputKind.RightUp => 257,
        TargetedInputKind.MiddleDown or TargetedInputKind.MiddleUp => 258, _ => 0 };
    internal bool IsDown => Kind is TargetedInputKind.KeyDown or TargetedInputKind.LeftDown or TargetedInputKind.RightDown or TargetedInputKind.MiddleDown;
    internal bool IsUp => Kind is TargetedInputKind.KeyUp or TargetedInputKind.LeftUp or TargetedInputKind.RightUp or TargetedInputKind.MiddleUp;
    internal TargetedInputSignal Release(int x, int y) => Kind switch
    { TargetedInputKind.KeyDown => new(TargetedInputKind.KeyUp, Key), TargetedInputKind.LeftDown => new(TargetedInputKind.LeftUp, X: x, Y: y),
        TargetedInputKind.RightDown => new(TargetedInputKind.RightUp, X: x, Y: y), TargetedInputKind.MiddleDown => new(TargetedInputKind.MiddleUp, X: x, Y: y), _ => this };
}

[Flags]
public enum TargetedInputModifiers { None = 0, Shift = 1, Control = 2, LeftButton = 4, RightButton = 8, MiddleButton = 16 }
public sealed record TargetedInputCommand(Guid RunId, long Sequence, TargetedInputTarget Target, TargetedInputSignal Signal,
    bool Cleanup, bool RepeatedDown, TargetedInputModifiers Modifiers);
public sealed record TargetedInputRunSnapshot(Guid RunId, TargetedInputTarget Target, TargetedInputPurpose Purpose,
    TargetedInputRunState State, long Generation, long LastSequence, int LogicalHolds, int SubmittedHolds, string Explanation);
public sealed record TargetedInputDispatchResult(TargetedInputDispatchStatus Status, int SubmittedCount, int UncertainCount,
    TargetedInputRunSnapshot Run);
public sealed record TargetedInputOpenResult(TargetedInputRun? Run, string Explanation);
public sealed record TargetedInputLimits(int MaximumRuns = 32, int MaximumBatch = 256, int MaximumHolds = 64)
{
    public TargetedInputLimits Validated()
    {
        if (MaximumRuns is < 1 or > 128 || MaximumBatch is < 1 or > 1024 || MaximumHolds is < 1 or > 258)
            throw new ArgumentOutOfRangeException(nameof(TargetedInputLimits));
        return this;
    }
}

/// <summary>
/// Additional admission for one captured owner. The owner identity is immutable for the run.
/// Both checks must be nonblocking, lock-free and nonthrowing. Ordinary submission requires
/// live permission, while cleanup retains authority for the exact owner after Stop.
/// </summary>
public interface ITargetedInputAdmission
{
    bool CanSubmit { get; }
    bool OwnsCleanup { get; }
}

/// <summary>
/// A designated target-directed executor, never a fallback to global input. Validate identity/incarnation on every
/// call and geometry on ordinary calls. Gone must mean the original registered recipient is definitively gone.
/// Queued is not an acknowledgement of consumption. Exceptions/cancellation during submission mean Uncertain.
/// Implementations must check admission immediately before their native permission/commit point. A call that
/// passed that point before Stop may finish afterward; already submitted input cannot be recalled.
/// </summary>
public interface ITargetedInputTransport
{
    ValueTask<TargetedInputValidation> ValidateAsync(TargetedInputTarget target, bool cleanup, CancellationToken ct);
    ValueTask<TargetedInputSubmission> SubmitAsync(TargetedInputCommand command, Func<bool> admissionOpen, CancellationToken ct);
}
