using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Vision;

namespace MacroSuite.Core.Execution;

/// <summary>
/// Execution scope and ambient services passed to macro steps and rule conditions.
/// Evaluates abort/panic state at execution boundaries (Rule 11).
/// </summary>
public sealed class MacroContext
{
    /// <summary>Activation authority supplied by the application composition owner.</summary>
    public Licensing.IActivationService? ActivationService { get; init; }

    private long _runtimeOrigin = System.Diagnostics.Stopwatch.GetTimestamp();
    private TimeSpan _initialRuntime;
    private DateTime _startedAtUtc = DateTime.UtcNow;
    private int _loopDepth;
    private int _blockDepth;
    private NavigationRequest? _navigation;
    private int _controlFlowSignal;
    private ColorDetectionResult? _lastColorDetection;
    private VisualMatchDiagnostics? _lastVisualDiagnostics;
    /// <summary>Unique execution identifier for this run.</summary>
    public Guid RunId { get; init; } = Guid.NewGuid();

    /// <summary>Macro execution controller governing pause, stop, and panic.</summary>
    public IMacroControl Control { get; init; }

    /// <summary>Centralized screen capture service.</summary>
    public IScreenCaptureService CaptureService { get; init; }

    /// <summary>Controlled OS input simulation player (Rule 4).</summary>
    public IInputPlayer? InputPlayer { get; init; }

    /// <summary>
    /// Optional 1 ms OS timer period for high-rate camera sweeps.
    /// Null keeps the default Windows ~15.6 ms sleep quantum.
    /// </summary>
    public ITimerResolution? TimerResolution { get; init; }

    /// <summary>Image template matcher (optional; null if not available).</summary>
    public IImageMatcher? ImageMatcher { get; init; }

    /// <summary>Licensing feature gate boundary (Rule 18).</summary>
    public Licensing.IFeatureGate? FeatureGate { get; init; }

    /// <summary>Usage telemetry and spend cap enforcement tracker.</summary>
    public Usage.IUsageTracker? UsageTracker { get; init; }

    /// <summary>Plugin host orchestrating third-party plugin extensions (Rule 18).</summary>
    public Extensibility.IPluginHost? PluginHost { get; init; }

    /// <summary>Multi-provider AI vision routing and testing coordinator (Rule 9, Rule 15).</summary>
    public AI.AIVisionRouter? VisionRouter { get; init; }

    /// <summary>Safety approval gate evaluating proposed AI actions before execution (Rule 8).</summary>
    public AI.IApprovalGate? ApprovalGate { get; init; }

    /// <summary>Structured, asynchronous audit logger tracking AI operations (Rule 10, Rule 18).</summary>
    public Audit.IAuditLogger? AuditLogger { get; init; }

    /// <summary>Optional non-blocking execution trace sink for developer diagnostics.</summary>
    public IExecutionTraceSink? Trace { get; init; }

    /// <summary>Optional bounded async execution-status sink for the developer overlay.</summary>
    public IExecutionStatusSink? StatusSink { get; init; }

    /// <summary>Optional safety validator for AI actions; Settings supply text/confidence limits (Rule 8).</summary>
    public AI.AIActionValidator? ActionValidator { get; init; }

    /// <summary>Optional foreground-window query used by AI expected-window checks (Rule 3).</summary>
    public Abstractions.IForegroundWindowInfo? ForegroundWindow { get; init; }

    /// <summary>Optional window lookup used for client-relative click/move playback.</summary>
    public IWindowCoordinateService? WindowCoordinates { get; init; }

    public PlaybackSessionContext? PlaybackSession { get; init; }
    public Task DelayAsync(TimeSpan duration, CancellationToken ct = default)
        => PlaybackSession is { } session ? session.DelayAsync(duration, ct) : Task.Delay(duration, ct);

    /// <summary>Image encoder service converting raw captured frames to JPEG/PNG (Rule 2, Rule 3).</summary>
    public Abstractions.IImageEncoder? ImageEncoder { get; init; }

    /// <summary>Optional native-resolution click snap after AI coordinate mapping (Rule 8).</summary>
    public AI.IClickRefiner? ClickRefiner { get; init; }

    /// <summary>Explicit flag indicating whether autonomous mode is actively engaged by user.</summary>
    public bool AutonomousMode { get; init; }

    /// <summary>Upper bound for a human approval prompt.</summary>
    public TimeSpan ApprovalTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Playback speed multiplier affecting delay durations and recorded gaps (e.g. 2.0 = 2x speed).</summary>
    public double PlaybackSpeed { get; init; } = 1.0;

    /// <summary>Input-player options that honor the current playback speed.</summary>
    public PlaybackOptions ToPlaybackOptions(IntPtr targetWindow = default, int recordedClientWidth = 0, int recordedClientHeight = 0)
        => new()
        {
            SpeedMultiplier = PlaybackSpeed > 0.05 ? PlaybackSpeed : 1.0,
            InterpolateMouseMoves = false,
            Session = PlaybackSession,
            TargetWindowHandle = targetWindow,
            RecordedClientWidth = recordedClientWidth,
            RecordedClientHeight = recordedClientHeight
        };

