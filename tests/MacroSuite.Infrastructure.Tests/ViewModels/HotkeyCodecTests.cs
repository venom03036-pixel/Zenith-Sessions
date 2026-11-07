using FluentAssertions;
using MacroSuite.App.Common;
using System.Windows.Input;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

/// <summary>
/// Any key on the keyboard must be bindable, not just function keys.
/// </summary>
public class HotkeyCodecTests
{
    [Theory]
    [InlineData("F7", 0u, 0x76u)]
    [InlineData("F24", 0u, 0x87u)]
    [InlineData("G", 0u, 0x47u)]
    [InlineData("5", 0u, 0x35u)]
    [InlineData("Space", 0u, 0x20u)]
    [InlineData("Enter", 0u, 0x0Du)]
    [InlineData("Home", 0u, 0x24u)]
    [InlineData("PageDown", 0u, 0x22u)]
    [InlineData("Up", 0u, 0x26u)]
    [InlineData("Num7", 0u, 0x67u)]
    [InlineData("[", 0u, 0xDBu)]
    [InlineData("ESC", 0u, 0x1Bu)]
    [InlineData("Ctrl+1", 0x0002u, 0x31u)]
    [InlineData("Ctrl+Shift+Delete", 0x0006u, 0x2Eu)]
    [InlineData("Alt+Tab", 0x0001u, 0x09u)]
    [InlineData("Win+`", 0x0008u, 0xC0u)]
    [InlineData("Shift+9", 0x0004u, 0x39u)]
    [InlineData("Shift+Ctrl+L", 0x0006u, 0x4Cu)]
    [InlineData("Ctrl+Alt+Shift+Win+Num9", 0x000Fu, 0x69u)]
    public void TryParse_AcceptsAnyKey(string hotkey, uint expectedModifiers, uint expectedVk)
    {
        HotkeyCodec.TryParse(hotkey, out uint mod, out uint vk).Should().BeTrue();
        mod.Should().Be(expectedModifiers);
        vk.Should().Be(expectedVk);
    }

    [Fact]
    public void TryParse_RejectsEmptyInput()
    {
        HotkeyCodec.TryParse(null, out _, out _).Should().BeFalse();
        HotkeyCodec.TryParse("   ", out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("Other+F12")]
    [InlineData("Shfit+9")]
    [InlineData("Ctrl+L+9")]
    public void TryParse_RejectsUnknownModifierRatherThanBindingTheBareKey(string hotkey)
    {
        HotkeyCodec.TryParse(hotkey, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(0u, 0x76u, "F7")]
    [InlineData(0x0002u, 0x31u, "Ctrl+1")]
    [InlineData(0x0006u, 0x2Eu, "Ctrl+Shift+Delete")]
    public void Format_RoundTripsModifiersAndVirtualKey(uint modifiers, uint vk, string expected)
    {
        HotkeyCodec.Format(modifiers, vk).Should().Be(expected);
        HotkeyCodec.AreEqual(HotkeyCodec.Format(modifiers, vk), expected).Should().BeTrue();
    }

    [Fact]
    public void AreEqual_IgnoresCaseAndModifierOrder()
    {
        HotkeyCodec.AreEqual("ctrl+shift+k", "Shift+Ctrl+K").Should().BeTrue();
        HotkeyCodec.AreEqual("F7", "F8").Should().BeFalse();
        // A modified binding is a different hotkey than the bare key.
        HotkeyCodec.AreEqual("Ctrl+1", "1").Should().BeFalse();
    }

    [Theory]
    [InlineData(Key.Add, 0x6Bu, "NumAdd")]
    [InlineData(Key.Subtract, 0x6Du, "NumSubtract")]
    [InlineData(Key.Decimal, 0x6Eu, "NumDecimal")]
    [InlineData(Key.Divide, 0x6Fu, "NumDivide")]
    [InlineData(Key.Multiply, 0x6Au, "NumMultiply")]
    [InlineData(Key.Separator, 0x6Cu, "NumSeparator")]
    public void NumpadOperators_PreservePhysicalKeyThroughCaptureAndEveryModifierCombination(
        Key key, uint expectedVk, string expectedName)
    {
        // KeyName is also used by WPF capture; exercise it without sending desktop input.
        HotkeyCodec.KeyName(key).Should().Be(expectedName);
        HotkeyCodec.TryParse(HotkeyCodec.KeyName(key), out uint bareModifiers, out uint capturedVk).Should().BeTrue();
        bareModifiers.Should().Be(0);
        capturedVk.Should().Be(expectedVk);
        for (uint modifiers = 0; modifiers <= 15; modifiers++)
        {
            string formatted = HotkeyCodec.Format(modifiers, expectedVk);
            HotkeyCodec.TryParse(formatted, out uint parsedModifiers, out uint parsedVk).Should().BeTrue();
            parsedModifiers.Should().Be(modifiers);
            parsedVk.Should().Be(expectedVk);
        }
    }

    [Theory]
    [InlineData("NumAdd", "=", 0xBBu)]
    [InlineData("NumSubtract", "-", 0xBDu)]
    [InlineData("NumDecimal", ".", 0xBEu)]
    [InlineData("NumDivide", "/", 0xBFu)]
    public void NumpadOperators_DoNotCollideWithExistingMainKeyboardBindings(string numpad, string mainKey, uint mainVk)
    {
        HotkeyCodec.AreEqual("Ctrl+Shift+" + numpad, "Ctrl+Shift+" + mainKey).Should().BeFalse();
        HotkeyCodec.VirtualKeyOf(mainKey).Should().Be(mainVk);
    }

    [Fact]
    public void NumpadOperators_KeepExistingNamedAndMultiplyBindingsReadable()
    {
        HotkeyCodec.AreEqual("Ctrl+Add", "Ctrl+NumAdd").Should().BeTrue();
        HotkeyCodec.AreEqual("Alt+Subtract", "Alt+NumSubtract").Should().BeTrue();
        HotkeyCodec.AreEqual("Shift+Decimal", "Shift+NumDecimal").Should().BeTrue();
        HotkeyCodec.AreEqual("Win+Divide", "Win+NumDivide").Should().BeTrue();
        HotkeyCodec.AreEqual("Ctrl+*", "Ctrl+NumMultiply").Should().BeTrue();
    }
}
