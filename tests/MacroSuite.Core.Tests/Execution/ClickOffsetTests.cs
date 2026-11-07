using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using Xunit;

namespace MacroSuite.Core.Tests.Execution;

public sealed class ClickOffsetTests
{
    [Theory]
    [InlineData(297, 185, 0, 127, 297, 312)]
    [InlineData(127, 280, 48, -204, 175, 76)]
    [InlineData(-500, -300, -21, 12, -521, -288)]
    public async Task DetectedImageClick_AddsOffsetsToMatchBeforeEmittingInput(
        int matchX, int matchY, int offsetX, int offsetY, int expectedX, int expectedY)
    {
        using var capture = new UnusedCapture();
        using var control = new MacroController();
        using var player = new TrackingPlayer();
        var context = new MacroContext(capture, control) { InputPlayer = player };
        context.Variables.Set(ImageCondition.LastMatchXVariable, matchX);
        context.Variables.Set(ImageCondition.LastMatchYVariable, matchY);
        await new ClickStep(new Point(999, 888), delayAfterMs: 0)
        {
            XVariable = "$" + ImageCondition.LastMatchXVariable,
            YVariable = ImageCondition.LastMatchYVariable,
            OffsetX = offsetX, OffsetY = offsetY
        }.ExecuteAsync(context);

        player.Events.Select(e => e.Type).Should().Equal(
            InputEventType.MouseMove, InputEventType.MouseDown, InputEventType.MouseUp);
        player.Events.Should().OnlyContain(e => e.X == expectedX && e.Y == expectedY);
    }

    [Fact]
    public async Task CustomCoordinates_AddOffsetsBeforeClientScalingAndConversion()
    {
        using var capture = new UnusedCapture();
        using var control = new MacroController();
        using var player = new TrackingPlayer();
        var windows = new TrackingWindows();
        var context = new MacroContext(capture, control) { InputPlayer = player, WindowCoordinates = windows };
        context.Variables.Set("TargetX", 10);
        context.Variables.Set("TargetY", 20);

        await new ClickStep(Point.Empty, delayAfterMs: 0)
        {
            XVariable = "TargetX", YVariable = "$TargetY", OffsetX = 5, OffsetY = -4,
            IsClientRelative = true, RecordedClientWidth = 100, RecordedClientHeight = 100
        }.ExecuteAsync(context);

        windows.LastClientPoint.Should().Be(new Point(30, 32));
        player.Events.Should().OnlyContain(e => e.X == 130 && e.Y == -168);
    }

