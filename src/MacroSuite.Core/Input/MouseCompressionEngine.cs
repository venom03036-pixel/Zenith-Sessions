using System.Diagnostics;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Input;

/// <summary>
/// Reduces the number of <see cref="InputEventType.MouseMove"/> events in a recorded stream
/// while preserving semantic accuracy for playback.
/// </summary>
/// <remarks>
/// <para>
/// <b>Algorithm:</b> The engine holds at most one "pending" move event. When a new move arrives,
/// the engine decides whether to emit the pending event or silently replace it (suppress):
/// <list type="bullet">
///   <item><description>
///     If the cursor has not moved far enough (<see cref="CompressionOptions.MinDistancePx"/>)
///     <em>and</em> not enough time has elapsed (<see cref="CompressionOptions.MinTimeMs"/>)
///     since the last emitted position → suppress the pending move, replace with the new one.
///   </description></item>
///   <item><description>
///     Otherwise, if the pending point represents a direction change (cross-product sign ≠ 0)
///     and <see cref="CompressionOptions.PreserveDirectionChanges"/> is enabled → always emit.
///   </description></item>
///   <item><description>
///     Otherwise, if both thresholds are exceeded → emit.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <b>Critical invariant:</b> The final <see cref="InputEventType.MouseMove"/> immediately
/// preceding any <see cref="InputEventType.MouseDown"/> is <em>always</em> emitted first.
/// This guarantees the click lands at the exact recorded coordinate.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. Use one instance per recording consumer.
/// </para>
/// </remarks>
public sealed class MouseCompressionEngine
{
    private readonly CompressionOptions _options;
    private readonly long _minTimeTicks;
    private readonly long _minDistSq;
    private readonly long _pauseTicks;

    // State for DistanceAndTime mode: at most one pending move event held between calls
    private InputEvent? _pending;

    // State for PathInterpolation mode: stroke start, latest move, and via points count
    private InputEvent? _strokeStart;
    private InputEvent? _strokeLatest;
    private long _strokeLastTick;
    private int _strokeViaPointsCount;

    // Position and time of the last event we actually emitted
    private int _lastEmittedX;
    private int _lastEmittedY;
    private long _lastEmittedTick;

    /// <summary>
    /// Total number of events processed (including non-move events).
    /// </summary>
    public long ProcessedCount { get; private set; }

    /// <summary>
    /// Total number of <see cref="InputEventType.MouseMove"/> events suppressed.
    /// </summary>
    public long SuppressedCount { get; private set; }

    public MouseCompressionEngine(CompressionOptions? options = null)
    {
        _options = (options ?? CompressionOptions.Default).Validate();

        // Pre-compute threshold values in tick / squared-pixel units to avoid
        // per-event conversions on the hot path.
        _minTimeTicks = (long)(_options.MinTimeMs * Stopwatch.Frequency / 1000.0);
        _minDistSq    = (long)_options.MinDistancePx * _options.MinDistancePx;
        _pauseTicks   = (long)(100.0 * Stopwatch.Frequency / 1000.0); // 100ms pause separates strokes
    }

    /// <summary>
    /// Processes a single <see cref="InputEvent"/> and appends zero or more events to
    /// <paramref name="output"/>.
    /// </summary>
    /// <param name="ev">The event to process. Passed by reference to avoid struct copy.</param>
    /// <param name="output">Destination list. The caller owns this list.</param>
    public void Process(in InputEvent ev, List<InputEvent> output)
    {
        ProcessedCount++;

        if (ev.Type == InputEventType.MouseMove)
        {
            if (_options.SamplingMode == MouseSamplingMode.PathInterpolation)
            {
                ProcessMovePathInterpolation(in ev, output);
            }
            else
            {
                ProcessMove(in ev, output);
            }
            return;
        }

        // Non-move event: flush the pending move or stroke first.
        // This is the critical invariant — the move preceding a MouseDown is never dropped.
        FlushPending(output);
        output.Add(ev);
    }

    /// <summary>
    /// Flushes any held pending move event into <paramref name="output"/>.
    /// Call this after the last event in a recording session to ensure the
    /// final cursor position is captured.
    /// </summary>
    public void Flush(List<InputEvent> output)
    {
        FlushPending(output);
    }

