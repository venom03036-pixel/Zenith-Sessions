using System.Diagnostics;
using FluentAssertions;
using MacroSuite.Core.Input;
using Xunit;

namespace MacroSuite.Core.Tests.Input;

public sealed class RecordingPolicyTests
{
    [Fact]
    public void ReservedScriptHotkey_IsNeverRecorded()
    {
        var reserved = new ReservedHotkeyRegistry();
        reserved.Replace([0x78]); // F9

        RecordingPolicy.ShouldRecordKeyboard(
            InputMode.Recording, 0x78, reserved, foregroundIsMacroEngine: false, recordVk: 0x71, stopVk: 0x72)
            .Should().BeFalse();
    }

    [Fact]
    public void TargetAppKey_IsRecordedWhileRecording()
    {
        var reserved = new ReservedHotkeyRegistry();
        reserved.Replace([0x78]);

        RecordingPolicy.ShouldRecordKeyboard(
            InputMode.Recording, (int)'E', reserved, foregroundIsMacroEngine: false, recordVk: 0x71, stopVk: 0x72)
            .Should().BeTrue();
    }

    [Fact]
    public void OnlyConfiguredTransportKeys_StopRecording()
    {
        // Record F7, stop F10: a game's F2 must still be recorded.
        RecordingPolicy.IsTransportVirtualKey(0x71, recordVk: 0x76, stopVk: 0x79).Should().BeFalse();
        RecordingPolicy.IsTransportVirtualKey(0x76, recordVk: 0x76, stopVk: 0x79).Should().BeTrue();
        RecordingPolicy.IsTransportVirtualKey(0x79, recordVk: 0x76, stopVk: 0x79).Should().BeTrue();
        RecordingPolicy.IsTransportVirtualKey(0x1B, recordVk: 0x76, stopVk: 0x79).Should().BeFalse();
    }

    [Theory]
    [InlineData(0x7B, 0x1B)] // F12 is reserved; the game's Esc is recordable.
    [InlineData(0x1B, 0x7B)] // Choosing Esc explicitly reverses that reservation.
    public void EmergencyReservation_FollowsTheConfiguredKey(int emergencyKey, int gameKey)
    {
        var reserved = new ReservedHotkeyRegistry();
        reserved.Replace([emergencyKey]);

        RecordingPolicy.ShouldRecordKeyboard(InputMode.Recording, emergencyKey, reserved,
            foregroundIsMacroEngine: false, recordVk: 0x71, stopVk: 0x72).Should().BeFalse();
        RecordingPolicy.ShouldRecordKeyboard(InputMode.Recording, gameKey, reserved,
            foregroundIsMacroEngine: false, recordVk: 0x71, stopVk: 0x72).Should().BeTrue();
    }

    [Fact]
    public void KeysOnMacroEngine_AreNotRecorded()
    {
        var reserved = new ReservedHotkeyRegistry();
        RecordingPolicy.ShouldRecordKeyboard(
            InputMode.Recording, (int)'E', reserved, foregroundIsMacroEngine: true, recordVk: 0x71, stopVk: 0x72)
            .Should().BeFalse();
    }

    [Fact]
    public void MouseOnEngineOrPickerMode_IsNotRecorded()
    {
        InputModeGate.Set(InputMode.Recording);
        try
        {
            RecordingPolicy.ShouldRecordMouse(InputMode.Recording, hitIsMacroEngineWindow: true, Stopwatch.GetTimestamp())
                .Should().BeFalse();
            RecordingPolicy.ShouldRecordMouse(InputMode.PickingColor, hitIsMacroEngineWindow: false, Stopwatch.GetTimestamp())
                .Should().BeFalse();
            RecordingPolicy.ShouldRecordMouse(InputMode.Recording, hitIsMacroEngineWindow: false, Stopwatch.GetTimestamp())
                .Should().BeTrue();
        }
        finally
        {
            InputModeGate.Set(InputMode.Normal);
        }
    }

    [Fact]
    public void Enter_RestoresPreviousMode()
    {
        InputModeGate.Set(InputMode.Recording);
        try
        {
            using (InputModeGate.Enter(InputMode.PickingLocation))
            {
                InputModeGate.Current.Should().Be(InputMode.PickingLocation);
                InputModeGate.AcceptsRecording.Should().BeFalse();
            }
            InputModeGate.Current.Should().Be(InputMode.Recording);
        }
        finally
        {
            InputModeGate.Set(InputMode.Normal);
        }
    }
}
