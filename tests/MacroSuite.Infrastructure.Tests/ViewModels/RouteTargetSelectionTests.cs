using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using FluentAssertions;
using MacroSuite.App.ViewModels;
using MacroSuite.App.Views;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

/// <summary>
/// The jump-destination lists of the action editor (QUALITY_STATUS F-4). Each list is O(actions),
/// so it is built once per change of its inputs, and the destination ComboBoxes select the saved
/// destination by item instead of searching every target through a per-item SelectedValuePath binding.
/// </summary>
public partial class MacroEditorViewModelTests
{
    private const string UnavailableDestinationPrefix = "Unavailable destination";

    [Fact]
    public void RouteTargets_ReuseOneListUntilStepsOrSelectionChange_AndRebuildPicksUpEveryChange()
    {
        using var vm = CreateViewModel();
        vm.NewMacroCommand.Execute(null);
        for (int i = 0; i < 4; i++) vm.AddDelayCommand.Execute(null);
        Guid kept = vm.GetStepAt(1)!.StepId, removed = vm.GetStepAt(2)!.StepId, disabled = vm.GetStepAt(3)!.StepId;
        var block = new IfThenElseStep(new VisualPresenceCondition(), [])
        { Routing = new BlockRoutingOptions { OnNotSatisfied = FlowRoute.Jump, TargetStepId = removed } };
        vm.ReplaceStepAt(0, block, reloadPanel: true);
        var editor = vm.ActionEditor;

        var first = editor.RouteTargets;
        editor.RouteTargets.Should().BeSameAs(first, "reading the destinations again must not rebuild an O(actions) list");
        first.Select(t => t.StepId).Should().Equal(block.StepId, kept, removed, disabled);
        editor.SelectedRouteTarget.Should().BeSameAs(first[2], "the selected destination is the list's own item");

        var raised = new List<string?>();
        editor.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var added = new DelayStep(TimeSpan.Zero);
        vm.Steps.RemoveAt(2);
        vm.Steps.ReplaceAt(2, new DelayStep(TimeSpan.Zero) { StepId = disabled, IsEnabled = false });
        vm.Steps.AddStep(added);
        editor.RouteTargets.Should().BeSameAs(first, "the list is rebuilt when the editor reports the steps change, not on every read");

        vm.RefreshStepOrder(persist: false);
        var rebuilt = editor.RouteTargets;
        rebuilt.Should().NotBeSameAs(first);
        editor.RouteTargets.Should().BeSameAs(rebuilt);
        rebuilt.Select(t => t.StepId).Should().Equal(block.StepId, kept, added.StepId, removed);
        rebuilt[^1].DisplayName.Should().StartWith(UnavailableDestinationPrefix, "the saved destination was removed");
        rebuilt.Should().NotContain(t => t.StepId == disabled, "a disabled action is not a destination");
        editor.SelectedRouteTarget.Should().BeSameAs(rebuilt[^1]);
        editor.RouteTargetStepId.Should().Be(removed, "rebuilding the list never changes the saved destination");
        raised.Should().Contain(new[] { nameof(ActionPropertiesViewModel.RouteTargets), nameof(ActionPropertiesViewModel.SelectedRouteTarget) });

        Guid unknown = Guid.NewGuid();
        raised.Clear();
        editor.RouteTargetStepId = unknown;
        editor.RouteTargets.Should().Contain(t => t.StepId == unknown && t.DisplayName.StartsWith(UnavailableDestinationPrefix));
        editor.SelectedRouteTarget!.StepId.Should().Be(unknown);
        raised.Should().Contain(nameof(ActionPropertiesViewModel.RouteTargets));
        var withUnknown = editor.RouteTargets;
        editor.RouteTargetStepId = kept;
        editor.RouteTargets.Should().BeSameAs(withUnknown, "choosing a listed destination does not rebuild the list");
        editor.SelectedRouteTarget.Should().BeSameAs(withUnknown[1]);

        vm.SelectedStep = vm.Steps[1];
        var afterLoad = editor.RouteTargets;
        afterLoad.Should().NotBeSameAs(withUnknown, "loading another action rebuilds the list");
        afterLoad.Should().NotContain(t => t.DisplayName.StartsWith(UnavailableDestinationPrefix));
        afterLoad.Select(t => t.StepId).Should().Equal(block.StepId, kept, added.StepId);
        editor.RouteTargets.Should().BeSameAs(afterLoad);
    }

