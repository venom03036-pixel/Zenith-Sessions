using FluentAssertions;
using MacroSuite.Core.Rules;
using Xunit;

namespace MacroSuite.Core.Tests.Rules;

public sealed class ConditionFailureMessageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActionableDiagnostic_PreservesStableCodeAndOriginalException(bool hasInnerException)
    {
        Exception? inner = hasInnerException ? new InvalidOperationException("Original failure") : null;
        var evaluation = ConditionEvaluation.Fail("ImageSearchRegionTooSmall", inner) with
        {
            ErrorMessage = "Choose a larger search area or capture a smaller template."
        };

        var failure = Assert.Throws<ConditionEvaluationException>(() => evaluation.ThrowIfFailed("Wait Image: M"));

        failure.Message.Should().Be("Wait Image: M: Choose a larger search area or capture a smaller template.");
        failure.ErrorCode.Should().Be("ImageSearchRegionTooSmall");
        failure.InnerException.Should().BeSameAs(inner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureWithoutDiagnostic_KeepsItsExistingErrorCodeMessage(bool hasInnerException)
    {
        Exception? inner = hasInnerException ? new InvalidOperationException("Original failure") : null;
        var evaluation = ConditionEvaluation.Fail("MatchFailed", inner);

        var failure = Assert.Throws<ConditionEvaluationException>(() => evaluation.ThrowIfFailed("Wait Image"));

        failure.Message.Should().Be("Wait Image: MatchFailed");
        failure.ErrorCode.Should().Be("MatchFailed");
        failure.InnerException.Should().BeSameAs(inner);
    }
}
