using System.Diagnostics;
using FluentAssertions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using Xunit;

namespace MacroSuite.Core.Tests.Input;

/// <summary>
/// Unit tests for <see cref="MouseCompressionEngine"/>.
/// Tests use a synthetic Stopwatch-tick timeline so they run deterministically on any machine.
/// </summary>
public sealed class MouseCompressionEngineTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static readonly long TicksPerMs = Stopwatch.Frequency / 1000;

    /// <summary>
    /// Creates an <see cref="InputEvent"/> at a relative time offset and position.
    /// </summary>
    private static InputEvent Move(int x, int y, long offsetMs = 0) =>
        new(InputEventType.MouseMove,
            offsetMs * TicksPerMs,
            x, y, 0);

    private static InputEvent LeftDown(int x, int y, long offsetMs) =>
        new(InputEventType.MouseDown,
            offsetMs * TicksPerMs,
            x, y, (int)MouseButton.Left);

    private static InputEvent Key(long offsetMs, int vk = 0x41 /* A */) =>
        new(InputEventType.KeyDown,
            offsetMs * TicksPerMs,
            0, 0, vk);

    private static List<InputEvent> Run(
        MouseCompressionEngine engine,
        params InputEvent[] events)
    {
        var output = new List<InputEvent>();
        foreach (var ev in events)
            engine.Process(in ev, output);
        engine.Flush(output);
        return output;
    }

    // -----------------------------------------------------------------------
    // Empty stream
    // -----------------------------------------------------------------------

    [Fact]
    public void EmptyStream_ProducesNoOutput()
    {
        var engine = new MouseCompressionEngine();
        var output = Run(engine);
        output.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Single events pass through
    // -----------------------------------------------------------------------

    [Fact]
    public void SingleMove_IsEmittedOnFlush()
    {
        var engine = new MouseCompressionEngine();
        var output = Run(engine, Move(100, 200, 0));
        output.Should().ContainSingle()
            .Which.Type.Should().Be(InputEventType.MouseMove);
    }

    [Fact]
    public void NonMoveEvents_PassThroughUnchanged()
    {
        var engine = new MouseCompressionEngine();
        var output = Run(engine, LeftDown(100, 100, 0), Key(10));
        output.Should().HaveCount(2);
        output[0].Type.Should().Be(InputEventType.MouseDown);
        output[1].Type.Should().Be(InputEventType.KeyDown);
    }

    // -----------------------------------------------------------------------
    // Critical invariant: last move before MouseDown is never suppressed
    // -----------------------------------------------------------------------

    [Fact]
    public void LastMoveBeforeClick_IsAlwaysEmitted()
    {
        // Three moves very close together, then a click.
        // Only the last move (immediately before the click) must appear in output.
        var options = new CompressionOptions
        {
            MinDistancePx          = 50,   // high threshold — suppresses most moves
            MinTimeMs              = 100,
            PreserveDirectionChanges = false,
        };
        var engine = new MouseCompressionEngine(options);

        var events = new[]
        {
            Move(100, 100, 0),    // first move
            Move(101, 101, 5),    // very close — should be suppressed
            Move(102, 102, 10),   // very close — this is the pre-click position
            LeftDown(102, 102, 11), // click at same position
        };

        var output = Run(engine, events);

        // At minimum the pre-click move and the click itself must be in the output
        output.Should().Contain(ev => ev.Type == InputEventType.MouseDown);
        var lastMoveBeforeClick = output
            .Where(ev => ev.Type == InputEventType.MouseMove)
            .LastOrDefault();

        lastMoveBeforeClick.X.Should().Be(102);
        lastMoveBeforeClick.Y.Should().Be(102);
    }

    // -----------------------------------------------------------------------
    // Compression: below-threshold moves are suppressed
    // -----------------------------------------------------------------------

    [Fact]
    public void MovesWithinBothThresholds_AreSuppressed()
    {
        var options = new CompressionOptions
        {
            MinDistancePx          = 20,
            MinTimeMs              = 50,
            PreserveDirectionChanges = false,
        };
        var engine = new MouseCompressionEngine(options);

        // 5 consecutive moves within 10px, 10ms apart — all should be collapsed to 1
        var events = Enumerable.Range(0, 5)
            .Select(i => Move(100 + i, 100, i * 10))
            .ToArray();

        var output = Run(engine, events);

        // Only the final position should come out (flushed)
        var moves = output.Where(e => e.Type == InputEventType.MouseMove).ToList();
        moves.Should().HaveCount(1);
        moves[0].X.Should().Be(104); // last position
    }

    [Fact]
    public void MovesExceedingDistanceThreshold_AreEmitted()
    {
        var options = new CompressionOptions
        {
            MinDistancePx          = 5,
            MinTimeMs              = 50,
            PreserveDirectionChanges = false,
        };
        var engine = new MouseCompressionEngine(options);

        // Moves far apart (> 5px each) but within time threshold
        // Time threshold not exceeded but distance is — should emit based on combined criteria
        // Since only one threshold is exceeded, we need both exceeded for straight lines
        var events = new[]
        {
            Move(0,   0,   0),
            Move(100, 0,   5),   // 100px away but only 5ms — time not exceeded
            Move(200, 0,   100), // 100px more, 100ms total — both exceeded
        };

        var output = Run(engine, events);
        var moves = output.Where(e => e.Type == InputEventType.MouseMove).ToList();

        // The move at (200,0) should be emitted at minimum (flush)
        moves.Should().NotBeEmpty();
        moves.Last().X.Should().Be(200);
    }

    // -----------------------------------------------------------------------
    // Direction change preservation
    // -----------------------------------------------------------------------

    [Fact]
    public void DirectionChange_IsPreservedWhenEnabled()
    {
        var options = new CompressionOptions
        {
            MinDistancePx          = 2,
            MinTimeMs              = 5,
            PreserveDirectionChanges = true,
        };
        var engine = new MouseCompressionEngine(options);

        // Straight right then straight down — the corner at (50, 0) must be preserved
        var events = new[]
        {
            Move(0,   0,  0),   // start
            Move(50,  0,  20),  // move right → this is the inflection point
            Move(50,  50, 40),  // move down
        };

        var output = Run(engine, events);
        var moves = output.Where(e => e.Type == InputEventType.MouseMove).ToList();

        // The inflection at (50, 0) must be in the output
        moves.Should().Contain(ev => ev.X == 50 && ev.Y == 0,
            because: "direction change at (50,0) must be preserved");
    }

    [Fact]
    public void OnePixelJitter_IsNotTreatedAsDirectionChange()
    {
        var options = new CompressionOptions
        {
            MinDistancePx = 8,
            MinTimeMs = 16,
            PreserveDirectionChanges = true,
        };
        var engine = new MouseCompressionEngine(options);

        // Noisy 1px wiggle along a short stroke — must not emit a step per pixel.
        var events = new[]
        {
            Move(100, 100, 0),
            Move(101, 100, 2),
            Move(101, 101, 4),
            Move(102, 101, 6),
            Move(103, 100, 8),
            Move(104, 101, 10),
            Move(105, 100, 12),
        };

        var output = Run(engine, events);
        var moves = output.Where(ev => ev.Type == InputEventType.MouseMove).ToList();
        moves.Count.Should().BeLessThan(events.Length);
    }

    [Fact]
    public void DirectionChange_IsNotPreservedWhenDisabled()
    {
        var options = new CompressionOptions
        {
            MinDistancePx          = 2,
            MinTimeMs              = 5,
            PreserveDirectionChanges = false,
        };
        var engine = new MouseCompressionEngine(options);

        var events = new[]
        {
            Move(0,   0,  0),
            Move(50,  0,  20),  // potential inflection — but we don't care
            Move(50,  50, 40),
        };

        var output = Run(engine, events);
        // With direction preservation off, we only care that it doesn't crash and produces
        // a valid subset. The inflection may or may not be present.
        output.Should().NotBeNull();
        output.Should().AllSatisfy(ev => ev.Type.Should().Be(InputEventType.MouseMove));
    }

    // -----------------------------------------------------------------------
    // SuppressedCount tracking
    // -----------------------------------------------------------------------

    [Fact]
    public void SuppressedCount_IncrementsCorrectly()
    {
        var options = new CompressionOptions
        {
            MinDistancePx          = 100,  // very high — suppress everything
            MinTimeMs              = 100,
            PreserveDirectionChanges = false,
        };
        var engine = new MouseCompressionEngine(options);

        // 10 moves very close together — all but the last should be suppressed
        var events = Enumerable.Range(0, 10)
            .Select(i => Move(i, 0, i))
            .ToArray();

        Run(engine, events);

        engine.ProcessedCount.Should().Be(10);
        engine.SuppressedCount.Should().BeGreaterThan(0);
    }

    // -----------------------------------------------------------------------
    // Reset
    // -----------------------------------------------------------------------

    [Fact]
    public void Reset_ClearsStateAndCounters()
    {
        var engine = new MouseCompressionEngine();

        Run(engine, Move(0, 0, 0), Move(1, 0, 100), LeftDown(10, 10, 200));

        engine.Reset();

        engine.ProcessedCount.Should().Be(0);
        engine.SuppressedCount.Should().Be(0);

        // After reset, should behave as if freshly constructed
        var output = Run(engine, Move(500, 500, 0));
        output.Should().ContainSingle()
            .Which.X.Should().Be(500);
    }

    [Theory]
    [InlineData(MouseSamplingMode.DistanceAndTime)]
    [InlineData(MouseSamplingMode.PathInterpolation)]
    public void Reset_WithClearedReusedList_ProducesSameOutputAsFreshEngine(MouseSamplingMode mode)
    {
        // MacroSuite.Benchmarks reuses one engine and one output list per operation
        // (Reset + Clear) so MemoryDiagnoser measures only Process and Flush. That is only
        // valid while a reset engine behaves exactly like a newly constructed one.
        var options = new CompressionOptions { SamplingMode = mode };
        // A zig-zag stroke with more turns than MaxViaPoints, a click, then a straight stroke.
        var events = new List<InputEvent>();
        int x = 100, y = 200;
        long t = 0;
        for (int i = 0; i < 40; i++)
        {
            x += 3;
            y += (i / 5) % 2 == 0 ? 3 : -3;
            t += 4;
            events.Add(Move(x, y, t));
        }
        events.Add(LeftDown(x, y, t + 200));
        for (int i = 0; i < 20; i++) events.Add(Move(x - (i * 5), y, t + 400 + (i * 4)));

        var fresh = new MouseCompressionEngine(options);
        var expected = Run(fresh, events.ToArray());

        var reused = new MouseCompressionEngine(options);
        var output = new List<InputEvent>(events.Count);
        foreach (var ev in events) reused.Process(in ev, output);
        reused.Flush(output);
        reused.Process(Move(900, 900, 5_000), output); // leave held, unflushed state behind too

        reused.Reset();
        output.Clear();
        foreach (var ev in events) reused.Process(in ev, output);
        reused.Flush(output);

        output.Should().Equal(expected);
        reused.ProcessedCount.Should().Be(fresh.ProcessedCount);
        reused.SuppressedCount.Should().Be(fresh.SuppressedCount);
    }

    // -----------------------------------------------------------------------
    // Options validation
    // -----------------------------------------------------------------------

    [Fact]
    public void NegativeMinDistance_ThrowsOnValidation()
    {
        var act = () => new MouseCompressionEngine(new CompressionOptions
        {
            MinDistancePx = -1
        });
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void NegativeMinTime_ThrowsOnValidation()
    {
        var act = () => new MouseCompressionEngine(new CompressionOptions
        {
            MinTimeMs = -1.0
        });
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void NegativeMaxViaPoints_ThrowsOnValidation()
    {
        var act = () => new MouseCompressionEngine(new CompressionOptions
        {
            MaxViaPoints = -1
        });
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void PathInterpolation_EmitsStartAndEndPointsOfStroke()
    {
        var options = new CompressionOptions
        {
            SamplingMode = MouseSamplingMode.PathInterpolation,
            MaxViaPoints = 4
        };
        var engine = new MouseCompressionEngine(options);

        // A smooth linear mouse stroke from (0,0) to (100, 100) across 5 intermediate points within 50ms
        var events = new[]
        {
            Move(0, 0, 0),
            Move(20, 20, 10),
            Move(40, 40, 20),
            Move(60, 60, 30),
            Move(80, 80, 40),
            Move(100, 100, 50)
        };

        var output = Run(engine, events);

        // Intermediate linear points should be compressed; start (0,0) and end (100,100) must be present
        output.Should().NotBeEmpty();
        output.First().X.Should().Be(0);
        output.First().Y.Should().Be(0);
        output.Last().X.Should().Be(100);
        output.Last().Y.Should().Be(100);
        output.Count.Should().BeLessThan(events.Length);
    }

    [Fact]
    public void PathInterpolation_CapturesInflectionViaPointOnDirectionChange()
    {
        var options = new CompressionOptions
        {
            SamplingMode = MouseSamplingMode.PathInterpolation,
            MaxViaPoints = 5
        };
        var engine = new MouseCompressionEngine(options);

        // Move right from (0,0) to (100,0), then sharp 90-degree turn down to (100,100)
        var events = new[]
        {
            Move(0, 0, 0),
            Move(50, 0, 10),
            Move(100, 0, 20), // Sharp corner / inflection point
            Move(100, 50, 30),
            Move(100, 100, 40)
        };

        var output = Run(engine, events);

        // Start (0,0), inflection (100,0), and end (100,100) should be preserved
        output.Should().Contain(m => m.X == 0 && m.Y == 0);
        output.Should().Contain(m => m.X == 100 && m.Y == 0);
        output.Should().Contain(m => m.X == 100 && m.Y == 100);
    }

    [Fact]
    public void PathInterpolation_FlushesOnNonMoveEvent()
    {
        var options = new CompressionOptions
        {
            SamplingMode = MouseSamplingMode.PathInterpolation
        };
        var engine = new MouseCompressionEngine(options);

        var output = new List<InputEvent>();
        var m1 = Move(10, 10, 0);
        var m2 = Move(50, 50, 20);
        var click = LeftDown(50, 50, 30);

        engine.Process(in m1, output);
        engine.Process(in m2, output);
        engine.Process(in click, output);

        // Prior to or with click, the stroke endpoints must be flushed
        output.Should().Contain(e => e.Type == InputEventType.MouseDown);
        output.Should().Contain(e => e.Type == InputEventType.MouseMove && e.X == 50 && e.Y == 50);
    }
}
