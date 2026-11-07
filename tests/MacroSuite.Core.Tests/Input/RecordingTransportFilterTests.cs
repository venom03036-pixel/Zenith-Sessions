using FluentAssertions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using Xunit;

namespace MacroSuite.Core.Tests.Input;

public sealed class RecordingTransportFilterTests
{
    private readonly record struct Stamped(long Sequence, InputEvent Input);
    private static Stamped Key(long sequence, int key, bool down) => new(sequence, new(down ? InputEventType.KeyDown : InputEventType.KeyUp, sequence, 0, 0, key));
    private static RecordingTransportFilter<Stamped> Filter() => new(p => p.Input, [(6u, 76u), (0u, 0x78u)]);
    private static void Feed(RecordingTransportFilter<Stamped> filter, List<Stamped> output, params Stamped[] packets)
    { foreach (var packet in packets) filter.Process(packet, output.Add); }

    [Fact]
    public void EntireControlShiftLChordIsExcludedIncludingModifiersAndRepeats()
    {
        var filter = Filter(); var output = new List<Stamped>();
        Feed(filter, output, Key(1, 0xa2, true), Key(2, 0xa0, true), Key(3, 76, true), Key(4, 76, true),
            Key(5, 0xa2, false), Key(6, 76, false), Key(7, 0xa0, false));
        filter.FlushOnStop(output.Add); output.Should().BeEmpty();
    }
    [Fact]
    public void OrdinaryModifierCombinationPreservesOriginalMetadataAndOrder()
    {
        var filter = Filter(); var output = new List<Stamped>();
        var input = new[] { Key(1, 0xa2, true), Key(2, 65, true), Key(3, 65, false), Key(4, 0xa2, false) };
        Feed(filter, output, input); output.Should().Equal(input);
    }
    [Fact]
    public void ModifierUsedByEarlierMouseActionRemainsBalancedAcrossTransportChord()
    {
        var filter = Filter(); var output = new List<Stamped>();
        Feed(filter, output, Key(1, 0xa2, true), new(2, new(InputEventType.MouseDown, 2, 10, 20, (int)MouseButton.Left)),
            Key(3, 0xa0, true), Key(4, 76, true), Key(5, 76, false), Key(6, 0xa0, false),
            new(7, new(InputEventType.MouseUp, 7, 10, 20, (int)MouseButton.Left)), Key(8, 0xa2, false));
        output.Select(p => p.Sequence).Should().Equal(1, 2, 7, 8);
    }
    [Fact]
    public void ModifierStillHeldAfterTransportIsRestoredBeforeSubsequentRealInput()
    {
        var filter = Filter(); var output = new List<Stamped>();
        Feed(filter, output, Key(1, 0xa2, true), Key(2, 0xa0, true), Key(3, 76, true), Key(4, 76, false),
            Key(5, 65, true), Key(6, 65, false), Key(7, 0xa0, false), Key(8, 0xa2, false));
        output.Select(p => p.Sequence).Should().Equal(1, 2, 5, 6, 7, 8);
    }
    [Fact]
    public void ModifierTapAndNormalHeldModifierAtStopAreNotLost()
    {
        var filter = Filter(); var output = new List<Stamped>();
        Feed(filter, output, Key(1, 0xa0, true), Key(2, 0xa0, false), Key(3, 0xa2, true));
        filter.FlushOnStop(output.Add); output.Select(p => p.Sequence).Should().Equal(1, 2, 3);
    }
    [Fact]
    public void NormalPressDoesNotBecomeTransportOnAutorepeatAfterModifierChange()
    {
        var filter = Filter(); var output = new List<Stamped>();
        Feed(filter, output, Key(1, 76, true), Key(2, 0xa2, true), Key(3, 0xa0, true), Key(4, 76, true),
            Key(5, 76, false), Key(6, 0xa0, false), Key(7, 0xa2, false));
        output.Select(p => p.Sequence).Should().Equal(1, 2, 3, 4, 5, 6, 7);
    }
    [Fact]
    public void NormalRepeatedModifiersRetainEveryPacketWithBoundedBuffer()
    {
        var filter = Filter(); var output = new List<Stamped>();
        var input = Enumerable.Range(1, 100).Select(i => Key(i, 0xa2, true)).Append(Key(101, 0xa2, false)).ToArray();
        Feed(filter, output, input); output.Should().Equal(input);
    }
}
