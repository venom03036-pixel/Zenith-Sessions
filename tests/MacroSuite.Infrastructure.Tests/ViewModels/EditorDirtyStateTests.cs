using FluentAssertions;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Steps;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    [Fact]
    public void CleanAction_PresentationRefreshAndCancelledPickers_DoNotCreateDraftOrUndo()
    {
        using var vm = CreateViewModel();
        vm.NewMacroCommand.Execute(null);
        vm.Steps.ReplaceAll([new KeyPressStep(77)]);
        vm.SelectedStep = vm.Steps[0];
        vm.ActionEditor.RefreshRouteTargets();
        vm.ActionEditor.IsCapturingKey = true;
        vm.ActionEditor.IsCapturingKey = false;
        vm.ActionEditor.IsPickingLocation = true;
        vm.ActionEditor.PickHint = "Move the pointer";
        vm.ActionEditor.IsPickingLocation = false;
        vm.ActionEditor.PickHint = string.Empty;
        vm.ActionEditor.HasDraftChanges.Should().BeFalse();
        vm.SaveStateText.Should().NotBe("Unapplied changes");
        vm.ActionEditor.TryCommit().Should().BeTrue();
        vm.UndoEditCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void ActionDraft_RestoringOriginalValueClearsDirtyWithoutApplying()
    {
        using var vm = CreateViewModel();
        vm.NewMacroCommand.Execute(null);
        vm.Steps.ReplaceAll([new DelayStep(TimeSpan.FromMilliseconds(120))]);
        vm.SelectedStep = vm.Steps[0];
        vm.ActionEditor.DurationMs = 840;
        vm.ActionEditor.HasDraftChanges.Should().BeTrue();
        vm.SaveStateText.Should().Be("Unapplied changes");
        vm.ActionEditor.DurationMs = 120;
        vm.ActionEditor.HasDraftChanges.Should().BeFalse();
        vm.SaveStateText.Should().NotBe("Unapplied changes");
        vm.ActionEditor.TryCommit().Should().BeTrue();
        vm.UndoEditCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void RevertNestedDraft_RestoresChildAndPreservesUnrelatedMacroMetadataDraft()
    {
        using var vm = CreateViewModel();
        vm.NewMacroCommand.Execute(null);
        vm.Steps.ReplaceAll([new LoopStep(2, [new DelayStep(TimeSpan.FromMilliseconds(120))])]);
        vm.SelectedStep = (MacroStepItemViewModel)vm.OutlineRows[1]!;
        string originalName = vm.MacroName;
        vm.MacroName = "A real metadata edit";
        vm.ActionEditor.DurationMs = 840;
        vm.ActionEditor.CancelEdits();
        vm.ActionEditor.HasDraftChanges.Should().BeFalse();
        vm.ActionEditor.DurationMs.Should().Be(120);
        ((DelayStep)((LoopStep)vm.Steps[0].Step).BodySteps[0]).Duration.Should().Be(TimeSpan.FromMilliseconds(120));
        vm.HasMacroMetadataChanges.Should().BeTrue();
        vm.SaveStateText.Should().Be("Unsaved macro changes");
        vm.MacroName = originalName;
        vm.HasMacroMetadataChanges.Should().BeFalse();
        vm.SaveStateText.Should().NotBe("Unsaved macro changes");
        vm.UndoEditCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void ApplyAction_UpdatesBaselineAndKeepsTheExistingUndoOperation()
    {
        using var vm = CreateViewModel();
        vm.NewMacroCommand.Execute(null);
        vm.Steps.ReplaceAll([new DelayStep(TimeSpan.FromMilliseconds(120))]);
        vm.SelectedStep = vm.Steps[0];
        vm.ActionEditor.DurationMs = 840;
        vm.ActionEditor.Apply().Should().BeTrue();
        vm.ActionEditor.HasDraftChanges.Should().BeFalse();
        vm.ActionEditor.RefreshRouteTargets();
        vm.ActionEditor.HasDraftChanges.Should().BeFalse();
        ((DelayStep)vm.Steps[0].Step).Duration.Should().Be(TimeSpan.FromMilliseconds(840));
        vm.UndoEditCommand.Execute(null);
        ((DelayStep)vm.Steps[0].Step).Duration.Should().Be(TimeSpan.FromMilliseconds(120));
        vm.ActionEditor.HasDraftChanges.Should().BeFalse();
    }

    [Fact]
    public void SwitchingCleanScripts_DoesNotShowAnUnappliedDraft()
    {
        using var vm = CreateViewModel();
        var first = vm.SelectedScript!;
        vm.SelectedScript = vm.MacroScripts.First(script => script != first);
        vm.SelectedScript = first;
        vm.ActionEditor.HasDraftChanges.Should().BeFalse();
        vm.HasMacroMetadataChanges.Should().BeFalse();
        vm.SaveStateText.Should().NotBe("Unapplied changes");
    }

    [Fact]
    public void InvalidActionDraft_BlocksSwitchingUntilReverted()
    {
        using var vm = CreateViewModel();
        vm.NewMacroCommand.Execute(null);
        vm.Steps.ReplaceAll([new AIStep("Find the button")]);
        vm.SelectedStep = vm.Steps[0];
        var selected = vm.SelectedScript!;
        var other = vm.MacroScripts.First(script => script != selected);
        vm.ActionEditor.MinConfidence = "invalid";
        vm.ActionEditor.HasDraftChanges.Should().BeTrue();
        vm.ActionEditor.TryCommit().Should().BeFalse();
        vm.SelectedScript = other;
        vm.SelectedScript.Should().BeSameAs(selected);
        vm.ActionEditor.CancelEdits();
        vm.ActionEditor.HasDraftChanges.Should().BeFalse();
        vm.SelectedScript = other;
        vm.SelectedScript.Should().BeSameAs(other);
    }

    [Fact]
    public void UnrelatedScriptSave_DoesNotClearSelectedActionOrMetadataDraft()
    {
        string folder = Path.Combine(Path.GetTempPath(), "zenith-dirty-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var vm = CreatePersistentEditor(folder);
            var other = vm.SelectedScript!;
            vm.NewMacroCommand.Execute(null);
            vm.AddDelayCommand.Execute(null);
            vm.SelectedStep = vm.Steps[0];
            vm.MacroName = "Unsaved name";
            vm.ActionEditor.DurationMs += 123;
            vm.SaveMacroScript(other);
            vm.ActionEditor.HasDraftChanges.Should().BeTrue();
            vm.HasMacroMetadataChanges.Should().BeTrue();
            vm.SaveStateText.Should().Be("Unapplied changes");
            vm.ActionEditor.CancelEdits();
            vm.SaveStateText.Should().Be("Unsaved macro changes");
            vm.SaveMacroCommand.Execute(null);
            vm.SaveStateText.Should().Be("Saved");
            vm.HasMacroMetadataChanges.Should().BeFalse();
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void SaveFailure_SurvivesRouteRefreshRevertAndUnrelatedScriptSave_UntilSuccessfulRetry()
    {
        string folder = Path.Combine(Path.GetTempPath(), "zenith-dirty-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var vm = CreatePersistentEditor(folder);
            var other = vm.SelectedScript!;
            vm.NewMacroCommand.Execute(null);
            vm.AddDelayCommand.Execute(null);
            vm.SelectedStep = vm.Steps[0];
            string path = vm.SelectedScript!.FilePath!;
            vm.MacroName = "Not yet saved";
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                vm.SaveMacroCommand.Execute(null);
                vm.ActionEditor.RefreshRouteTargets();
                vm.ActionEditor.DurationMs += 123;
                vm.ActionEditor.CancelEdits();
                vm.SaveMacroScript(other);
                vm.SaveStateText.Should().Be("Save failed");
                vm.ActionEditor.HasDraftChanges.Should().BeFalse();
                vm.HasMacroMetadataChanges.Should().BeTrue();
            }
            vm.SaveMacroCommand.Execute(null);
            vm.SaveStateText.Should().Be("Saved");
            vm.HasMacroMetadataChanges.Should().BeFalse();
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Deletion_ExposesContextualUndoNoticeAndRestoresSameScript()
    {
        using var vm = CreateViewModel();
        var deleted = vm.SelectedScript!;
        string? status = null;
        vm.StatusChanged += message => status = message;
        vm.DeleteScript(deleted);
        vm.HasDeletedMacro.Should().BeTrue();
        vm.DeletedMacroNotice.Should().Contain(deleted.Name);
        status.Should().Be("Ready");
        vm.UndoDeleteMacroCommand.Execute(null);
        vm.HasDeletedMacro.Should().BeFalse();
        vm.DeletedMacroNotice.Should().BeEmpty();
        vm.SelectedScript.Should().BeSameAs(deleted);
    }

    [Fact]
    public void SavePendingMetadata_ReportsSavingThenSaved_AndClearsOnlyCommittedDraft()
    {
        string folder = Path.Combine(Path.GetTempPath(), "zenith-saving-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var vm = CreatePersistentEditor(folder);
            vm.MacroName = "Name ready to save";
            vm.SaveStateText.Should().Be("Unsaved macro changes");
            var transitions = new List<string>();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MacroEditorViewModel.SaveStateText)) transitions.Add(vm.SaveStateText);
            };
            vm.SaveMacroCommand.Execute(null);
            transitions.Should().ContainInOrder("Saving…", "Saved");
            vm.HasMacroMetadataChanges.Should().BeFalse();
            vm.ActionEditor.HasDraftChanges.Should().BeFalse();
            vm.SaveStateText.Should().Be("Saved");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
