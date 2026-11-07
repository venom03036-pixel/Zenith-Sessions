using FluentAssertions;
using MacroSuite.App.Common;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public class DurationDisplayTests
{
    [Theory]
    [InlineData(0, "0ms")]
    [InlineData(50, "50ms")]
    [InlineData(999, "999ms")]
    [InlineData(1000, "1.00s")]
    [InlineData(5000, "5.00s")]
    [InlineData(12500, "12.5s")]
    [InlineData(60_000, "1m")]
    [InlineData(90_000, "1m 30s")]
    [InlineData(3_600_000, "1h")]
    [InlineData(3_900_000, "1h 5m")]
    public void Format_ScalesUnits(double milliseconds, string expected)
    {
        DurationDisplay.Format(milliseconds).Should().Be(expected);
    }
}
