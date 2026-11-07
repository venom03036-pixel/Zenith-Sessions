using System.Diagnostics;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    [Fact]
    public Task MiniRecording_WithoutSelection_CreatesCleanDestinationAndRestoresOnce() => WpfTestApplication.RunAsync(async () =>
    {
        var recorder = new TestInputRecorder();
        using var vm = CreateRecordingViewModel(recorder);
        vm.AddGeneratedMacro(new MacroModel { Name = "Previous macro", Steps =
            [new KeyPressStep(66) { RelativeTimestamp = TimeSpan.FromSeconds(45) }] }).Should().BeTrue();
        var old = vm.SelectedScript!;
        vm.Description = "Previous macro notes";
        vm.RepeatCount = 17;
        vm.SelectedScript = null;
        vm.IsMiniToolbarVisible = true;
        int staleFields = 0, restored = 0;
        vm.CommitVisibleRecordingFields = () => { staleFields++; return false; };
        vm.LeaveCompactOverlay = () => restored++;
        vm.RecordCommand.CanExecute(null).Should().BeTrue();
        await vm.ToggleRecordingAsync();
        vm.IsRecording.Should().BeTrue();
        var destination = vm.SelectedScript!;
        destination.Should().NotBeSameAs(old);
        destination.Target!.Mode.Should().Be(MacroTargetMode.Screen);
        vm.Description.Should().NotBe("Previous macro notes");
        vm.RepeatCount.Should().Be(1);
        recorder.Writer!.TryWrite(new(InputEventType.KeyDown, Stopwatch.GetTimestamp(), 0, 0, 65));
        recorder.Writer.TryWrite(new(InputEventType.KeyUp, Stopwatch.GetTimestamp(), 0, 0, 65));
        await vm.StopRecordingAsync();
        vm.SelectedScript.Should().BeSameAs(destination);
        vm.Steps.Should().HaveCount(2);
        vm.Steps.Select(s => s.Step).OfType<KeyPressStep>().Max(s => s.RelativeTimestamp).Should().BeLessThan(TimeSpan.FromSeconds(5));
        old.Actions.Should().HaveCount(1);
        staleFields.Should().Be(0); restored.Should().Be(1);
        vm.IsRecordingBusy.Should().BeFalse();
    });
}
