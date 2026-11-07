using System.Drawing;
using FluentAssertions;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    [Fact]
    public void DetectedImageClick_LoadApplyAndDuplicate_PreserveTargetAndOffsets()
    {
        using var vm = CreateViewModel();
        vm.NewMacroCommand.Execute(null);
        vm.AddDelayCommand.Execute(null);
        var click = new ClickStep(new Point(999, 888))
        {
            XVariable = "$" + ImageCondition.LastMatchXVariable,
            YVariable = ImageCondition.LastMatchYVariable,
            OffsetX = 48, OffsetY = -204
        };
        vm.ReplaceStepAt(0, click, reloadPanel: true);

        vm.ActionEditor.ClickDetectedImage.Should().BeTrue();
        vm.ActionEditor.ShowClickOffsetFields.Should().BeTrue();
        vm.ActionEditor.ShowPointFields.Should().BeFalse();
        vm.ActionEditor.ClickOffsetX.Should().Be(48);
        vm.ActionEditor.ClickOffsetY.Should().Be(-204);
        vm.ActionEditor.ClickOffsetX = 0;
        vm.ActionEditor.ClickOffsetY = 127;
        vm.ActionEditor.Apply().Should().BeTrue();
        vm.DuplicateStepCommand.Execute(null);

        var edited = (ClickStep)vm.GetStepAt(0)!;
        var copy = (ClickStep)vm.GetStepAt(1)!;
        edited.StepId.Should().Be(click.StepId);
        copy.StepId.Should().NotBe(click.StepId);
        foreach (ClickStep item in new[] { edited, copy })
        {
            item.XVariable.Should().Be(click.XVariable);
            item.YVariable.Should().Be(click.YVariable);
            item.OffsetX.Should().Be(0);
            item.OffsetY.Should().Be(127);
        }
        vm.Steps[1].ParameterTokens.Should().Contain("detected image").And.Contain("127");
        vm.Steps[1].ParameterTokens.Should().NotContain("999").And.NotContain("888");
    }

    [Fact]
    public void CustomClickVariables_StayIntactWhenUncheckedCheckboxLoadsAndActionApplies()
    {
        using var vm = CreateViewModel();
        vm.NewMacroCommand.Execute(null);
        vm.AddDelayCommand.Execute(null);
        var click = new ClickStep(new Point(1, 2))
        {
            XVariable = "$CustomX", YVariable = "CustomY", OffsetX = -7, OffsetY = 9,
            IsClientRelative = true, TargetWindowName = "Test window",
            RecordedClientWidth = 800, RecordedClientHeight = 600
        };
        vm.ReplaceStepAt(0, click, reloadPanel: true);

        vm.ActionEditor.ClickDetectedImage.Should().BeFalse();
        vm.ActionEditor.ShowClickOffsetFields.Should().BeTrue();
        vm.ActionEditor.ClickDetectedImage = false; // A binding refresh must not erase custom coordinates.
        vm.ActionEditor.Apply().Should().BeTrue();

        ((ClickStep)vm.GetStepAt(0)!).Should().BeEquivalentTo(click);
    }

    [Fact]
    public void DetectedImageCheckbox_RestoresCustomDraftWhenUnchecked_AndUsesScreenCoordinatesWhenEnabled()
    {
        using var vm = CreateViewModel();
        vm.NewMacroCommand.Execute(null);
        vm.AddDelayCommand.Execute(null);
        vm.ReplaceStepAt(0, new ClickStep(new Point(1, 2))
        { XVariable = "CustomX", YVariable = "$CustomY", OffsetX = -7, OffsetY = 9, IsClientRelative = true }, true);

        vm.ActionEditor.ClickDetectedImage = true;
        vm.ActionEditor.XVariable.Should().Be(ImageCondition.LastMatchXVariable);
        vm.ActionEditor.YVariable.Should().Be(ImageCondition.LastMatchYVariable);
        vm.ActionEditor.ClickOffsetY = 127;
        vm.ActionEditor.ClickDetectedImage = false;
        vm.ActionEditor.XVariable.Should().Be("CustomX");
        vm.ActionEditor.YVariable.Should().Be("$CustomY");
        vm.ActionEditor.ClickOffsetX.Should().Be(-7);
        vm.ActionEditor.ClickOffsetY.Should().Be(9);
        vm.ActionEditor.Apply().Should().BeTrue();
        ((ClickStep)vm.GetStepAt(0)!).IsClientRelative.Should().BeTrue();

        vm.ActionEditor.ClickDetectedImage = true;
        vm.ActionEditor.ClickOffsetX = 48;
        vm.ActionEditor.ClickOffsetY = -204;
        vm.ActionEditor.Apply().Should().BeTrue();
        var applied = (ClickStep)vm.GetStepAt(0)!;
        applied.IsClientRelative.Should().BeFalse();
        applied.XVariable.Should().Be(ImageCondition.LastMatchXVariable);
        applied.YVariable.Should().Be(ImageCondition.LastMatchYVariable);
        applied.OffsetX.Should().Be(48);
        applied.OffsetY.Should().Be(-204);
    }

    [Fact]
    public void TurningOffSavedDetectedClick_ReturnsToFixedPositionWithoutHiddenOffsets()
    {
        using var vm = CreateViewModel();
        vm.NewMacroCommand.Execute(null);
        vm.AddDelayCommand.Execute(null);
        vm.ReplaceStepAt(0, new ClickStep(new Point(10, 20))
        { XVariable = ImageCondition.LastMatchXVariable, YVariable = ImageCondition.LastMatchYVariable, OffsetY = 127 }, true);

        vm.ActionEditor.ClickDetectedImage = false;
        vm.ActionEditor.ShowPointFields.Should().BeTrue();
        vm.ActionEditor.ShowClickOffsetFields.Should().BeFalse();
        vm.ActionEditor.Apply().Should().BeTrue();
        var applied = (ClickStep)vm.GetStepAt(0)!;
        applied.XVariable.Should().BeNull();
        applied.YVariable.Should().BeNull();
        applied.OffsetX.Should().Be(0);
        applied.OffsetY.Should().Be(0);
        applied.Position.Should().Be(new Point(10, 20));
    }

    [Fact]
    public void ClickSummary_ShowsCustomCoordinateExpressionInsteadOfUnusedFallback()
    {
        var click = new ClickStep(new Point(999, 888))
        { XVariable = "CustomX", YVariable = "$CustomY", OffsetX = 48, OffsetY = -204 };
        var row = MacroStepItemViewModel.FromStep(click, 0);

        row.ParameterTokens.Should().Contain("x=$CustomX").And.Contain("y=$CustomY").And.Contain("48").And.Contain("-204");
        row.ParameterTokens.Should().NotContain("999").And.NotContain("888");
        row.Title.Should().NotContain("999").And.NotContain("888");
    }

    [Fact]
    public void EditingCoordinateVariableToCustomValue_DoesNotClearItWhenCheckboxBecomesUnchecked()
    {
        using var vm = CreateViewModel();
        vm.NewMacroCommand.Execute(null);
        vm.AddDelayCommand.Execute(null);
        vm.ReplaceStepAt(0, new ClickStep(Point.Empty)
        { XVariable = ImageCondition.LastMatchXVariable, YVariable = ImageCondition.LastMatchYVariable, OffsetY = 127 }, true);

        vm.ActionEditor.XVariable = "CustomX";
        vm.ActionEditor.ClickDetectedImage.Should().BeFalse();
        vm.ActionEditor.ClickDetectedImage = false;
        vm.ActionEditor.Apply().Should().BeTrue();
        var applied = (ClickStep)vm.GetStepAt(0)!;
        applied.XVariable.Should().Be("CustomX");
        applied.YVariable.Should().Be(ImageCondition.LastMatchYVariable);
        applied.OffsetY.Should().Be(127);
    }
}
