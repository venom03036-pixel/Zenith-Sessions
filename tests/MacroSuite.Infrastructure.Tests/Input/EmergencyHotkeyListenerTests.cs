using FluentAssertions;
using MacroSuite.Infrastructure.Input;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Input;

public sealed class EmergencyHotkeyListenerTests
{
    private const uint KeyDown = 0x0100;
    private const uint KeyUp = 0x0101;
    private const uint SysKeyDown = 0x0104;
    private const uint SysKeyUp = 0x0105;
    private const uint F12 = 0x7B;
    private const uint Shift = 0x04;
    private const uint Control = 0x02;

    [Theory]
    [InlineData(0, F12, 0, F12, true)]
    [InlineData(0, F12, Shift, F12, false)]
    [InlineData(0, F12, 0, 0x1B, false)]
    [InlineData(Shift, 0x39, Shift, 0x39, true)]
    [InlineData(Shift, 0x39, 0, 0x39, false)]
    [InlineData(Control | Shift, 0x4C, Control | Shift, 0x4C, true)]
    [InlineData(Control | Shift, 0x4C, Shift, 0x4C, false)]
    [InlineData(Control | Shift, 0x4C, Control | Shift | 0x01, 0x4C, false)]
    [InlineData(0x4000, F12, 0, F12, true)]
    public void Shortcut_RequiresTheConfiguredKeyAndExactModifiers(uint modifiers, uint key,
        uint pressedModifiers, uint pressedKey, bool expected)
    {
        EmergencyHotkeyListener.MatchesShortcut(modifiers, key, pressedModifiers, pressedKey)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(0, KeyDown, 0, true)]
    [InlineData(0, SysKeyDown, 0, true)]
    [InlineData(-1, KeyDown, 0, false)]
    [InlineData(0, KeyUp, 0, false)]
    [InlineData(0, SysKeyUp, 0, false)]
    [InlineData(0, KeyDown, 0x10, false)]
    [InlineData(0, KeyDown, 0x02, false)]
    [InlineData(0, KeyDown, 0x12, false)]
    public void Eligibility_AcceptsOnlyPhysicalKeyDown(int hookCode, uint message,
        uint flags, bool expected)
    {
        EmergencyHotkeyListener.IsEligible(hookCode, message, flags).Should().Be(expected);
    }

    [Fact]
    public void HeldShortcut_InvokesOnceAndConsumesRepeatsThroughRelease()
    {
        var state = new EmergencyHotkeyState(0, F12);

        Handle(state, KeyDown).Should().Be((true, true));
        Handle(state, KeyDown).Should().Be((true, false));
        Handle(state, KeyDown).Should().Be((true, false));
        Handle(state, KeyUp).Should().Be((true, false));
        Handle(state, KeyDown).Should().Be((true, true));
    }

    [Fact]
    public void WrongModifiers_DoNotConsumeOrActivateWhenKeyAutoRepeats()
    {
        var state = new EmergencyHotkeyState(Shift, F12);

        Handle(state, KeyDown).Should().Be((false, false));
        Handle(state, KeyDown, modifiers: Shift).Should().Be((false, false));
        Handle(state, KeyUp).Should().Be((false, false));
        Handle(state, KeyDown, modifiers: Shift).Should().Be((true, true));
    }

    [Fact]
    public void EditingShortcut_DoesNotTriggerUntilReleasedAndPressedAgain()
    {
        var state = new EmergencyHotkeyState(0, F12);

        Handle(state, KeyDown, suppressed: true).Should().Be((false, false));
        Handle(state, KeyDown).Should().Be((false, false));
        Handle(state, KeyUp).Should().Be((false, false));
        Handle(state, KeyDown).Should().Be((true, true));
    }

    [Theory]
    [InlineData(0x10u)]
    [InlineData(0x02u)]
    public void InjectedShortcut_CannotActivateOrChangePhysicalRepeatState(uint flags)
    {
        var state = new EmergencyHotkeyState(0, F12);

        Handle(state, KeyDown, flags: flags).Should().Be((false, false));
        Handle(state, KeyDown).Should().Be((true, true));
        Handle(state, KeyUp, flags: flags).Should().Be((false, false));
        Handle(state, KeyDown).Should().Be((true, false));
        Handle(state, KeyUp).Should().Be((true, false));
        Handle(state, KeyDown).Should().Be((true, true));
    }

    [Fact]
    public void AlreadyHeldAtInstallation_WaitsForTheNextPhysicalPress()
    {
        var state = new EmergencyHotkeyState(0, F12, initiallyDown: true);

        Handle(state, KeyDown).Should().Be((false, false));
        Handle(state, KeyUp).Should().Be((false, false));
        Handle(state, KeyDown).Should().Be((true, true));
    }

    [Fact]
    public void OtherKeyRelease_DoesNotResetEmergencyKeyRepeatState()
    {
        var state = new EmergencyHotkeyState(0, F12);

        Handle(state, KeyDown).Should().Be((true, true));
        state.TryHandle(KeyUp, 0, 0x41, 0, false, out bool invoke).Should().BeFalse();
        invoke.Should().BeFalse();
        Handle(state, KeyDown).Should().Be((true, false));
    }

    [Fact]
    public void AltShortcut_HandlesSystemKeyMessagesAndRelease()
    {
        var state = new EmergencyHotkeyState(0x01, F12);

        Handle(state, SysKeyDown, modifiers: 0x01).Should().Be((true, true));
        Handle(state, SysKeyUp).Should().Be((true, false));
        Handle(state, SysKeyDown, modifiers: 0x01).Should().Be((true, true));
    }

    private static (bool Consumed, bool Invoked) Handle(EmergencyHotkeyState state,
        uint message, uint flags = 0, uint modifiers = 0, bool suppressed = false)
    {
        bool consumed = state.TryHandle(message, flags, F12, modifiers, suppressed,
            out bool invoked);
        return (consumed, invoked);
    }
}
