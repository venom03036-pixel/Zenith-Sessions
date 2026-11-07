using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Rules;
using Xunit;

namespace MacroSuite.Core.Tests.Input;

public sealed class PlaybackSessionTests
{
    private sealed class NoCapture : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
    private sealed class Sink : IPlaybackSessionSink
    {
        public readonly ConcurrentQueue<(InputEvent Event, bool Cleanup)> Events = new();
        public ValueTask EmitSessionInputAsync(InputEvent input, bool cleanup, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Events.Enqueue((input, cleanup)); return ValueTask.CompletedTask; }
    }
    private sealed class Targets : TargetWindowServiceBase
    {
        public List<TargetWindowInfo> Windows = [Window(10, 1, "one"), Window(20, 2, "two")];
        public nint Foreground = 10;
        public override bool SupportsWindowTargeting => true;
        public Targets() => Observe();
        public static TargetWindowInfo Window(nint handle, int process, string title)
            => new(handle, process, process * 100L, "application", title, new(100, 200, 800, 600), 0);
        protected override IReadOnlyList<TargetWindowInfo> ReadWindows() => Windows;
        protected override TargetWindowInfo? ReadWindow(nint handle) => Windows.FirstOrDefault(w => w.Handle == handle);
        protected override nint ReadForeground() => Foreground;
        protected override Rectangle ReadScreenBounds() => new(-1920, 0, 3840, 1080);
        protected override bool ActivateWindow(nint handle) { Foreground = handle; Observe(); return true; }
        public void Focus(nint handle) { Foreground = handle; Observe(); }
        public void Refresh() => Observe();
        public override void Dispose() { Disposed = true; }
    }
    private static InputEvent Key(bool down, int key = 65) => new(down ? InputEventType.KeyDown : InputEventType.KeyUp, Stopwatch.GetTimestamp(), 0, 0, key);
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    [Fact]
    public async Task PauseReleasesPhysicalHold_ResumeRestoresItOnce_ThenUpCompletes()
    {
        using var control = new MacroController();
        await using var session = new PlaybackSessionContext(control);
        var sink = new Sink(); session.BindSink(sink);
        await session.EmitAsync(Key(true));
        await control.PauseAsync();
        await Until(() => sink.Events.Count == 2);
        sink.Events.Last().Should().Match<(InputEvent Event, bool Cleanup)>(e => e.Event.Type == InputEventType.KeyUp && e.Cleanup);
        await control.ResumeAsync();
        await Until(() => sink.Events.Count == 3);
        await session.EmitAsync(Key(false));
        sink.Events.Select(x => x.Event.Type).Should().Equal(InputEventType.KeyDown, InputEventType.KeyUp, InputEventType.KeyDown, InputEventType.KeyUp);
    }

    [Fact]
    public async Task TargetLossPauses_RefocusDoesNotAutomaticallyResume()
    {
        using var targets = new Targets(); using var control = new MacroController();
        var pinned = targets.Resolve(new() { Mode = MacroTargetMode.SpecificWindow, ApplicationId = "application", WindowTitle = "one" });
        await using var session = new PlaybackSessionContext(control, targets, pinned);
        var sink = new Sink(); session.BindSink(sink); await session.EmitAsync(Key(true));
        targets.Focus(20); await Until(() => control.IsPaused && sink.Events.Count == 2);
        targets.Focus(10); await Task.Delay(30);
        control.IsPaused.Should().BeTrue(); sink.Events.Count.Should().Be(2);
        await control.ResumeAsync(); await Until(() => sink.Events.Count == 3);
    }