    /// <summary>Thread-safe runtime variables scoped to this execution run.</summary>
    public MacroVariables Variables { get; } = new();

    /// <summary>Latest color-detection result for this run (not static/process-wide).</summary>
    public ColorDetectionResult? LastColorDetection => _lastColorDetection;

    /// <summary>Latest perceptual visual-match diagnostics for this run.</summary>
    public VisualMatchDiagnostics? LastVisualDiagnostics => _lastVisualDiagnostics;

    /// <summary>
    /// Records run-scoped color diagnostics and last-detection variables for the current action.
    /// </summary>
    public void RecordDetection(ColorDetectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _lastColorDetection = result;
        Variables.Set(MacroVariableNames.LastDetection, result);
        Variables.Set(MacroVariableNames.LastDetectedConfidence, result.Confidence);
        Variables.Set(MacroVariableNames.LastDetectedPercentage, result.MatchPercentage);
        Variables.Set(MacroVariableNames.LastDetectedDistance, result.Distance);
        if (result.BestMatchPoint is { } point)
        {
            Variables.Set(MacroVariableNames.LastDetectedX, point.X);
            Variables.Set(MacroVariableNames.LastDetectedY, point.Y);
            Variables.Set(MacroVariableNames.LastDetectedXAlias, point.X);
            Variables.Set(MacroVariableNames.LastDetectedYAlias, point.Y);
            Variables.Set(MacroVariableNames.LastDetectionX, point.X);
            Variables.Set(MacroVariableNames.LastDetectionY, point.Y);
        }

        Variables.Set(MacroVariableNames.LastDetectionConfidence, result.Confidence);
        Variables.Set(MacroVariableNames.LastDetectionPercentage, result.MatchPercentage);
        Variables.Set(MacroVariableNames.LastDetectionDistance, result.Distance);

        if (result.MatchingBounds is { } bounds)
        {
            Variables.Set(MacroVariableNames.LastDetectedRegionX, bounds.X);
            Variables.Set(MacroVariableNames.LastDetectedRegionY, bounds.Y);
            Variables.Set(MacroVariableNames.LastDetectedRegionWidth, bounds.Width);
            Variables.Set(MacroVariableNames.LastDetectedRegionHeight, bounds.Height);
            Variables.Set(MacroVariableNames.LastDetectionRegionX, bounds.X);
            Variables.Set(MacroVariableNames.LastDetectionRegionY, bounds.Y);
            Variables.Set(MacroVariableNames.LastDetectionRegionWidth, bounds.Width);
            Variables.Set(MacroVariableNames.LastDetectionRegionHeight, bounds.Height);
        }
        else if (!result.CaptureRegion.IsEmpty)
        {
            Variables.Set(MacroVariableNames.LastDetectedRegionX, result.CaptureRegion.X);
            Variables.Set(MacroVariableNames.LastDetectedRegionY, result.CaptureRegion.Y);
            Variables.Set(MacroVariableNames.LastDetectedRegionWidth, result.CaptureRegion.Width);
            Variables.Set(MacroVariableNames.LastDetectedRegionHeight, result.CaptureRegion.Height);
            Variables.Set(MacroVariableNames.LastDetectionRegionX, result.CaptureRegion.X);
            Variables.Set(MacroVariableNames.LastDetectionRegionY, result.CaptureRegion.Y);
            Variables.Set(MacroVariableNames.LastDetectionRegionWidth, result.CaptureRegion.Width);
            Variables.Set(MacroVariableNames.LastDetectionRegionHeight, result.CaptureRegion.Height);
        }

        PublishStatus(new ExecutionStatusSnapshot
        {
            RunId = RunId,
            Phase = "ColorDetection",
            Detection = result,
            Visual = _lastVisualDiagnostics,
            Checks = 1,
            ConsecutiveTrue = result.Matched ? 1 : 0,
            ConsecutiveFalse = result.Matched ? 0 : 1,
            Elapsed = result.DetectionTime,
            Message = result.Matched ? "Color matched." : "Color not matched."
        });
    }

    /// <summary>
    /// Records run-scoped perceptual matcher diagnostics. Replaces process-wide last-result state.
    /// </summary>
    public void RecordVisualDiagnostics(VisualMatchDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        _lastVisualDiagnostics = diagnostics;
    }

    /// <summary>
    /// Non-blocking status publish. Drops the snapshot when the sink is full (Rule 14).
    /// </summary>
    public void PublishStatus(ExecutionStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        StatusSink?.TryWrite(snapshot with { RunId = RunId });
    }

    /// <summary>Token signaled on normal stop.</summary>
    public CancellationToken StopToken => Control.StopToken;

    /// <summary>Token signaled immediately on panic stop (Rule 11).</summary>
    public CancellationToken PanicToken => Control.PanicToken;

