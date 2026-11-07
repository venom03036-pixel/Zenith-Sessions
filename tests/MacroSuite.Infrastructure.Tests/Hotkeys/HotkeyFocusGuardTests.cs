using FluentAssertions;
using MacroSuite.App.Common;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Hotkeys;

public sealed class HotkeyFocusGuardTests
{
    [Fact]
    public void NullFocus_IsNotTextEditing()
    {
        HotkeyFocusGuard.IsTextEditingContext(null).Should().BeFalse();
    }
}
