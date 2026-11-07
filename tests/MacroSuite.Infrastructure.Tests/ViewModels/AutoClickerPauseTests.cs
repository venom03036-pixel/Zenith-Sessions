using FluentAssertions;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class AutoClickerViewModelTests
{
    [Fact]
    public async Task ReturnToEditor_PausesClickerWithoutEndingRun_AndResumeDoesNotBurst()
    {
        var player = new TrackingInputPlayer();
        using var vm = new MacroSuite.App.ViewModels.AutoClickerViewModel(player) { ClicksPerSecond = 20 };
        vm.ToggleRun();
        await player.FirstClick.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await vm.PauseForEditorFocusAsync();
        vm.IsPaused.Should().BeTrue(); vm.IsRunning.Should().BeTrue();
        int pausedCount = player.Clicks.Count;
        await Task.Delay(130);
        player.Clicks.Should().HaveCount(pausedCount);
        await vm.TogglePauseAsync();
        for (int i = 0; i < 40 && player.Clicks.Count == pausedCount; i++) await Task.Delay(5);
        vm.StopClicker();
        await vm.RunCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        player.Clicks.Count.Should().BeInRange(pausedCount + 1, pausedCount + 2);
        vm.IsPaused.Should().BeFalse(); vm.IsRunning.Should().BeFalse();
        await vm.ShutdownAsync();
    }

    [Fact]
    public async Task EmergencySignal_WakesPausedOwner_WithoutRequiringUiStopFirst()
    {
        var player = new TrackingInputPlayer();
        using var vm = new MacroSuite.App.ViewModels.AutoClickerViewModel(player) { ClicksPerSecond = 0.1 };
        vm.ToggleRun(); await player.FirstClick.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await vm.PauseForEditorFocusAsync(); int pausedCount = player.Clicks.Count;
        await Task.Run(vm.SignalEmergencyStop);
        await vm.RunCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        player.Clicks.Should().HaveCount(pausedCount); vm.IsRunning.Should().BeFalse();
        await vm.ShutdownAsync();
    }
}
