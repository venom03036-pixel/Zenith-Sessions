using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using Xunit;

namespace MacroSuite.Core.Tests.Input;

public sealed class RecordingHotkeyFilterTests
{
    private static InputEvent Down(int key) => new(InputEventType.KeyDown, 0, 0, 0, key);
    private static InputEvent Up(int key) => new(InputEventType.KeyUp, 0, 0, 0, key);

    [Theory]
    [InlineData(4u, 0x10)]
    [InlineData(4u, 0xA0)]
    [InlineData(4u, 0xA1)]
    [InlineData(2u, 0x11)]
    [InlineData(2u, 0xA2)]
    [InlineData(2u, 0xA3)]
    [InlineData(1u, 0x12)]
    [InlineData(1u, 0xA4)]
    [InlineData(1u, 0xA5)]
    [InlineData(8u, 0x5B)]
    [InlineData(8u, 0x5C)]
    public void GenericAndPhysicalModifierVariants_RemoveOnlyTheirControlPress(uint modifier, int modifierKey)
    {
        var filter = new RecordingHotkeyFilter(modifier, '9');
        Assert.True(filter.ShouldRecord(Down(modifierKey)));
        Assert.False(filter.ShouldRecord(Down('9')));
        Assert.False(filter.ShouldRecord(Down('9')));
        Assert.True(filter.ShouldRecord(Up(modifierKey)));
        Assert.False(filter.ShouldRecord(Up('9')));
        Assert.True(filter.ShouldRecord(Down('9')));
        Assert.True(filter.ShouldRecord(Up('9')));
    }

    [Fact]
    public void ThreeModifierChord_PreservesAllModifierEventsAndSuppressesPairedRelease()
    {
        var filter = new RecordingHotkeyFilter(1 | 2 | 4, 'L');
        Assert.True(filter.ShouldRecord(Down(0xA2)));
        Assert.True(filter.ShouldRecord(Down(0xA5)));
        Assert.True(filter.ShouldRecord(Down(0xA1)));
        Assert.False(filter.ShouldRecord(Down('L')));
        Assert.True(filter.ShouldRecord(Up(0xA5)));
        Assert.True(filter.ShouldRecord(Up(0xA1)));
        Assert.True(filter.ShouldRecord(Up(0xA2)));
        Assert.False(filter.ShouldRecord(Down('L'))); // held-key repeat after all modifiers released
        Assert.False(filter.ShouldRecord(Up('L')));
        Assert.True(filter.ShouldRecord(Down('L')));
        Assert.True(filter.ShouldRecord(Up('L')));
    }

    [Fact]
    public void FourModifierChord_CanBeUsedRepeatedlyInTheSameSession()
    {
        var filter = new RecordingHotkeyFilter(15, 0x79); // Ctrl+Alt+Shift+Win+F10
        foreach (int modifier in new[] { 0x10, 0x11, 0x12, 0x5C }) Assert.True(filter.ShouldRecord(Down(modifier)));
        Assert.False(filter.ShouldRecord(Down(0x79)));
        Assert.False(filter.ShouldRecord(Up(0x79)));
        Assert.False(filter.ShouldRecord(Down(0x79)));
        Assert.False(filter.ShouldRecord(Up(0x79)));
        foreach (int modifier in new[] { 0x10, 0x11, 0x12, 0x5C }) Assert.True(filter.ShouldRecord(Up(modifier)));
        Assert.True(filter.ShouldRecord(Down(0x79)));
        Assert.True(filter.ShouldRecord(Up(0x79)));
    }

    [Fact]
    public void PlainKeyAndDifferentOrExtraModifiers_ArePreserved()
    {
        var filter = new RecordingHotkeyFilter(4, '9');
        Assert.True(filter.ShouldRecord(Down('9')));
        Assert.True(filter.ShouldRecord(Up('9')));
        Assert.True(filter.ShouldRecord(Down(0x11)));
        Assert.True(filter.ShouldRecord(Down('9'))); // Ctrl+9, not Shift+9
        Assert.True(filter.ShouldRecord(Up('9')));
        Assert.True(filter.ShouldRecord(Down(0x10)));
        Assert.True(filter.ShouldRecord(Down('9'))); // Ctrl+Shift+9 is also not an exact match
        Assert.True(filter.ShouldRecord(Up('9')));
        Assert.True(filter.ShouldRecord(Up(0x11)));
        Assert.False(filter.ShouldRecord(Down('9')));
        Assert.False(filter.ShouldRecord(Up('9')));
        Assert.True(filter.ShouldRecord(Down('8')));
        Assert.True(filter.ShouldRecord(Up('8')));
    }

    [Fact]
    public void ModifiersAddedAfterAnOrdinaryPress_DoNotRemoveItsReleaseOrRepeats()
    {
        var filter = new RecordingHotkeyFilter(4, '9');
        Assert.True(filter.ShouldRecord(Down('9')));
        Assert.True(filter.ShouldRecord(Down(0x10)));
        Assert.True(filter.ShouldRecord(Down('9')));
        Assert.True(filter.ShouldRecord(Up('9')));
        Assert.False(filter.ShouldRecord(Down('9'))); // a new physical press now matches
        Assert.False(filter.ShouldRecord(Up('9')));
    }

    [Fact]
    public void MatchingPressRepeatsStaySuppressed_WhenExtraModifiersArePressed()
    {
        var filter = new RecordingHotkeyFilter(4, '9');
        Assert.True(filter.ShouldRecord(Down(0x10)));
        Assert.False(filter.ShouldRecord(Down('9')));
        Assert.True(filter.ShouldRecord(Down(0x11)));
        Assert.False(filter.ShouldRecord(Down('9')));
        Assert.True(filter.ShouldRecord(Up(0x10)));
        Assert.False(filter.ShouldRecord(Up('9')));
        Assert.True(filter.ShouldRecord(Down('9'))); // next press is Ctrl+9
        Assert.True(filter.ShouldRecord(Up('9')));
    }

