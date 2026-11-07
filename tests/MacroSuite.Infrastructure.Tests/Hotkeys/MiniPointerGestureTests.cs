using FluentAssertions;
using MacroSuite.App.Common;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Hotkeys;

public sealed class MiniPointerGestureTests
{
    [Fact] public void ClickAndSubthresholdJitter_PreserveSingleReleaseAction()
    { var gesture = new MiniPointerGesture(); gesture.Begin(200, 300); gesture.Move(202, 303, 4, 4).Should().BeFalse(); gesture.End().Should().BeFalse(); }
    [Theory] [InlineData(1.0)] [InlineData(1.25)] [InlineData(1.5)] [InlineData(2.0)]
    public void PhysicalDpiThreshold_DragSuppressesPendingButtonAction(double scale)
    { var gesture = new MiniPointerGesture(); gesture.Begin(-400, 300); gesture.Move(-400 + 4 * scale, 300, 4 * scale, 4 * scale).Should().BeTrue(); gesture.End().Should().BeTrue(); }
    [Fact] public void StateHideCaptureOrClickThroughInvalidation_CancelsReleaseWithoutCancellingNextClick()
    { var gesture = new MiniPointerGesture(); gesture.Begin(0, 0); gesture.Cancel(); gesture.End().Should().BeTrue(); gesture.Begin(0, 0); gesture.End().Should().BeFalse(); }
    [Fact] public void CrossingThreshold_StartsOnlyOneNativeMove()
    { var gesture = new MiniPointerGesture(); gesture.Begin(0, 0); gesture.Move(5, 0, 4, 4).Should().BeTrue(); gesture.Move(10, 0, 4, 4).Should().BeFalse(); gesture.End().Should().BeTrue(); }
}
