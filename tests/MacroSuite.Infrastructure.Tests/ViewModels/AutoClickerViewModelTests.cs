using FluentAssertions;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Coordinates;
using System.Drawing;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class AutoClickerViewModelTests
{
    private sealed class TrackingInputPlayer : IInputPlayer
    {
        public bool IsPlaying => false;
        public List<(MouseButton button, int x, int y, bool doubleClick)> Clicks { get; } = new();
        public TaskCompletionSource FirstClick { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim? HoldClick { get; set; }
        public bool ThrowOnRelease { get; set; }
        public int ReleaseCount { get; private set; }
        public bool IsDisposed { get; private set; }

        public Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default) =>
            Task.CompletedTask;

        public void ReleaseAllHeldKeys()
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(TrackingInputPlayer));
            ReleaseCount++;
            if (ThrowOnRelease) throw new InvalidOperationException("Input release unavailable");
        }

        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false)
        {
            Clicks.Add((button, x, y, doubleClick));
            FirstClick.TrySetResult();
            HoldClick?.Wait(TimeSpan.FromSeconds(2));
        }

        public void SendRelativeMove(int dx, int dy) { }

        public void Dispose() => IsDisposed = true;
    }

    [Fact]
    public void InitialState_HasSensibleDefaults()
    {
        var inputPlayer = new TrackingInputPlayer();
        using var vm = new AutoClickerViewModel(inputPlayer);

        vm.ClicksPerSecond.Should().Be(10.0);
        vm.StartHotkey.Should().Be("F1");
        vm.IsRunning.Should().BeFalse();
        vm.UseCurrentPosition.Should().BeTrue();
        vm.RepeatUntilStopped.Should().BeTrue();
    }

    [Fact]
    public void ShortcutCapture_RequestsSharedEditorWithoutChangingExistingBind()
    {
        var inputPlayer = new TrackingInputPlayer();
        using var vm = new AutoClickerViewModel(inputPlayer);
        bool fired = false;
        vm.HotkeyCaptureRequested += () => fired = true;

        vm.BeginHotkeyCaptureCommand.Execute(null);

        vm.StartHotkey.Should().Be("F1");
        fired.Should().BeTrue();
    }

    [Fact]
    public void AssignedCombo_UpdatesSummaryAndCaptureDisplay()
    {
        using var vm = new AutoClickerViewModel(new TrackingInputPlayer());
        vm.IsCapturingHotkey = true;
        vm.StartHotkeyDisplay.Should().Be("Press shortcut…");
        vm.StartHotkey = "Ctrl+Shift+L";
        vm.IsCapturingHotkey = false;
        vm.StartHotkeyDisplay.Should().Be("Ctrl+Shift+L");
        vm.RunSummary.Should().Contain("Ctrl+Shift+L to toggle");
    }

    [Fact]
    public void SettingCps_ClampsToAllowedRange()
    {
        var inputPlayer = new TrackingInputPlayer();
        using var vm = new AutoClickerViewModel(inputPlayer);

        vm.ClicksPerSecond = -5;
        vm.ClicksPerSecond.Should().Be(0.1);

        vm.ClicksPerSecond = 1000;
        vm.ClicksPerSecond.Should().Be(500.0);
    }

    [Fact]
    public async Task ToggleRunCommand_TogglesRunningStateAndSendsClicks()
    {
        var inputPlayer = new TrackingInputPlayer();
        using var vm = new AutoClickerViewModel(inputPlayer);
        vm.ClicksPerSecond = 20.0;

        vm.ToggleRunCommand.Execute(null);
        vm.IsRunning.Should().BeTrue();

        // Allow clicker thread to run briefly
        await Task.Delay(100);

        vm.ToggleRunCommand.Execute(null);
        await vm.RunCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        vm.IsRunning.Should().BeFalse();

        // Verify that SendClick was called through the single input pipeline
        inputPlayer.Clicks.Should().NotBeEmpty();
    }

    [Fact]
    public void RepeatChoices_AreMutuallyExclusive_AndKeepCount()
    {
        using var vm = new AutoClickerViewModel(new TrackingInputPlayer());
        vm.RepeatCount = 7;
        vm.RepeatOnce = true;
        vm.RepeatOnce.Should().BeTrue();
        vm.RepeatNTimes.Should().BeFalse();
        vm.RepeatUntilStopped.Should().BeFalse();
        vm.RepeatNTimes = true;
        vm.RepeatOnce.Should().BeFalse();
        vm.RepeatNTimes.Should().BeTrue();
        vm.RunSummary.Should().Contain("7 times");
        vm.RepeatUntilStopped = true;
        vm.RepeatOnce.Should().BeFalse();
        vm.RepeatNTimes.Should().BeFalse();
        vm.RepeatCount.Should().Be(7);
    }

    [Fact]
    public async Task DoubleClickRateAndRepeat_CountPairs_AndOnceSendsOnePair()
    {
        var player = new TrackingInputPlayer();
        using var vm = new AutoClickerViewModel(player);
        vm.SelectedClickType = "Double";
        vm.ClicksPerSecond = 10;
        vm.RepeatCount = 9;
        vm.RepeatOnce = true;
        vm.IntervalPreviewText.Should().Be("10 double-clicks/s = 100 ms between double-click pairs.");
        vm.ToggleRun();
        await vm.RunCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        player.Clicks.Should().ContainSingle().Which.doubleClick.Should().BeTrue();
        vm.CompletedCountText.Should().Be("1 double-click pairs completed");
    }

    [Fact]
    public async Task FiniteRepeat_SendsConfiguredNumberOfPairs()
    {
        var player = new TrackingInputPlayer();
        using var vm = new AutoClickerViewModel(player);
        vm.SelectedClickType = "Double";
        vm.ClicksPerSecond = 500;
        vm.RepeatCount = 3;
        vm.RepeatNTimes = true;
        vm.ToggleRun();
        await vm.RunCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        player.Clicks.Should().HaveCount(3).And.OnlyContain(click => click.doubleClick);
    }

    [Fact]
    public async Task MacroActivity_BlocksStartingAndPicking_WithoutInput()
    {
        var player = new TrackingInputPlayer();
        var picker = new StubLocationPicker { Next = new ScreenPoint(90, 100) };
        using var vm = new AutoClickerViewModel(player, picker) { UseFixedPosition = true, IsMacroActive = true };
        vm.ToggleRunCommand.CanExecute(null).Should().BeFalse();
        vm.CanEditConfiguration.Should().BeFalse();
        vm.ToggleRun();
        await vm.PickFixedLocationAsync();
        player.Clicks.Should().BeEmpty();
        vm.FixedX.Should().Be(0);
        vm.IsMacroActive = false;
        vm.ToggleRunCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Stop_InterruptsLongInterval_AndLocksRestartUntilCleanup()
    {
        using var holdClick = new ManualResetEventSlim();
        var player = new TrackingInputPlayer { HoldClick = holdClick };
        using var vm = new AutoClickerViewModel(player) { ClicksPerSecond = 0.1 };
        vm.ToggleRun();
        await player.FirstClick.Task.WaitAsync(TimeSpan.FromSeconds(2));
        vm.CanEditConfiguration.Should().BeFalse();
        vm.StopClicker();
        vm.IsRunning.Should().BeTrue();
        vm.ToggleRunCommand.CanExecute(null).Should().BeFalse();
        vm.ToggleRun();
        holdClick.Set();
        await vm.RunCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        vm.IsRunning.Should().BeFalse();
        vm.IsStopping.Should().BeFalse();
        player.Clicks.Should().ContainSingle();
    }

    [Fact]
    public async Task Shutdown_WaitsForInFlightClick_AndPreventsNewWorkBeforeBackendDisposal()
    {
        using var holdClick = new ManualResetEventSlim();
        var player = new TrackingInputPlayer { HoldClick = holdClick };
        using var vm = new AutoClickerViewModel(player, new StubLocationPicker())
        {
            ClicksPerSecond = 0.1,
            UseFixedPosition = true,
            IsCapturingHotkey = true
        };
        vm.RegisterTestClick();
        vm.ToggleRun();
        await player.FirstClick.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var shutdown = vm.ShutdownAsync();
        try
        {
            shutdown.IsCompleted.Should().BeFalse("the in-flight input operation has not returned");
            vm.ShutdownAsync().Should().BeSameAs(shutdown);
            vm.IsRunning.Should().BeTrue();
            vm.IsStopping.Should().BeTrue();
            vm.CanEditConfiguration.Should().BeFalse();
            vm.IsCapturingHotkey.Should().BeFalse();
            vm.ToggleRunCommand.CanExecute(null).Should().BeFalse();
            vm.PickLocationCommand.CanExecute(null).Should().BeFalse();
            vm.BeginHotkeyCaptureCommand.CanExecute(null).Should().BeFalse();
            vm.StartTestCommand.CanExecute(null).Should().BeFalse();
            vm.RegisterTestClickCommand.CanExecute(null).Should().BeFalse();

            vm.ToggleRun();
            vm.RegisterTestClick();
            await vm.PickFixedLocationAsync();
            vm.TotalTestClicks.Should().Be(1);
        }
        finally
        {
            holdClick.Set();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        }

        vm.RunCompletion.IsCompleted.Should().BeTrue();
        vm.IsRunning.Should().BeFalse();
        vm.IsStopping.Should().BeFalse();
        vm.IsTestRunning.Should().BeFalse();
        player.Clicks.Should().ContainSingle();
        player.IsDisposed.Should().BeFalse("the application owns the shared input backend");

        player.Dispose();
        int releases = player.ReleaseCount;
        vm.StopClicker();
        vm.Dispose();
        await vm.ShutdownAsync();
        player.ReleaseCount.Should().Be(releases);
        vm.RunLog.Should().BeEmpty("completed shutdown must not touch an already-disposed backend");
    }

    [Fact]
    public async Task Shutdown_CancelsAndDrainsPickerAndTester_WithoutApplyingLateCoordinates()
    {
        var picker = new BlockingLocationPicker();
        using var vm = new AutoClickerViewModel(new TrackingInputPlayer(), picker)
        {
            UseFixedPosition = true,
            FixedX = 30,
            FixedY = 40
        };
        vm.RegisterTestClick();
        var pick = vm.PickFixedLocationAsync();
        vm.IsPickingLocation.Should().BeTrue();

        var shutdown = vm.ShutdownAsync();
        try
        {
            await picker.CancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            picker.CancelRequested.Should().BeTrue();
            shutdown.IsCompleted.Should().BeFalse("the picker overlay has not completed its cleanup");
        }
        finally
        {
            picker.Completion.TrySetResult(new ScreenPoint(100, 200));
            await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        }

        pick.IsCompleted.Should().BeTrue();
        vm.IsPickingLocation.Should().BeFalse();
        vm.IsTestRunning.Should().BeFalse();
        vm.FixedX.Should().Be(30);
        vm.FixedY.Should().Be(40);
    }

    [Fact]
    public async Task Shutdown_InputReleaseFailure_StillFinishesRunCleanupAndReportsError()
    {
        var player = new TrackingInputPlayer { ThrowOnRelease = true };
        using var vm = new AutoClickerViewModel(player) { RepeatOnce = true };
        vm.ToggleRun();
        await vm.RunCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        await vm.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));

        vm.IsRunning.Should().BeFalse();
        vm.IsStopping.Should().BeFalse();
        player.Clicks.Should().ContainSingle();
        vm.RunLog.Should().Contain(message => message.Contains("Releasing Auto Clicker input failed"));
        vm.InputOperations.Snapshot.IsQuarantined.Should().BeTrue();
        vm.InputOperations.TryAcquire(HostInputOperationKind.Playback, out _, out _).Should().BeFalse();
        int attempts = player.ReleaseCount;
        player.ThrowOnRelease = false;
        (await vm.RecoverInputOwnershipAsync()).Should().BeTrue();
        player.ReleaseCount.Should().Be(attempts + 1);
        vm.InputOperations.Snapshot.IsBusy.Should().BeFalse();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteRates_DoNotEnterTheTimingLoop(double invalid)
    {
        using var vm = new AutoClickerViewModel(new TrackingInputPlayer());
        vm.ClicksPerSecond = invalid;
        vm.RandomOffsetCps = invalid;
        vm.ClicksPerSecond.Should().Be(10);
        vm.RandomOffsetCps.Should().Be(2);
    }

    [Fact]
    public void CpsInputText_UpdatesClicksPerSecond_AndSynchronizes()
    {
        var inputPlayer = new TrackingInputPlayer();
        using var vm = new AutoClickerViewModel(inputPlayer);

        vm.ClicksPerSecond = 10.0;
        vm.CpsInputText.Should().Be("10");

        // Typing valid integer updates ClicksPerSecond
        vm.CpsInputText = "50";
        vm.ClicksPerSecond.Should().Be(50.0);

        // Typing decimal updates ClicksPerSecond
        vm.CpsInputText = "25.5";
        vm.ClicksPerSecond.Should().Be(25.5);

        // Setting ClicksPerSecond updates CpsInputText
        vm.ClicksPerSecond = 100.0;
        vm.CpsInputText.Should().Be("100");

        // Setting fractional ClicksPerSecond formats appropriately
        vm.ClicksPerSecond = 0.5;
        vm.CpsInputText.Should().Be("0.5");

        // Invalid input does not crash and leaves ClicksPerSecond unchanged
        vm.CpsInputText = "invalid";
        vm.ClicksPerSecond.Should().Be(0.5);
    }

    private sealed class StubLocationPicker : ILocationPicker
    {
        public ScreenPoint? Next { get; set; }
        public bool IsArmed => false;
        public Task<ScreenPoint?> ArmAndWaitAsync(CancellationToken ct = default) => Task.FromResult(Next);
        public void Cancel() { }
        public void Dispose() { }
    }

    private sealed class BlockingLocationPicker : ILocationPicker
    {
        public TaskCompletionSource CancelEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ScreenPoint?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsArmed => !Completion.Task.IsCompleted;
        public bool CancelRequested { get; private set; }
        public Task<ScreenPoint?> ArmAndWaitAsync(CancellationToken ct = default) => Completion.Task;
        public void Cancel() { CancelRequested = true; CancelEntered.TrySetResult(); }
        public void Dispose() { }
    }

    private sealed class StubDesktop : IVirtualDesktopGeometry
    {
        public Rectangle VirtualDesktopBounds => new(-1920, 0, 3840, 1080);
        public IReadOnlyList<MonitorDescriptor> Monitors =>
        [
            new(new Rectangle(-1920, 0, 1920, 1080), Rectangle.Empty, 1, 1, false),
            new(new Rectangle(0, 0, 1920, 1080), Rectangle.Empty, 1, 1, true)
        ];
    }

    [Fact]
    public void FixedPosition_IdentifiesContainingMonitorAndOutOfBounds()
    {
        using var vm = new AutoClickerViewModel(new TrackingInputPlayer()) { DisplayGeometryProvider = () => new StubDesktop() };
        vm.FixedPositionMonitorText.Should().StartWith("Primary display");
        vm.FixedX = -100;
        vm.FixedPositionMonitorText.Should().Contain("Display at X -1920, Y 0");
        vm.FixedY = 2000;
        vm.FixedPositionMonitorText.Should().Be("This position is outside the connected displays.");
    }

    [Fact]
    public async Task PickFixedLocation_UsesPickerCoordinates_NotTheButton()
    {
        var picker = new StubLocationPicker { Next = new ScreenPoint(2162, 880) };
        using var vm = new AutoClickerViewModel(new TrackingInputPlayer(), picker);
        vm.UseFixedPosition = true;
        vm.FixedX = 1;
        vm.FixedY = 2;

        await vm.PickFixedLocationAsync();

        vm.FixedX.Should().Be(2162);
        vm.FixedY.Should().Be(880);
        vm.IsPickingLocation.Should().BeFalse();
    }

    [Fact]
    public async Task PickFixedLocation_Cancel_LeavesPreviousCoordinates()
    {
        var picker = new StubLocationPicker { Next = null };
        using var vm = new AutoClickerViewModel(new TrackingInputPlayer(), picker);
        vm.UseFixedPosition = true;
        vm.FixedX = 100;
        vm.FixedY = 200;

        await vm.PickFixedLocationAsync();

        vm.FixedX.Should().Be(100);
        vm.FixedY.Should().Be(200);
        vm.IsPickingLocation.Should().BeFalse();
    }
}