    [Theory]
    [InlineData(int.MaxValue, 1, true)]
    [InlineData(int.MinValue, -1, true)]
    [InlineData(int.MaxValue, 1, false)]
    [InlineData(int.MinValue, -1, false)]
    public async Task OffsetOverflow_FailsBeforeCoordinateConversionOrInput(int value, int offset, bool xAxis)
    {
        using var capture = new UnusedCapture();
        using var control = new MacroController();
        using var player = new TrackingPlayer();
        var windows = new TrackingWindows();
        var context = new MacroContext(capture, control) { InputPlayer = player, WindowCoordinates = windows };
        context.Variables.Set("X", xAxis ? value : 10);
        context.Variables.Set("Y", xAxis ? 20 : value);
        var click = new ClickStep(Point.Empty, delayAfterMs: 0)
        {
            XVariable = "X", YVariable = "Y", IsClientRelative = true,
            OffsetX = xAxis ? offset : 0, OffsetY = xAxis ? 0 : offset
        };

        await FluentActions.Awaiting(() => click.ExecuteAsync(context)).Should().ThrowAsync<OverflowException>();

        windows.LastClientPoint.Should().BeNull();
        player.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingMatch_DoesNotFallBackToStoredPositionPlusOffset()
    {
        using var capture = new UnusedCapture();
        using var control = new MacroController();
        using var player = new TrackingPlayer();
        var context = new MacroContext(capture, control) { InputPlayer = player };
        var click = new ClickStep(new Point(999, 888), delayAfterMs: 0)
        { XVariable = ImageCondition.LastMatchXVariable, YVariable = ImageCondition.LastMatchYVariable, OffsetY = 127 };

        await FluentActions.Awaiting(() => click.ExecuteAsync(context)).Should().ThrowAsync<InvalidOperationException>();
        player.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task LegacySavedClickWithoutOffsets_StillClicksItsStoredPosition()
    {
        const string json = """
            {"schemaVersion":2,"steps":[{"$type":"click","position":{"x":12,"y":34},"delayAfterMs":0}]}
            """;
        var click = (ClickStep)new MacroJsonSerializer().Deserialize(json).Steps[0];
        click.OffsetX.Should().Be(0);
        click.OffsetY.Should().Be(0);
        using var capture = new UnusedCapture();
        using var control = new MacroController();
        using var player = new TrackingPlayer();
        await click.ExecuteAsync(new MacroContext(capture, control) { InputPlayer = player });
        player.Events.Should().HaveCount(3).And.OnlyContain(e => e.X == 12 && e.Y == 34);
    }

    [Fact]
    public void SaveCloneAndEdit_PreserveOffsetTargetAndClientMetadata()
    {
        var source = new ClickStep(new Point(2, 3), MouseButton.Right, 2, 7)
        {
            XVariable = "CustomX", YVariable = "$CustomY", OffsetX = 48, OffsetY = -204,
            IsClientRelative = true, TargetWindowName = "Test window",
            RecordedClientWidth = 700, RecordedClientHeight = 500,
            RelativeTimestamp = TimeSpan.FromSeconds(4), Stroke = "Press"
        };
        var serializer = new MacroJsonSerializer();
        var loaded = (ClickStep)serializer.Deserialize(serializer.Serialize(new MacroModel { Steps = [source] })).Steps[0];
        var clone = (ClickStep)MacroActionFactory.Clone(source);
        var converted = (ClickStep)MacroActionFactory.Convert(source, EditableActionKind.MouseClick);
        var edited = (ClickStep)MacroActionFactory.UpdateClick(source, 2, 3, MouseButton.Right, 2);

        foreach (ClickStep item in new[] { loaded, clone, converted, edited })
        {
            item.Should().BeEquivalentTo(source, o => o.Excluding(s => s.StepId));
        }
        clone.StepId.Should().NotBe(source.StepId);
        edited.StepId.Should().Be(source.StepId);
        converted.StepId.Should().Be(source.StepId);
        var changed = (ClickStep)MacroActionFactory.UpdateClick(source, 2, 3, MouseButton.Right, 2,
            offsetX: 0, offsetY: 127, isClientRelative: false);
        changed.OffsetX.Should().Be(0);
        changed.OffsetY.Should().Be(127);
        changed.IsClientRelative.Should().BeFalse();
    }

    private sealed class TrackingPlayer : IInputPlayer
    {
        public List<InputEvent> Events { get; } = [];
        public bool IsPlaying => false;
        public Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Events.AddRange(events); return Task.CompletedTask; }
        public void ReleaseAllHeldKeys() { }
        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false) { }
        public void SendRelativeMove(int dx, int dy) { }
        public void Dispose() { }
    }

    private sealed class TrackingWindows : IWindowCoordinateService
    {
        public Point? LastClientPoint { get; private set; }
        public IntPtr GetWindowAt(int screenX, int screenY) => new(1);
        public IntPtr GetForegroundWindow() => new(1);
        public (int clientX, int clientY) ScreenToClient(IntPtr hWnd, int screenX, int screenY) => (screenX - 100, screenY + 200);
        public (int screenX, int screenY) ClientToScreen(IntPtr hWnd, int clientX, int clientY)
        { LastClientPoint = new Point(clientX, clientY); return (clientX + 100, clientY - 200); }
        public (int width, int height) GetClientSize(IntPtr hWnd) => (200, 200);
        public bool IsWindowValid(IntPtr hWnd) => hWnd != IntPtr.Zero;
        public IntPtr FindWindowByTitle(string? titleOrProcessName) => new(1);
    }

    private sealed class UnusedCapture : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;
        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
            => throw new InvalidOperationException("Clicking must not capture another frame.");
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("Clicking must not capture another frame.");
        public void Dispose() { }
    }
}