    [Fact]
    public void ReleasingOneSide_DoesNotClearTheOtherHeldModifier()
    {
        var filter = new RecordingHotkeyFilter(4, '9');
        Assert.True(filter.ShouldRecord(Down(0xA0)));
        Assert.True(filter.ShouldRecord(Down(0xA1)));
        Assert.True(filter.ShouldRecord(Up(0xA0)));
        Assert.False(filter.ShouldRecord(Down('9')));
        Assert.False(filter.ShouldRecord(Up('9')));
        Assert.True(filter.ShouldRecord(Up(0xA1)));
        Assert.True(filter.ShouldRecord(Down('9')));
        Assert.True(filter.ShouldRecord(Up('9')));
    }

    [Theory]
    [InlineData(InputEventType.MouseMove)]
    [InlineData(InputEventType.MouseDown)]
    [InlineData(InputEventType.MouseUp)]
    [InlineData(InputEventType.MouseWheel)]
    [InlineData(InputEventType.MouseMoveRelative)]
    [InlineData(InputEventType.MouseHWheel)]
    [InlineData(InputEventType.Unicode)]
    [InlineData(InputEventType.UnicodeUp)]
    [InlineData((InputEventType)255)]
    public void NonKeyboardEvents_PassWithoutChangingModifierState(InputEventType type)
    {
        var filter = new RecordingHotkeyFilter(4, '9');
        Assert.True(filter.ShouldRecord(new InputEvent(type, 1, 20, 30, 0x10)));
        Assert.True(filter.ShouldRecord(new InputEvent(type, 2, 20, 30, '9')));
        Assert.True(filter.ShouldRecord(Down('9')));
        Assert.True(filter.ShouldRecord(Up('9')));
    }

    [Theory]
    [InlineData(4u, 0u)]
    [InlineData(4u, 256u)]
    [InlineData(16u, 0x79u)]
    [InlineData(uint.MaxValue, 0x79u)]
    [InlineData(0u, 0x10u)]
    public void DisabledOrInvalidConfiguration_NeverDropsEvents(uint modifiers, uint key)
    {
        var filter = new RecordingHotkeyFilter(modifiers, key);
        Assert.True(filter.ShouldRecord(Down(0x10)));
        Assert.True(filter.ShouldRecord(Down(0x79)));
        Assert.True(filter.ShouldRecord(Down(0)));
        Assert.True(filter.ShouldRecord(Up(0x79)));
        Assert.True(filter.ShouldRecord(Up(0x10)));
    }

    [Fact]
    public void BareFunctionShortcut_OnlyMatchesWithoutModifiers()
    {
        var filter = new RecordingHotkeyFilter(0, 0x79);
        Assert.False(filter.ShouldRecord(Down(0x79)));
        Assert.False(filter.ShouldRecord(Up(0x79)));
        Assert.True(filter.ShouldRecord(Down(0xA0)));
        Assert.True(filter.ShouldRecord(Down(0x79)));
        Assert.True(filter.ShouldRecord(Up(0x79)));
    }

    [Fact]
    public void UnpairedReleaseAndANewSession_DoNotInheritSuppression()
    {
        var first = new RecordingHotkeyFilter(4, '9');
        Assert.True(first.ShouldRecord(Up('9')));
        Assert.True(first.ShouldRecord(Down(0x10)));
        Assert.False(first.ShouldRecord(Down('9')));
        var second = new RecordingHotkeyFilter(4, '9');
        Assert.True(second.ShouldRecord(Up('9')));
        Assert.True(second.ShouldRecord(Down('9')));
        Assert.True(second.ShouldRecord(Up('9')));
    }

    [Fact]
    public void InactiveShortcut_PreservesOrdinaryChords_AndVisibilityChangesKeepPressPairs()
    {
        var filter = new RecordingHotkeyFilter(4, '9');
        Assert.True(filter.ShouldRecord(Down(0x10), shortcutActive: false));
        Assert.True(filter.ShouldRecord(Down('9'), shortcutActive: false));
        Assert.True(filter.ShouldRecord(Down('9'), shortcutActive: true));
        Assert.True(filter.ShouldRecord(Up('9'), shortcutActive: true));
        Assert.False(filter.ShouldRecord(Down('9'), shortcutActive: true));
        Assert.False(filter.ShouldRecord(Down('9'), shortcutActive: false));
        Assert.False(filter.ShouldRecord(Up('9'), shortcutActive: false));
        Assert.True(filter.ShouldRecord(Down('9'), shortcutActive: false));
        Assert.True(filter.ShouldRecord(Up('9'), shortcutActive: false));
    }

    [Fact]
    public void ConsumerFiltering_AllocatesNothingPerEvent()
    {
        var filter = new RecordingHotkeyFilter(4, '9');
        var modifierDown = Down(0x10); var modifierUp = Up(0x10);
        var keyDown = Down('9'); var keyUp = Up('9');
        for (int i = 0; i < 100; i++)
        {
            filter.ShouldRecord(in modifierDown); filter.ShouldRecord(in keyDown);
            filter.ShouldRecord(in modifierUp); filter.ShouldRecord(in keyUp);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        int suppressed = 0;
        for (int i = 0; i < 10_000; i++)
        {
            if (!filter.ShouldRecord(in modifierDown)) suppressed++;
            if (!filter.ShouldRecord(in keyDown)) suppressed++;
            if (!filter.ShouldRecord(in modifierUp)) suppressed++;
            if (!filter.ShouldRecord(in keyUp)) suppressed++;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(20_000, suppressed);
        Assert.Equal(0, allocated);
    }
}
