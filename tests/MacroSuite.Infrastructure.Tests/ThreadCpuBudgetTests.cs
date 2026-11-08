using FluentAssertions;
using Xunit;

namespace MacroSuite.Infrastructure.Tests;

/// <summary>
/// The CPU budget must fail a pass that computes and must not fail a pass that only waits,
/// so a busy machine cannot turn it into another wall-clock flake.
/// </summary>
public sealed class ThreadCpuBudgetTests
{
    [Fact]
    public void PassThatComputesPastItsBudget_FailsAndReportsWhatItUsed()
    {
        var error = Assert.Throws<ThreadCpuBudgetExceededException>(() =>
            ThreadCpuBudget.Run("Spinning pass", TimeSpan.FromMilliseconds(50), () => ThreadCpuBudget.Burn(TimeSpan.FromMilliseconds(300))));

        error.Use.Cpu.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(300));
        error.Message.Should().Contain("Spinning pass").And.Contain("over its 50 ms CPU budget")
            .And.Contain("user").And.Contain("kernel").And.Contain("MB allocated").And.Contain("gen2");
    }

    [Fact]
    public void PassThatOnlyWaits_DoesNotCountAgainstItsBudget()
    {
        var use = ThreadCpuBudget.Run("Waiting pass", TimeSpan.FromMilliseconds(50), () => Thread.Sleep(400));

        use.Wall.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(350), "the pass waited");
        use.Cpu.Should().BeLessThan(TimeSpan.FromMilliseconds(50), "waiting uses no CPU of the thread");
    }
}
