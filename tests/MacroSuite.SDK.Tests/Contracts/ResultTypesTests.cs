using MacroSuite.SDK.Contracts;

namespace MacroSuite.SDK.Tests.Contracts;

/// <summary>
/// A plugin's failure must always carry a reason the host can show; a success needs none.
/// </summary>
public sealed class ResultTypesTests
{
    [Fact]
    public void StepResultOk_IsSuccessful_AndKeepsOptionalMessageAndDuration()
    {
        var bare = StepResult.Ok();
        var detailed = StepResult.Ok("Done.", TimeSpan.FromMilliseconds(25));

        Assert.True(bare.Success);
        Assert.Null(bare.Message);
        Assert.Equal(TimeSpan.Zero, bare.Duration);
        Assert.True(detailed.Success);
        Assert.Equal("Done.", detailed.Message);
        Assert.Equal(TimeSpan.FromMilliseconds(25), detailed.Duration);
    }

    [Fact]
    public void StepResultFailed_IsUnsuccessful_AndKeepsReasonAndDuration()
    {
        var result = StepResult.Failed("Database unreachable.", TimeSpan.FromSeconds(2));

        Assert.False(result.Success);
        Assert.Equal("Database unreachable.", result.Message);
        Assert.Equal(TimeSpan.FromSeconds(2), result.Duration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void StepResultFailed_WithoutReason_IsRejected(string? reason)
    {
        Assert.ThrowsAny<ArgumentException>(() => StepResult.Failed(reason!));
    }

    [Fact]
    public void ValidationResultSuccess_IsValid_WithNoMessage()
    {
        var result = ValidationResult.Success();

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void ValidationResultFailure_IsInvalid_AndKeepsReason()
    {
        var result = ValidationResult.Failure("Parameter 'table' is required.");

        Assert.False(result.IsValid);
        Assert.Equal("Parameter 'table' is required.", result.ErrorMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidationResultFailure_WithoutReason_IsRejected(string? reason)
    {
        Assert.ThrowsAny<ArgumentException>(() => ValidationResult.Failure(reason!));
    }
}