    /// <summary>Resets engine state. Use when starting a new recording session.</summary>
    public void Reset()
    {
        _pending               = null;
        _strokeStart           = null;
        _strokeLatest          = null;
        _strokeLastTick        = 0;
        _strokeViaPointsCount  = 0;
        _lastEmittedX          = 0;
        _lastEmittedY          = 0;
        _lastEmittedTick       = 0;
        ProcessedCount         = 0;
        SuppressedCount        = 0;
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private void ProcessMove(in InputEvent ev, List<InputEvent> output)
    {
        if (!_pending.HasValue)
        {
            // First move — hold it
            _pending = ev;
            return;
        }

        var pending = _pending.Value;

        if (ShouldEmitPending(in pending, in ev))
        {
            EmitPending(in pending, output);
        }
        else
        {
            // Suppress the pending move; the new position supersedes it
            SuppressedCount++;
        }

        _pending = ev;
    }

    private void ProcessMovePathInterpolation(in InputEvent ev, List<InputEvent> output)
    {
        if (!_strokeStart.HasValue)
        {
            // Begin a new motion stroke: always emit the initial start point
            _strokeStart          = ev;
            _strokeLatest         = ev;
            _strokeLastTick       = ev.TimestampTicks;
            _strokeViaPointsCount = 0;
            EmitPending(in ev, output);
            return;
        }

        long elapsedSinceLastTick = ev.TimestampTicks - _strokeLastTick;

        // If mouse paused for > 100ms, close current stroke and start fresh
        if (elapsedSinceLastTick > _pauseTicks)
        {
            if (_strokeLatest.HasValue && (_strokeLatest.Value.X != _lastEmittedX || _strokeLatest.Value.Y != _lastEmittedY))
            {
                var finalPoint = _strokeLatest.Value;
                EmitPending(in finalPoint, output);
            }

            _strokeStart          = ev;
            _strokeLatest         = ev;
            _strokeLastTick       = ev.TimestampTicks;
            _strokeViaPointsCount = 0;
            EmitPending(in ev, output);
            return;
        }

        // Test if the current latest point is a viable via-point before replacing it
        if (_strokeLatest.HasValue && _strokeViaPointsCount < _options.MaxViaPoints)
        {
            var pending = _strokeLatest.Value;
            long dx = pending.X - _lastEmittedX;
            long dy = pending.Y - _lastEmittedY;
            long distSq = dx * dx + dy * dy;

            if (distSq >= _minDistSq)
            {
                // Check if direction change occurred
                long v1x = pending.X - _lastEmittedX;
                long v1y = pending.Y - _lastEmittedY;
                long v2x = ev.X - pending.X;
                long v2y = ev.Y - pending.Y;
                long cross = v1x * v2y - v1y * v2x;

                if (cross != 0)
                {
                    EmitPending(in pending, output);
                    _strokeViaPointsCount++;
                }
                else
                {
                    SuppressedCount++;
                }
            }
            else
            {
                SuppressedCount++;
            }
        }
        else
        {
            SuppressedCount++;
        }

        _strokeLatest   = ev;
        _strokeLastTick = ev.TimestampTicks;
    }

    private void FlushPending(List<InputEvent> output)
    {
        if (_options.SamplingMode == MouseSamplingMode.PathInterpolation)
        {
            if (_strokeLatest.HasValue)
            {
                var ev = _strokeLatest.Value;
                if (ev.X != _lastEmittedX || ev.Y != _lastEmittedY)
                {
                    EmitPending(in ev, output);
                }
                _strokeStart  = null;
                _strokeLatest = null;
            }
        }
        else if (_pending.HasValue)
        {
            var ev = _pending.Value; // store in local — Nullable.Value cannot be passed as 'in'
            EmitPending(in ev, output);
            _pending = null;
        }
    }

    private void EmitPending(in InputEvent ev, List<InputEvent> output)
    {
        output.Add(ev);
        _lastEmittedX    = ev.X;
        _lastEmittedY    = ev.Y;
        _lastEmittedTick = ev.TimestampTicks;
    }

    /// <summary>
    /// Determines whether the pending move should be emitted before being replaced by
    /// <paramref name="next"/>.
    /// </summary>
    /// <remarks>
    /// Emission criteria (both must be true for suppression):
    /// <list type="number">
    ///   <item><description>
    ///     Time since last emission &lt; <see cref="CompressionOptions.MinTimeMs"/>
    ///   </description></item>
    ///   <item><description>
    ///     Distance from last emitted position &lt; <see cref="CompressionOptions.MinDistancePx"/>
    ///   </description></item>
    /// </list>
    /// If either threshold is exceeded, the pending move is emitted. Additionally, if
    /// <see cref="CompressionOptions.PreserveDirectionChanges"/> is enabled and the pending
    /// point represents an inflection (cross-product of the two vectors ≠ 0), it is always emitted.
    /// </remarks>
    private bool ShouldEmitPending(in InputEvent pending, in InputEvent next)
    {
        // --- Time check (from last emitted, not from pending) ---
        long ticksSinceEmit = next.TimestampTicks - _lastEmittedTick;
        bool timeExceeded   = ticksSinceEmit >= _minTimeTicks;

        // --- Distance check (from last emitted position to the *new* position) ---
        long dx = next.X - _lastEmittedX;
        long dy = next.Y - _lastEmittedY;
        bool distExceeded = dx * dx + dy * dy >= _minDistSq;

        // Suppress if both thresholds are NOT exceeded
        if (!timeExceeded && !distExceeded)
            return false;

        // --- Direction-change check at the pending point ---
        // Integer cross != 0 is true for almost every real mouse sample (1px jitter).
        // Require a meaningful turn (~15°) AND that the pending point itself moved
        // at least MinDistancePx from the last emitted position. Otherwise a 1.3s
        // mouse stroke records hundreds of MOVE steps and stalls the UI.
        if (_options.PreserveDirectionChanges)
        {
            long v1x = pending.X - _lastEmittedX;
            long v1y = pending.Y - _lastEmittedY;
            long v1sq = v1x * v1x + v1y * v1y;
            if (v1sq >= _minDistSq)
            {
                long v2x = next.X - pending.X;
                long v2y = next.Y - pending.Y;
                long v2sq = v2x * v2x + v2y * v2y;
                long cross = v1x * v2y - v1y * v2x;
                // |sin θ| = |cross| / (|v1||v2|). sin(15°) ≈ 0.2588; 0.2588² ≈ 0.0670.
                // cross² / (v1² v2²) >= 0.067  →  cross² * 10000 >= 670 * v1² * v2²
                if (v2sq > 0 && cross != 0 &&
                    cross * cross * 10000L >= 670L * v1sq * v2sq)
                {
                    return true;
                }
            }
        }

        // Emit if both thresholds exceeded on a straight-line segment
        return timeExceeded && distExceeded;
    }
}
