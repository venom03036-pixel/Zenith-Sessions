using FluentAssertions;
using MacroSuite.App.ViewModels;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    [Theory]
    [InlineData("Shift+9")]
    [InlineData("Ctrl+Shift+L")]
    [InlineData("F11")]
    public void MiniClickThrough_CapturesAndPublishesCustomBinding(string hotkey)
    {
        using var vm = CreateViewModel();
        (string Target, string Hotkey)? changed = null;
        vm.QuickBindChanged += (target, value) => changed = (target, value);
        vm.BeginQuickBindCapture("mini-click-through");
        vm.AppKeybinds.Single(row => row.Id == "mini-click-through").IsCapturing.Should().BeTrue();
        vm.CompleteQuickBindCapture(hotkey).Should().BeTrue();
        vm.MiniToolbarClickThroughHotkey.Should().Be(hotkey);
        changed.Should().Be(("mini-click-through", hotkey));
        vm.AppKeybinds.Single(row => row.Id == "mini-click-through").HotkeyDisplay.Should().Be(hotkey);
    }

    [Fact]
    public void MiniClickThrough_ProtectedBindingCannotBeSilentlyStolen()
    {
        using var vm = CreateViewModel();
        vm.ConfirmHotkeyConflictPrompter = _ => HotkeyConflictChoice.UnbindPrevious;
        vm.TryAssignBind("record", vm.MiniToolbarClickThroughHotkey).Should().BeFalse();
        vm.RecordHotkey.Should().Be("F6");
        vm.MiniToolbarClickThroughHotkey.Should().Be("Ctrl+Alt+F10");
    }

    [Theory]
    [InlineData("record")]
    [InlineData("stop")]
    [InlineData("play")]
    [InlineData("pause")]
    [InlineData("panic")]
    public void MiniClickThrough_CannotUnbindAnExistingTransport(string target)
    {
        using var vm = CreateViewModel();
        string old = vm.AppKeybinds.Single(row => row.Id == target).Hotkey;
        vm.ConfirmHotkeyConflictPrompter = _ => HotkeyConflictChoice.UnbindPrevious;
        vm.TryAssignBind("mini-click-through", old).Should().BeFalse();
        vm.AppKeybinds.Single(row => row.Id == target).Hotkey.Should().Be(old);
        vm.MiniToolbarClickThroughHotkey.Should().Be("Ctrl+Alt+F10");
    }

    [Fact]
    public void MiniClickThrough_RebindingFreesOldKey_AndEscapeCancelsCapture()
    {
        using var vm = CreateViewModel();
        vm.TryAssignBind("mini-click-through", "Ctrl+Shift+L").Should().BeTrue();
        vm.TryAssignBind("record", "Ctrl+Alt+F10").Should().BeTrue();
        vm.BeginQuickBindCapture("mini-click-through");
        vm.CompleteQuickBindCapture("ESC").Should().BeTrue();
        vm.MiniToolbarClickThroughHotkey.Should().Be("Ctrl+Shift+L");
        vm.QuickBindCaptureTarget.Should().BeEmpty();
    }
}
