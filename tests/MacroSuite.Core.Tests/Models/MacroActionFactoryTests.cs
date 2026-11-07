using FluentAssertions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Vision;
using Xunit;

namespace MacroSuite.Core.Tests.Models;

public sealed class MacroActionFactoryTests
{
    [Fact]
    public void Convert_ClickToMove_PreservesCoordinates()
    {
        var click = new ClickStep(new System.Drawing.Point(2733, 794), MouseButton.Right, 2);
        var converted = MacroActionFactory.Convert(click, EditableActionKind.MouseMove).Should().BeOfType<MouseMoveStep>().Subject;
        converted.Position.X.Should().Be(2733);
        converted.Position.Y.Should().Be(794);
        converted.StepId.Should().Be(click.StepId);
    }

    [Fact]
    public void Convert_WaitToVisualWait_UsesSafeDefaults()
    {
        var wait = new DelayStep(TimeSpan.FromMilliseconds(1000));
        var visual = MacroActionFactory.Convert(wait, EditableActionKind.VisualWait).Should().BeOfType<VisualWaitStep>().Subject;
        visual.SearchMode.Should().Be("Fixed");
        visual.SimilarityPercent.Should().Be(90);
        visual.TimeoutMs.Should().Be(1000);
    }

    [Fact]
    public void UpdateWait_ChangesDurationOnly()
    {
        var wait = new DelayStep(TimeSpan.FromMilliseconds(1000));
        var updated = MacroActionFactory.UpdateWait(wait, 250).Should().BeOfType<DelayStep>().Subject;
        updated.Duration.Should().Be(TimeSpan.FromMilliseconds(250));
        updated.StepId.Should().Be(wait.StepId);
    }

    [Fact]
    public void Convert_ClickToColorWait_KeepsPointNotGuessedColorFromClick()
    {
        var click = new ClickStep(new System.Drawing.Point(10, 20));
        var wait = MacroActionFactory.Convert(click, EditableActionKind.ColorWait).Should().BeOfType<WaitUntilStep>().Subject;
        wait.Condition.Should().BeOfType<ColorCondition>().Which.Location.Should().Be(new System.Drawing.Point(10, 20));
    }

    [Fact]
    public void UpdateSweep_ReplacesMotionFieldsAndKeepsId()
    {
        var sweep = new MouseSweepStep(-10, 2, 400)
        {
            PlaybackHz = 120,
            Interpolation = MouseInterpolation.Linear
        };
        var updated = MacroActionFactory.UpdateSweep(
            sweep, -250, 12, 850, 1.0, 0.8, 240, MouseInterpolation.SmootherStep, 20, 75, true, false, MouseSweepProfile.Smooth)
            .Should().BeOfType<MouseSweepStep>().Subject;
        updated.StepId.Should().Be(sweep.StepId);
        updated.DeltaX.Should().Be(-250);
        updated.DeltaY.Should().Be(12);
        updated.DurationMs.Should().Be(850);
        updated.PlaybackHz.Should().Be(240);
        updated.SensitivityY.Should().BeApproximately(0.8, 0.0001);
        updated.Interpolation.Should().Be(MouseInterpolation.SmootherStep);
        updated.FilterVerticalJitter.Should().BeTrue();
        updated.VerticalSmoothing.Should().Be(75);
    }

    [Fact]
    public void Convert_WaitToRepeatUntil_SetsUntilFlag()
    {
        var wait = new DelayStep(TimeSpan.FromMilliseconds(100));
        var loop = MacroActionFactory.Convert(wait, EditableActionKind.RepeatUntil).Should().BeOfType<LoopStep>().Subject;
        loop.RepeatUntil.Should().BeTrue();
        loop.WhileCondition.Should().BeOfType<VisualPresenceCondition>()
            .Which.Kind.Should().Be(VisualPresenceKind.ColorPresent);
        MacroActionFactory.KindOf(loop).Should().Be(EditableActionKind.RepeatUntil);
    }

    [Fact]
    public void Convert_ToRepeatUntilGone_SetsColorAbsent()
    {
        var wait = new DelayStep(TimeSpan.FromMilliseconds(100));
        var loop = MacroActionFactory.Convert(wait, EditableActionKind.RepeatUntilGone).Should().BeOfType<LoopStep>().Subject;
        loop.RepeatUntil.Should().BeTrue();
        loop.WhileCondition.Should().BeOfType<VisualPresenceCondition>()
            .Which.Kind.Should().Be(VisualPresenceKind.ColorAbsent);
        loop.Label.Should().Contain("gone");
        MacroActionFactory.KindOf(loop).Should().Be(EditableActionKind.RepeatUntilGone);
    }

    [Fact]
    public void Convert_RepeatUntilGoneToRepeatUntil_SetsColorPresent()
    {
        var presence = MacroActionFactory.ClonePresence(
            MacroActionFactory.DefaultPresence(8, 9),
            VisualPresenceKind.ColorAbsent);
        var source = new LoopStep(10_000, [], presence)
        {
            RepeatUntil = true,
            MinBodyDelayMs = 25
        };

        var converted = MacroActionFactory.Convert(source, EditableActionKind.RepeatUntil).Should().BeOfType<LoopStep>().Subject;
        converted.RepeatUntil.Should().BeTrue();
        converted.MinBodyDelayMs.Should().Be(25);
        converted.WhileCondition.Should().BeOfType<VisualPresenceCondition>()
            .Which.Kind.Should().Be(VisualPresenceKind.ColorPresent);
        converted.Label.Should().Contain("present");
        MacroActionFactory.KindOf(converted).Should().Be(EditableActionKind.RepeatUntil);
    }

