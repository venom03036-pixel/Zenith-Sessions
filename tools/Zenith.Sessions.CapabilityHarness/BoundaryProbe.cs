using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;

namespace Zenith.Sessions.Capabilities;

public sealed record BoundaryResult(string Name, bool Passed, string Detail,
    EvidenceOrigin Origin = EvidenceOrigin.ControlledBoundaryTest);

/// <summary>Exercises real Core code with invented targets and an in-memory sink. No native input is linked.</summary>
public static class BoundaryProbe
{
    public static async Task<IReadOnlyList<BoundaryResult>> RunAsync(CancellationToken ct)
    {
        var results = new List<BoundaryResult>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        using var targets = new InventedTargets();
        using var control = new MacroController();
        var target = targets.Resolve(new() { Mode = MacroTargetMode.SpecificWindow,
            ApplicationId = "invented", WindowTitle = "target" });
        await using var session = new PlaybackSessionContext(control, targets, target, token);
        var sink = new MemorySink();
        session.BindSink(sink);
        var keyDown = new InputEvent(InputEventType.KeyDown, Stopwatch.GetTimestamp(), 0, 0, 65);
        await session.EmitAsync(keyDown, token);
        results.Add(new("Focused baseline", sink.Events.Count == 1,
            "The actual playback session sent one event to the in-memory sink; no OS input was sent."));

        targets.Focus(20);
        await session.AwaitReleasedAsync(token);
        results.Add(new("Focus loss pauses and releases", control.IsPaused && sink.Events.Count == 2
            && sink.Events.Last() is { Type: InputEventType.KeyUp, Cleanup: true },
            "Invented focus moved to another target; Core paused and released its owned key in memory."));
        var pending = session.EmitAsync(new(InputEventType.MouseDown, Stopwatch.GetTimestamp(), 2, 2, 0), token).AsTask();
        results.Add(new("Unfocused emission is gated", !pending.IsCompleted && sink.Events.Count == 2,
            "An unfocused mouse event remains blocked at the existing pause gate."));
        targets.Focus(10);
        results.Add(new("Refocus does not silently resume", control.IsPaused && sink.Events.Count == 2,
            "Refocusing the invented target leaves playback manually paused."));
        await control.StopAsync();
        bool cancelled = false;
        try { await pending.WaitAsync(token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { cancelled = true; }
        results.Add(new("Stop cancels blocked emission", cancelled && sink.Events.Count == 2,
            $"Pending emission cancelled: {cancelled}; sink event count after Stop: {sink.Events.Count} (expected 2)."));

        var before = targets.Inspect(target);
        targets.MoveTarget();
        var moved = targets.Inspect(target);
        results.Add(new("Geometry generation changes", moved.IsValid && moved.Generation > before.Generation,
            "A moved/resized invented window advances its geometry generation."));
        targets.ReplaceProcessInstance();
        results.Add(new("Reused process/window identity is rejected", !targets.Inspect(target).IsValid,
            "The pinned target is rejected after its invented process instance changes."));
        return results;
    }

    private sealed class MemorySink : IPlaybackSessionSink
    {
        public ConcurrentQueue<(InputEventType Type, bool Cleanup)> Events { get; } = new();
        public ValueTask EmitSessionInputAsync(InputEvent input, bool cleanup, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Events.Enqueue((input.Type, cleanup)); return ValueTask.CompletedTask; }
    }

    private sealed class InventedTargets : TargetWindowServiceBase
    {
        private TargetWindowInfo _target = new(10, -101, 100, "invented", "target", new(100, 200, 800, 600), 0);
        private readonly TargetWindowInfo _other = new(20, -102, 200, "invented", "other", new(900, 200, 800, 600), 0);
        private nint _foreground = 10;
        public InventedTargets() => Observe();
        public override bool SupportsWindowTargeting => true;
        protected override IReadOnlyList<TargetWindowInfo> ReadWindows() => [_target, _other];
        protected override TargetWindowInfo? ReadWindow(nint handle) => handle == 10 ? _target : handle == 20 ? _other : null;
        protected override nint ReadForeground() => _foreground;
        protected override Rectangle ReadScreenBounds() => new(0, 0, 1920, 1080);
        protected override bool ActivateWindow(nint handle) => throw new NotSupportedException("This probe cannot activate native windows.");
        public void Focus(nint handle) { _foreground = handle; Observe(); }
        public void MoveTarget() { _target = _target with { ClientBounds = new(300, 100, 900, 700) }; Observe(); }
        public void ReplaceProcessInstance() { _target = _target with { ProcessInstance = 999 }; Observe(); }
        public override void Dispose() => Disposed = true;
    }
}