    /// <summary>Timestamp when execution began.</summary>
    public DateTime StartedAtUtc
    {
        get => _startedAtUtc;
        init
        {
            _startedAtUtc = value;
            _initialRuntime = DateTime.UtcNow - value;
            _runtimeOrigin = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Monotonic run duration, unaffected by later system-clock adjustments.</summary>
    public TimeSpan ElapsedRuntime => _initialRuntime + System.Diagnostics.Stopwatch.GetElapsedTime(_runtimeOrigin);

    /// <summary>
    /// Maximum allowed runtime before forced termination.
    /// Zero or negative means no wall-clock cap.
    /// </summary>
    public TimeSpan MaxRuntime { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// When false, <see cref="MaxRuntime"/> is not applied (repeat-until-stopped).
    /// Stop and Panic still abort immediately.
    /// </summary>
    public bool EnforceRuntimeCap { get; init; } = true;

    /// <summary>Maximum iterations allowed for unbounded loops.</summary>
    public int MaxIterations { get; init; } = 100_000;

    /// <summary>Maximum allowed loop nesting depth.</summary>
    public int MaxLoopDepth { get; init; } = 8;

    public int CurrentLoopDepth => Volatile.Read(ref _loopDepth);

    public int CurrentBlockDepth => Volatile.Read(ref _blockDepth);
    public NavigationRequest? PendingNavigation => Volatile.Read(ref _navigation);

    public void RequestNavigation(NavigationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (IsAborted) return;
        Interlocked.CompareExchange(ref _navigation, request, null);
        WriteTrace("ControlFlow", $"Requested {request.Kind}: {request.Reason}", actionId: request.SourceStepId, data: request);
    }

    public NavigationRequest? ConsumeNavigation() => Interlocked.Exchange(ref _navigation, null);

    public IDisposable EnterBlock()
    {
        Interlocked.Increment(ref _blockDepth);
        return new BlockDepthScope(this);
    }

    public ControlFlowSignal PendingControlFlow
        => (ControlFlowSignal)Volatile.Read(ref _controlFlowSignal);

    /// <summary>
    /// True when a positive <see cref="MaxRuntime"/> has elapsed.
    /// Zero / negative runtime means unlimited (repeat-until-stopped).
    /// </summary>
    public bool HasExceededMaxRuntime =>
        EnforceRuntimeCap
        && MaxRuntime > TimeSpan.Zero
        && ElapsedRuntime > MaxRuntime;

    /// <summary>
    /// Checks whether execution has been terminated or timed out.
    /// Checked at every step and condition evaluation boundary (Rule 11).
    /// </summary>
    public bool IsAborted =>
        Control.PanicToken.IsCancellationRequested ||
        Control.StopToken.IsCancellationRequested ||
        HasExceededMaxRuntime;

    public void WriteTrace(
        string category,
        string message,
        ExecutionTraceLevel level = ExecutionTraceLevel.Information,
        Guid? actionId = null,
        object? data = null,
        Exception? exception = null)
    {
        var sink = Trace;
        if (sink is null || !sink.IsEnabled)
            return;

        sink.Write(new ExecutionTraceEvent(
            DateTimeOffset.UtcNow,
            RunId,
            actionId,
            category,
            message,
            level,
            data,
            exception));
    }

    public IDisposable EnterLoop(Guid actionId)
    {
        int depth = Interlocked.Increment(ref _loopDepth);
        if (depth > MaxLoopDepth)
        {
            Interlocked.Decrement(ref _loopDepth);
            throw new InvalidOperationException(
                $"Control-flow nesting exceeded the configured maximum depth ({MaxLoopDepth}).");
        }

        WriteTrace("ControlFlow", $"Entered loop depth {depth}.", actionId: actionId);
        return new LoopDepthScope(this);
    }

    public void SignalControlFlow(ControlFlowSignal signal)
    {
        if (signal == ControlFlowSignal.None)
            return;
        Volatile.Write(ref _controlFlowSignal, (int)signal);
    }

    public ControlFlowSignal ConsumeControlFlow()
        => (ControlFlowSignal)Interlocked.Exchange(ref _controlFlowSignal, (int)ControlFlowSignal.None);

    private sealed class LoopDepthScope : IDisposable
    {
        private MacroContext? _owner;

        public LoopDepthScope(MacroContext owner) => _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null)
                Interlocked.Decrement(ref owner._loopDepth);
        }
    }

    private sealed class BlockDepthScope(MacroContext owner) : IDisposable
    {
        private MacroContext? _owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is not null) Interlocked.Decrement(ref current._blockDepth);
        }
    }

    /// <summary>
    /// Constructs a <see cref="MacroContext"/> with capture service and controller.
    /// </summary>
    public MacroContext(IScreenCaptureService captureService, IMacroControl? control = null)
    {
        CaptureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
        Control = control ?? new MacroController();
    }
}

public enum ControlFlowSignal
{
    None = 0,
    Break = 1,
    Continue = 2
}