    [Fact]
    public void AppearanceTarget_MissingDestinationIsListedAndSelected_AndTheWatcherIsNotADestination()
    {
        using var vm = CreateViewModel();
        vm.NewMacroCommand.Execute(null);
        vm.AddDelayCommand.Execute(null);
        vm.AddDelayCommand.Execute(null);
        Guid missing = Guid.NewGuid();
        var watcher = new WaitUntilStep(new ImageCondition(RoutingTemplatePng()))
        { AppearanceRedirect = new AppearanceRedirectOptions { TargetStepId = missing } };
        vm.ReplaceStepAt(0, watcher, reloadPanel: true);
        var editor = vm.ActionEditor;

        editor.RedirectOnAppearance.Should().BeTrue();
        editor.AppearanceTargetStepId.Should().Be(missing);
        editor.RouteTargets.Should().NotContain(t => t.StepId == watcher.StepId);
        editor.SelectedAppearanceTarget.Should().NotBeNull("the saved destination stays selected while it is unavailable");
        editor.SelectedAppearanceTarget!.StepId.Should().Be(missing);
        editor.SelectedAppearanceTarget.DisplayName.Should().StartWith(UnavailableDestinationPrefix);
        editor.RouteTargets.Should().Contain(editor.SelectedAppearanceTarget);

        editor.SelectedAppearanceTarget = null;
        editor.AppearanceTargetStepId.Should().Be(missing, "a cleared ComboBox selection must not erase the saved destination");
        var choice = editor.RouteTargets[0];
        editor.SelectedAppearanceTarget = choice;
        editor.AppearanceTargetStepId.Should().Be(choice.StepId);
        editor.SelectedAppearanceTarget.Should().BeSameAs(choice);
    }

