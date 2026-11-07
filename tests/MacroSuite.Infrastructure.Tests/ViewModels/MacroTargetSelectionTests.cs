using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Models;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    [Fact]
    public void NewMacro_DefaultScreenTarget_DetectsAndClearsRealTargetDraftChanges()
    {
        using var vm = CreateViewModel(); vm.NewMacroCommand.Execute(null);
        vm.SelectedScript!.Target!.Mode.Should().Be(MacroTargetMode.Screen);
        vm.HasMacroMetadataChanges.Should().BeFalse();
        vm.TargetModeIndex = (int)MacroTargetMode.ActiveExternalWindow;
        vm.HasMacroMetadataChanges.Should().BeTrue();
        vm.TargetModeIndex = (int)MacroTargetMode.Screen;
        vm.HasMacroMetadataChanges.Should().BeFalse();
    }

    [Fact]
    public void WindowTarget_StoresPortableRule_AndSelectionDoesNotRetargetAnotherMacro()
    {
        using var vm = CreateViewModel(); vm.NewMacroCommand.Execute(null);
        var first = vm.SelectedScript!;
        var window = new TargetWindowInfo((IntPtr)1234, 5678, 42, "TargetFixture", "Fixture document", new Rectangle(10, 20, 800, 600), 7);
        vm.AvailableTargetWindows.Add(window); vm.SelectedTargetWindow = window;
        vm.TargetModeIndex = (int)MacroTargetMode.SpecificWindow;
        first.Target.Should().Be(new MacroTargetRule { Mode = MacroTargetMode.SpecificWindow, ApplicationId = "TargetFixture", WindowTitle = "Fixture document" });
        vm.NewMacroCommand.Execute(null);
        vm.SelectedTargetWindow.Should().BeNull(); vm.SelectedScript!.Target!.Mode.Should().Be(MacroTargetMode.Screen);
        first.Target!.WindowTitle.Should().Be("Fixture document");
    }

    [Fact]
    public void ApplicationTargetWithoutChosenWindow_KeepsPreviousValidRule()
    {
        using var vm = CreateViewModel(); vm.NewMacroCommand.Execute(null);
        string? status = null; vm.StatusChanged += message => status = message;
        vm.TargetModeIndex = (int)MacroTargetMode.Application;
        vm.TargetModeIndex.Should().Be((int)MacroTargetMode.Screen);
        status.Should().Contain("choose the target application first"); vm.HasMacroMetadataChanges.Should().BeFalse();
    }
}