    [Fact]
    public void Convert_RepeatUntilToRepeatUntilGone_SetsColorAbsentAndPreservesBodyDelay()
    {
        var presence = MacroActionFactory.DefaultPresence(8, 9);
        var source = new LoopStep(10_000, [], presence)
        {
            RepeatUntil = true,
            MinBodyDelayMs = 25
        };

        var converted = MacroActionFactory.Convert(source, EditableActionKind.RepeatUntilGone).Should().BeOfType<LoopStep>().Subject;
        converted.RepeatUntil.Should().BeTrue();
        converted.MinBodyDelayMs.Should().Be(25);
        converted.WhileCondition.Should().BeOfType<VisualPresenceCondition>()
            .Which.Kind.Should().Be(VisualPresenceKind.ColorAbsent);
        converted.Label.Should().Contain("gone");
    }

    [Fact]
    public void UpdateAi_WritesExpectedWindowAndMinConfidence()
    {
        var source = new AIStep("Click play");
        var updated = MacroActionFactory.UpdateAi(source, "Click play", 12_000, "Roblox", 0.8)
            .Should().BeOfType<AIStep>().Subject;
        updated.ExpectedWindow.Should().Be("Roblox");
        updated.MinConfidence.Should().Be(0.8);
        updated.Timeout.Should().Be(TimeSpan.FromMilliseconds(12_000));
    }

    [Fact]
    public void UpdateAi_EmptyExpectedWindow_ClearsValue()
    {
        var source = new AIStep("Click play") { ExpectedWindow = "Roblox", MinConfidence = 0.9 };
        var updated = MacroActionFactory.UpdateAi(source, "Click play", 1000, "  ", null)
            .Should().BeOfType<AIStep>().Subject;
        updated.ExpectedWindow.Should().BeNull();
        updated.MinConfidence.Should().BeNull();
    }

    [Fact]
    public void KindOf_ImageWait_IsDistinctFromColorWait()
    {
        var image = new WaitUntilStep(new ImageCondition("template.png"));
        var color = new WaitUntilStep(new ColorCondition(new System.Drawing.Point(1, 2), 9, 8, 7));
        MacroActionFactory.KindOf(image).Should().Be(EditableActionKind.ImageWait);
        MacroActionFactory.KindOf(color).Should().Be(EditableActionKind.ColorWait);
        MacroActionFactory.KindNames[(int)EditableActionKind.ImageWait].Should().Be("Image Wait");
        ((int)EditableActionKind.RepeatUntilGone).Should().Be(12);
        ((int)EditableActionKind.ImageWait).Should().Be(13);
    }

    [Fact]
    public void UpdateImageWait_PreservesTemplateAndDoesNotRebuildColor()
    {
        var wait = new WaitUntilStep(new ImageCondition("hero.png", 0.85))
        {
            Timeout = TimeSpan.FromSeconds(15),
            ThrowOnTimeout = true
        };

        var updated = MacroActionFactory.UpdateImageWait(wait, 8000, 90, 2, waitWhile: false)
            .Should().BeOfType<WaitUntilStep>().Subject;
        updated.Condition.Should().BeOfType<ImageCondition>().Which.TemplatePath.Should().Be("hero.png");
        updated.Timeout.Should().Be(TimeSpan.FromMilliseconds(8000));
        updated.RequiredConfirmations.Should().Be(2);
    }

    [Fact]
    public void UpdateImageWait_ReplacementSwapsTemplate()
    {
        var wait = new WaitUntilStep(new ImageCondition("hero.png", 0.85))
        {
            Timeout = TimeSpan.FromSeconds(15),
            ThrowOnTimeout = true
        };

        var updated = MacroActionFactory.UpdateImageWait(
                wait, 8000, 90, 2, waitWhile: true, replacement: new ImageCondition("boss.png", 0.9))
            .Should().BeOfType<WaitUntilStep>().Subject;
        updated.Condition.Should().BeOfType<ImageCondition>().Which.TemplatePath.Should().Be("boss.png");
        updated.WaitWhile.Should().BeTrue();
        updated.Timeout.Should().Be(TimeSpan.FromMilliseconds(8000));
    }

    [Fact]
    public void Convert_ImageWaitToColorWait_DoesNotKeepTemplate()
    {
        var wait = new WaitUntilStep(new ImageCondition("hero.png"));
        var converted = MacroActionFactory.Convert(wait, EditableActionKind.ColorWait).Should().BeOfType<WaitUntilStep>().Subject;
        converted.Condition.Should().BeOfType<ColorCondition>();
        converted.Condition.Should().NotBeOfType<ImageCondition>();
    }

    [Fact]
    public void UpdateColorWait_PreservesWaitWhileAndRelabels()
    {
        var wait = new WaitUntilStep(new ColorCondition(new System.Drawing.Point(1, 2), 9, 8, 7))
        {
            WaitWhile = true
        };

        var updated = MacroActionFactory.UpdateColorWait(
            wait, 4, 5, 1, 1, 9, 8, 7, 10, 1, ColorComparisonMode.Rgb, 1000, 50, 1, waitWhile: true)
            .Should().BeOfType<WaitUntilStep>().Subject;

        updated.WaitWhile.Should().BeTrue();
        updated.Label.Should().StartWith("Wait while:");
    }
}