    [Fact]
    public Task DestinationComboBoxes_ProductionEditor_SelectByItem_AndListRebuildKeepsSavedDestinations()
        => WpfTestApplication.RunAsync(async () =>
        {
            using var vm = CreateViewModel();
            vm.NewMacroCommand.Execute(null);
            for (int i = 0; i < 3; i++) vm.AddDelayCommand.Execute(null);
            Guid routeTarget = vm.GetStepAt(1)!.StepId;
            Guid appearanceTarget = vm.GetStepAt(2)!.StepId;
            using var fixture = new NavigationLayoutFixture();
            var view = new MacroEditorView { DataContext = vm }; fixture.Attach(view);
            await fixture.SettleAsync();
            try
            {
                var combos = NavigationVisuals<ComboBox>(view)
                    .Where(c => BindingOperations.GetBinding(c, ItemsControl.ItemsSourceProperty)?.Path.Path == "ActionEditor.RouteTargets")
                    .ToList();
                combos.Should().HaveCount(2, "the editor has a jump destination list for image watchers and one for blocks");
                foreach (var combo in combos)
                {
                    combo.SelectedValuePath.Should().BeEmpty("SelectedValuePath evaluates a binding against every destination on each search");
                    BindingOperations.GetBinding(combo, Selector.SelectedValueProperty).Should().BeNull();
                }
                var selectedItemPaths = combos.Select(c => BindingOperations.GetBinding(c, Selector.SelectedItemProperty)?.Path.Path).ToList();
                selectedItemPaths.Should().BeEquivalentTo(new[] { "ActionEditor.SelectedAppearanceTarget", "ActionEditor.SelectedRouteTarget" });
                var appearanceCombo = combos[selectedItemPaths.IndexOf("ActionEditor.SelectedAppearanceTarget")];
                var routeCombo = combos[selectedItemPaths.IndexOf("ActionEditor.SelectedRouteTarget")];

                // Block route. Moving the block renumbers its destination ("2." becomes "1."), so the
                // rebuilt list no longer holds the selected item and the ComboBox drops it.
                var block = new IfThenElseStep(new VisualPresenceCondition(), [])
                { Routing = new BlockRoutingOptions { OnNotSatisfied = FlowRoute.Jump, TargetStepId = routeTarget } };
                vm.ReplaceStepAt(0, block, reloadPanel: true);
                await fixture.SettleAsync();
                ((ActionRouteTarget)routeCombo.SelectedItem).StepId.Should().Be(routeTarget);
                ShouldShowDestination(routeCombo, vm.ActionEditor, vm.ActionEditor.SelectedRouteTarget);
                int cleared = 0;
                routeCombo.SelectionChanged += (_, e) => { if (e.AddedItems.Count == 0 && e.RemovedItems.Count > 0) cleared++; };
                vm.MoveDownCommand.Execute(null);
                await fixture.SettleAsync();
                cleared.Should().BeGreaterThan(0, "the rebuilt list no longer holds the selected item, so the ComboBox drops it");
                vm.ActionEditor.RouteTargetStepId.Should().Be(routeTarget);
                ShouldShowDestination(routeCombo, vm.ActionEditor, vm.ActionEditor.SelectedRouteTarget);
                ((ActionRouteTarget)routeCombo.SelectedItem).DisplayName.Should().StartWith("1.");
                vm.ActionEditor.HasDraftChanges.Should().BeFalse("re-selecting the same destination is not an edit");
                ((IfThenElseStep)vm.GetStepAt(1)!).Routing.TargetStepId.Should().Be(routeTarget);

                // The same drop while the block stays loaded (steps changed elsewhere, no reload):
                // the ComboBox writes null back, which must not erase the saved destination.
                cleared = 0;
                vm.Steps.Insert(0, MacroStepItemViewModel.FromStep(new DelayStep(TimeSpan.Zero), 0));
                vm.RefreshStepOrder(persist: false);
                await fixture.SettleAsync();
                vm.SelectedStep!.Step.StepId.Should().Be(block.StepId);
                cleared.Should().BeGreaterThan(0, "renumbering drops the selected item again");
                vm.ActionEditor.RouteTargetStepId.Should().Be(routeTarget);
                ShouldShowDestination(routeCombo, vm.ActionEditor, vm.ActionEditor.SelectedRouteTarget);
                ((ActionRouteTarget)routeCombo.SelectedItem).DisplayName.Should().StartWith("2.");
                vm.ActionEditor.HasDraftChanges.Should().BeFalse();

                // Image watcher (steps: new delay, route target, watcher, appearance target).
                vm.ReplaceStepAt(2, new WaitUntilStep(new ImageCondition(RoutingTemplatePng()))
                { AppearanceRedirect = new AppearanceRedirectOptions { TargetStepId = appearanceTarget } }, reloadPanel: true);
                await fixture.SettleAsync();
                ((ActionRouteTarget)appearanceCombo.SelectedItem).StepId.Should().Be(appearanceTarget);
                ShouldShowDestination(appearanceCombo, vm.ActionEditor, vm.ActionEditor.SelectedAppearanceTarget);
                vm.MoveDownCommand.Execute(null);
                await fixture.SettleAsync();
                vm.ActionEditor.AppearanceTargetStepId.Should().Be(appearanceTarget);
                ShouldShowDestination(appearanceCombo, vm.ActionEditor, vm.ActionEditor.SelectedAppearanceTarget);
                ((ActionRouteTarget)appearanceCombo.SelectedItem).DisplayName.Should().StartWith("3.");
                vm.ActionEditor.HasDraftChanges.Should().BeFalse();
                ((WaitUntilStep)vm.GetStepAt(3)!).AppearanceRedirect!.TargetStepId.Should().Be(appearanceTarget);

                // Choosing a destination in the ComboBox stores its id.
                var other = vm.ActionEditor.RouteTargets.First(t => t.StepId != appearanceTarget);
                appearanceCombo.SelectedItem = other;
                vm.ActionEditor.AppearanceTargetStepId.Should().Be(other.StepId);
            }
            finally { view.DataContext = null; }
        });

    private static void ShouldShowDestination(ComboBox combo, ActionPropertiesViewModel editor, ActionRouteTarget? expected)
    {
        expected.Should().NotBeNull();
        combo.ItemsSource.Should().BeSameAs(editor.RouteTargets, "the ComboBox shows the current destination list");
        // A rebuilt list holds equal records, so the ComboBox may keep an equal earlier instance.
        combo.SelectedItem.Should().Be(expected);
        combo.SelectedIndex.Should().Be(editor.RouteTargets.ToList().IndexOf(expected!));
    }
}
