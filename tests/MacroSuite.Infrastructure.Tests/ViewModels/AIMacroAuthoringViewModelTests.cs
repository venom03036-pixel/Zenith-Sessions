using System.Drawing;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.AI;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public class AIMacroAuthoringViewModelTests
{
    [Fact]
    public async Task GenerateDoesNotSaveAndRequiresReviewBeforeExplicitSave()
    {
        var service = new FakeService(); int saves = 0;
        var vm = new AIMacroAuthoringViewModel(service, (macro, ct) => { saves++; return Task.CompletedTask; });
        vm.Prompt = "Press M";
        await vm.GenerateAsync();
        Assert.True(vm.HasDraft); Assert.False(vm.CanSave); Assert.Equal(0, saves);
        await vm.SaveAsync(); Assert.Equal(0, saves);
        vm.Reviewed = true; Assert.True(vm.CanSave);
        await vm.SaveAsync(); Assert.Equal(1, saves); Assert.NotNull(vm.SavedMacro);
        await vm.SaveAsync(); Assert.Equal(1, saves);
        await vm.CloseAsync();
    }

    [Fact]
    public async Task ChangedInstructionsInvalidateReviewAndPreviousDraft()
    {
        var vm = new AIMacroAuthoringViewModel(new FakeService(), (_, _) => Task.CompletedTask) { Prompt = "Press M" };
        await vm.GenerateAsync(); vm.Reviewed = true;
        vm.Prompt = "Press L";
        Assert.False(vm.HasDraft); Assert.False(vm.CanSave); Assert.Empty(vm.Rows);
        await vm.CloseAsync();
    }

    [Fact]
    public async Task FailedSaveRemainsReviewableAndCanRetry()
    {
        int attempts = 0;
        var vm = new AIMacroAuthoringViewModel(new FakeService(), (_, _) => ++attempts == 1 ? Task.FromException(new IOException("Disk unavailable")) : Task.CompletedTask) { Prompt = "Press M" };
        await vm.GenerateAsync(); vm.Reviewed = true;
        await vm.SaveAsync(); Assert.Null(vm.SavedMacro); Assert.True(vm.CanSave); Assert.Contains("Disk unavailable", vm.Status);
        await vm.SaveAsync(); Assert.NotNull(vm.SavedMacro);
        await vm.CloseAsync();
    }

    [Fact]
    public async Task ClosingCancelsAndAwaitsGenerationWithoutSaving()
    {
        var service = new FakeService { WaitForCancellation = true }; int saves = 0;
        var vm = new AIMacroAuthoringViewModel(service, (_, _) => { saves++; return Task.CompletedTask; }) { Prompt = "Press M" };
        Task generating = vm.GenerateAsync();
        Assert.True(vm.IsBusy);
        await vm.CloseAsync(); await generating;
        Assert.False(vm.IsBusy); Assert.False(vm.HasDraft); Assert.Equal(0, saves); Assert.True(service.Cancelled);
    }

    private sealed class FakeService : IAIMacroAuthoringService
    {
        public string ProviderSummary => "Fake";
        public bool WaitForCancellation { get; init; }
        public bool Cancelled { get; private set; }
        public Task<AIMacroAuthoringSnapshot> CaptureAsync(Rectangle area, CancellationToken ct) => throw new NotSupportedException();
        public async Task<AIMacroDraft> GenerateAsync(string prompt, AIMacroAuthoringSnapshot? snapshot, CancellationToken ct)
        {
            if (WaitForCancellation)
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            return new AIMacroDraft(new MacroModel { Name = "Generated", Steps = [new KeyPressStep(77)] }, [new AIMacroDraftRow(1, 0, "Press M", "")], "fake", "test", 0.001m);
        }
    }
}
