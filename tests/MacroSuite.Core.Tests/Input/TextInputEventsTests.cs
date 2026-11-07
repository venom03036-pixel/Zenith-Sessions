using FluentAssertions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using Xunit;

namespace MacroSuite.Core.Tests.Input;

public sealed class TextInputEventsTests
{
    [Fact]
    public void FromText_EmitsUnicodeDownAndUpForPrintableCharacters()
    {
        InputEvent[] events = TextInputEvents.FromText("Aé");

        events.Should().HaveCount(4);
        events[0].Type.Should().Be(InputEventType.Unicode);
        events[0].Data.Should().Be('A');
        events[1].Type.Should().Be(InputEventType.UnicodeUp);
        events[1].Data.Should().Be('A');
        events[2].Data.Should().Be('é');
        events[3].Type.Should().Be(InputEventType.UnicodeUp);
    }

    [Fact]
    public void FromText_MapsNewlinesAndTabsToVirtualKeys()
    {
        InputEvent[] events = TextInputEvents.FromText("a\r\n\tb");

        events.Select(e => (e.Type, e.Data)).Should().Equal(
            (InputEventType.Unicode, (int)'a'),
            (InputEventType.UnicodeUp, (int)'a'),
            (InputEventType.KeyDown, TextInputEvents.VkReturn),
            (InputEventType.KeyUp, TextInputEvents.VkReturn),
            (InputEventType.KeyDown, TextInputEvents.VkTab),
            (InputEventType.KeyUp, TextInputEvents.VkTab),
            (InputEventType.Unicode, (int)'b'),
            (InputEventType.UnicodeUp, (int)'b'));
    }
}
