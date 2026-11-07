using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.AI;
using MacroSuite.SDK.AI;
using Xunit;

namespace MacroSuite.Core.Tests.AI;

public class AIActionValidatorTests
{
    private readonly AIActionValidator _validator = new();

    [Fact]
    public void Validate_NullAction_ReturnsFailure()
    {
        var result = _validator.Validate(null!);
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("null");
    }

    [Theory]
    [InlineData("format_hard_drive")]
    [InlineData("shutdown")]
    [InlineData("run_cmd")]
    [InlineData("powershell")]
    public void Validate_DisallowedActionType_ReturnsFailure(string invalidType)
    {
        var action = new AIAction { ActionType = invalidType };
        var result = _validator.Validate(action);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("safety policy");
    }

    [Theory]
    [InlineData("click")]
    [InlineData("double_click")]
    public void Validate_ClickWithoutCoordinates_ReturnsFailure(string clickType)
    {
        var action = new AIAction { ActionType = clickType, X = null, Y = 100, Confidence = 0.9 };
        var result = _validator.Validate(action, expectedWindow: "App", currentWindow: "App");

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("requires valid X and Y");
    }

    [Fact]
    public void Validate_ClickOutsideAllowedRegion_ReturnsFailure()
    {
        var action = new AIAction { ActionType = "click", X = 500, Y = 500, Confidence = 0.95 };
        var region = new Rectangle(100, 100, 200, 200); // Bounds: [100, 100, 300, 300]

        var result = _validator.Validate(action, allowedRegion: region, expectedWindow: "App", currentWindow: "App");

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("outside the allowed bounding region");
    }

    [Fact]
    public void Validate_ClickInsideAllowedRegion_ReturnsSuccess()
    {
        var action = new AIAction { ActionType = "click", X = 200, Y = 200, Confidence = 0.95 };
        var region = new Rectangle(100, 100, 200, 200);

        var result = _validator.Validate(action, allowedRegion: region, expectedWindow: "App", currentWindow: "App");

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ExpectedWindowMismatch_ReturnsFailure()
    {
        var action = new AIAction { ActionType = "click", X = 100, Y = 100, Confidence = 0.9 };

        var result = _validator.Validate(
            action,
            expectedWindow: "Notepad",
            currentWindow: "Calculator");

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("does not match required target window");
    }

    [Fact]
    public void Validate_ExpectedWindowMatch_ReturnsSuccess()
    {
        var action = new AIAction { ActionType = "click", X = 100, Y = 100, Confidence = 0.9 };

        var result = _validator.Validate(
            action,
            expectedWindow: "Notepad",
            currentWindow: "Untitled - Notepad");

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_TextExceedingMaxLength_ReturnsFailure()
    {
        var action = new AIAction
        {
            ActionType = "text",
            Text = new string('A', 600),
            Confidence = 0.95
        };

        var result = _validator.Validate(action, expectedWindow: "App", currentWindow: "App");

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exceeds safety maximum");
    }

    [Fact]
    public void Validate_TextEmpty_ReturnsFailure()
    {
        var action = new AIAction
        {
            ActionType = "text",
            Text = "",
            Confidence = 0.95
        };

        var result = _validator.Validate(action, expectedWindow: "App", currentWindow: "App");

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("empty");
    }

    [Fact]
    public void Validate_LowConfidence_ReturnsFailure()
    {
        var action = new AIAction
        {
            ActionType = "click",
            X = 100,
            Y = 100,
            Confidence = 0.30 // Below 0.50 threshold
        };

        var result = _validator.Validate(action, expectedWindow: "App", currentWindow: "App");

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("minimum safety threshold");
    }

    [Fact]
    public void Validate_NoneActionWithLowConfidence_Allowed()
    {
        var action = new AIAction
        {
            ActionType = "none",
            Confidence = 0.1,
            Reasoning = "No target found"
        };

        var result = _validator.Validate(action);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ClickMissingConfidence_ReturnsFailure()
    {
        var action = new AIAction { ActionType = "click", X = 100, Y = 100 };
        var result = _validator.Validate(action, expectedWindow: "App", currentWindow: "App");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confidence");
    }

    [Fact]
    public void Validate_ClickEmptyExpectedWindow_ReturnsFailure()
    {
        var action = new AIAction { ActionType = "click", X = 100, Y = 100, Confidence = 0.9 };
        var result = _validator.Validate(action, expectedWindow: " ", currentWindow: "App");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Expected window");
    }

    [Fact]
    public void Validate_ClickExtremeCoordinates_ReturnsFailure()
    {
        var action = new AIAction { ActionType = "click", X = 99_999, Y = 99_999, Confidence = 0.9 };
        var result = _validator.Validate(action, expectedWindow: "App", currentWindow: "App");
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("virtual screen");
    }
}
