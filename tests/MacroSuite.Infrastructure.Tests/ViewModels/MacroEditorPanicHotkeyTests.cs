using FluentAssertions;
using MacroSuite.App.ViewModels;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    [Theory]
    [InlineData("Shift+9")]
    [InlineData("Ctrl+Shift+L")]
    [InlineData("G")]
    [InlineData("ESC")]
    public void PanicCapture_AcceptsSingleKeysAndCombinations(string hotkey)
    {
        using var vm = CreateViewModel();
        string? notified = null;
        vm.PanicHotkeyChanged += value => notified = value;

        vm.BeginQuickBindCapture("panic");
        vm.QuickBindCaptureTarget.Should().Be("panic");
        vm.AppKeybinds.Single(row => row.Id == "panic").IsCapturing.Should().BeTrue();
        vm.CompleteQuickBindCapture(hotkey).Should().BeTrue();

        vm.PanicHotkey.Should().Be(hotkey);
        notified.Should().Be(hotkey);
        vm.AppKeybinds.Single(row => row.Id == "panic").HotkeyDisplay.Should().Be(hotkey);
        vm.QuickBindCaptureTarget.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("NoSuchKey")]
    public void PanicBind_CannotBeClearedOrMadeInvalid(string invalid)
    {
        using var vm = CreateViewModel();
        vm.PanicHotkey = "Shift+9";
        vm.PanicHotkey = invalid;

        vm.PanicHotkey.Should().Be("Shift+9");
        vm.TryAssignBind("panic", invalid).Should().BeFalse();
        vm.PanicHotkey.Should().Be("Shift+9");
    }

    [Fact]
    public void PanicBind_ReservesCurrentKey_AndReleasesPreviousKey()
    {
        using var vm = CreateViewModel();
        vm.TryAssignBind("panic", "Shift+9").Should().BeTrue();
        vm.ConfirmHotkeyConflictPrompter = _ => HotkeyConflictChoice.UnbindPrevious;

        vm.TryAssignBind("record", "Shift+9").Should().BeFalse();
        vm.PanicHotkey.Should().Be("Shift+9");
        vm.TryAssignBind("record", "F12").Should().BeTrue();
        vm.TryAssignBind("stop", "ESC").Should().BeTrue();
        vm.StopHotkey.Should().Be("ESC");
    }

    [Fact]
    public void PanicBind_ConflictCancelKeepsPanic_AndConfirmedChangeNeverClearsIt()
    {
        using var vm = CreateViewModel();
        vm.ConfirmHotkeyConflictPrompter = _ => HotkeyConflictChoice.Cancel;
        vm.TryAssignBind("panic", vm.RecordHotkey).Should().BeFalse();
        vm.PanicHotkey.Should().Be("F12");

        var observed = new List<string>();
        vm.PanicHotkeyChanged += observed.Add;
        vm.ConfirmHotkeyConflictPrompter = _ => HotkeyConflictChoice.UnbindPrevious;
        vm.TryAssignBind("panic", vm.RecordHotkey).Should().BeTrue();

        vm.RecordHotkey.Should().BeEmpty();
        vm.PanicHotkey.Should().Be("F6");
        observed.Should().Equal("F6");
    }
}