    [Fact]
    public async Task StopWhilePausedDoesNotReplayOrRestoreHeldInputs()
    {
        using var control = new MacroController(); await using var session = new PlaybackSessionContext(control);
        var sink = new Sink(); session.BindSink(sink); await session.EmitAsync(Key(true));
        await control.PauseAsync(); await Until(() => sink.Events.Count == 2);
        var blocked = session.EmitAsync(Key(false)).AsTask();
        await control.StopAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        sink.Events.Count.Should().Be(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrPanicCannotWakePausedInputBeforeLinkedCancellation(bool panic)
    {
        using var targets = new Targets();
        using var control = new MacroController();
        var pinned = targets.Resolve(new() { Mode = MacroTargetMode.ActiveExternalWindow });
        await using var session = new PlaybackSessionContext(control, targets, pinned);
        var sink = new Sink(); session.BindSink(sink);
        await session.EmitAsync(Key(true));
        targets.Focus(20);
        await session.AwaitReleasedAsync();
        var pending = session.EmitAsync(new(InputEventType.MouseDown, Stopwatch.GetTimestamp(), 0, 0, 0)).AsTask();
        targets.Focus(10); // Refocus alone must not restore a hold or resume the queued click.
        control.IsPaused.Should().BeTrue();
        if (panic) control.Panic(); else await control.StopAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await session.AwaitReleasedAsync();
        sink.Events.Select(e => e.Event.Type).Should().Equal(InputEventType.KeyDown, InputEventType.KeyUp);
        (await session.TryResumeAsync(inputIsNeutral: true)).Should().BeFalse();
    }

    [Fact]
    public async Task RecordedDeadlineDoesNotRunDuringPause()
    {
        using var control = new MacroController(); await using var session = new PlaybackSessionContext(control);
        var sink = new Sink(); long now = Stopwatch.GetTimestamp();
        var work = PlaybackSessionRunner.RunAsync([new(InputEventType.KeyDown, now, 0, 0, 65),
            new(InputEventType.KeyUp, now + Stopwatch.Frequency / 5, 0, 0, 65)], new(), session, sink, default);
        await Until(() => sink.Events.Count == 1); await control.PauseAsync();
        await Until(() => sink.Events.Count == 2); await Task.Delay(230);
        work.IsCompleted.Should().BeFalse();
        await control.ResumeAsync(); await Until(() => sink.Events.Count == 3);
        work.IsCompleted.Should().BeFalse();
        await work.WaitAsync(TimeSpan.FromSeconds(3)); sink.Events.Count.Should().Be(4);
    }

    [Fact]
    public void MultipleApplicationWindowsRequireSelection_AndMissingTargetNeverSubstitutesForeground()
    {
        using var targets = new Targets();
        Action ambiguous = () => targets.Resolve(new() { Mode = MacroTargetMode.Application, ApplicationId = "application" });
        ambiguous.Should().Throw<InvalidOperationException>().WithMessage("*Several windows*");
        var chosen = targets.Resolve(new() { Mode = MacroTargetMode.SpecificWindow, ApplicationId = "application", WindowTitle = "one" });
        targets.Windows.RemoveAt(0); targets.Focus(20);
        targets.Inspect(chosen).IsValid.Should().BeFalse();
        Action absent = () => targets.Resolve(chosen.Rule); absent.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void PinnedIdentityRejectsReusedHandle_AndGeometryChangesAdvanceGeneration()
    {
        using var targets = new Targets();
        var pinned = targets.Resolve(new() { Mode = MacroTargetMode.ActiveExternalWindow });
        long first = targets.Inspect(pinned).Generation;
        targets.Windows[0] = targets.Windows[0] with { ClientBounds = new(-700, 50, 1200, 800) }; targets.Refresh();
        var moved = targets.Inspect(pinned); moved.IsValid.Should().BeTrue(); moved.Generation.Should().BeGreaterThan(first);
        targets.Windows[0] = targets.Windows[0] with { ProcessInstance = 999 }; targets.Refresh();
        targets.Inspect(pinned).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public void LegacyMigrationDoesNotIntroduceTarget_OrRewriteInput(int version)
    {
        string json = $$"""{"schemaVersion":{{version}},"name":"old","settings":{"repeatCount":1},"steps":[]}""";
        var serializer = new MacroJsonSerializer(); var model = serializer.Deserialize(json);
        model.SchemaVersion.Should().Be(3); model.Settings.Target.Should().BeNull();
        json.Should().Contain($"\"schemaVersion\":{version}");
    }

    [Fact]
    public void ExplicitTargetRoundTrips_AndMalformedTargetIsRejected()
    {
        var serializer = new MacroJsonSerializer();
        var model = new MacroModel { Settings = new() { Target = new() { Mode = MacroTargetMode.SpecificWindow, ApplicationId = "application", WindowTitle = "one" } } };
        serializer.Deserialize(serializer.Serialize(model)).Settings.Target.Should().Be(model.Settings.Target);
        model.Settings.Target = new() { Mode = (MacroTargetMode)100 };
        Action invalid = () => serializer.Serialize(model); invalid.Should().Throw<InvalidDataException>();
    }
    [Fact]
    public async Task TargetWindowSearchUsesPinnedBoundsAndScreenModeNeverSubstitutesForeground()
    {
        using var targets = new Targets(); using var control = new MacroController();
        var condition = new ImageCondition("unused") { ScanOptions = new() { SearchScope = ImageSearchScope.TargetWindow } };
        var method = typeof(ImageCondition).GetMethod("GetCaptureRegion", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await using (var strict = new PlaybackSessionContext(control, targets, targets.Resolve(new() { Mode = MacroTargetMode.ActiveExternalWindow })))
        {
            var context = new MacroContext(new NoCapture(), control) { PlaybackSession = strict };
            targets.Windows[1] = targets.Windows[1] with { ClientBounds = new(900, 500, 300, 200) }; targets.Focus(20);
            ((Rectangle?)method.Invoke(condition, [context, false])).Should().Be(targets.Windows[0].ClientBounds);
        }
        using var screenControl = new MacroController();
        await using var screen = new PlaybackSessionContext(screenControl, targets, targets.Resolve(new() { Mode = MacroTargetMode.Screen }));
        var screenContext = new MacroContext(new NoCapture(), screenControl) { PlaybackSession = screen };
        Action invoke = () => method.Invoke(condition, [screenContext, false]);
        invoke.Should().Throw<System.Reflection.TargetInvocationException>().WithInnerException<InvalidOperationException>();
    }
    [Fact]
    public void OlderSchemaReaderRejectsNewTargetDocumentInsteadOfDroppingTarget()
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(new MacroJsonSerializer().Serialize(new MacroModel
            { Settings = new() { Target = new() { Mode = MacroTargetMode.Screen } } }))!;
        Action downgrade = () => new MacroMigrationEngine().Migrate(document, targetVersion: 2);
        downgrade.Should().Throw<InvalidOperationException>().WithMessage("*newer version*");
    }
}
