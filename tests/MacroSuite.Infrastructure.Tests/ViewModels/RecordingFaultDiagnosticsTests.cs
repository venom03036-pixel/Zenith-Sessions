using System.Diagnostics;
using FluentAssertions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Storage;
using MacroSuite.Infrastructure.Storage;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    [Theory]
    [InlineData(480, 0)]
    [InlineData(547, 1100)]
    [InlineData(0, 0)]
    public async Task RecordingFaultDiagnostics_RealProtectedJournal_NormalStopIsSavedAndNotFaulted(int movementCount, int beforeStopDelayMs)
    {
        using var folder = new RoutingTempFolder();
        var documents = new ProtectedMacroStore(folder.Library, new RecordingDiagnosticKeys());
        var recorder = new TestInputRecorder();
        using var vm = CreateRecordingViewModel(recorder, directory: folder.Library, documents: documents);
        vm.SelectedScript!.Target = null; // The user's existing schema-2 macro retains legacy Screen targeting.
        var messages = new List<string>();
        vm.StatusChanged += messages.Add;
        await vm.StartRecordingAsync();
        long first = Stopwatch.GetTimestamp();
        for (int i = 0; i < movementCount; i++)
            recorder.Writer!.TryWrite(new(InputEventType.MouseMove, first + i * Stopwatch.Frequency / 100,
                100 + i * 3, 100 + i * 2, 0)).Should().BeTrue();
        if (beforeStopDelayMs > 0) await Task.Delay(beforeStopDelayMs);
        await vm.StopRecordingAsync().WaitAsync(TimeSpan.FromSeconds(10));
        vm.SaveStateText.Should().Be("Saved", string.Join(" | ", messages));
        vm.RecordingStateText.Should().Be("Recording stopped", string.Join(" | ", messages));
        vm.IsRecordingBusy.Should().BeFalse();
        var saved = documents.Load(documents.GetLibraryPath(vm.SelectedScript.Id)).Model;
        saved.Steps.Should().HaveCount(vm.Steps.Count);
        (await documents.RecordingRecovery.ReadPendingAsync()).Should().BeEmpty();
    }

    private sealed class RecordingDiagnosticKeys : ILocalMacroKeyStore
    {
        private readonly byte[] _key = Enumerable.Range(0, 32).Select(i => (byte)(i + 17)).ToArray();
        public byte[] GetKey(bool createIfMissing) => _key.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordingFaultDiagnostics_RealProtectedJournal_RepeatedAppendsKeepExistingLibraryFilename(bool legacyFilename)
    {
        using var folder = new RoutingTempFolder();
        var documents = new ProtectedMacroStore(folder.Library, new RecordingDiagnosticKeys());
        var recorder = new TestInputRecorder();
        using var vm = CreateRecordingViewModel(recorder, directory: folder.Library, documents: documents);
        vm.SelectedScript!.Target = null;
        var script = vm.SelectedScript;
        string canonical = documents.GetLibraryPath(script.Id);
        if (legacyFilename)
        {
            string previousPath = script.FilePath!;
            string retainedPath = Path.Combine(folder.Library, "legacy-recording-name.zmacro");
            File.Move(previousPath, retainedPath);
            script.FilePath = retainedPath;
        }
        var messages = new List<string>();
        vm.StatusChanged += messages.Add;
        for (int session = 0; session < 3; session++)
        {
            await vm.StartRecordingAsync();
            long stamp = Stopwatch.GetTimestamp();
            recorder.Writer!.TryWrite(new(InputEventType.KeyDown, stamp, 0, 0, 87)).Should().BeTrue();
            recorder.Writer.TryWrite(new(InputEventType.KeyUp, stamp + Stopwatch.Frequency / 100, 0, 0, 87)).Should().BeTrue();
            await vm.StopRecordingAsync().WaitAsync(TimeSpan.FromSeconds(10));
            vm.SaveStateText.Should().Be("Saved", string.Join(" | ", messages));
            vm.RecordingStateText.Should().Be("Recording stopped", string.Join(" | ", messages));
            vm.IsRecordingBusy.Should().BeFalse();
            documents.Load(script.FilePath!).Model.Steps.Should().HaveCount((session + 1) * 2);
            (await documents.RecordingRecovery.ReadPendingAsync()).Should().BeEmpty();
        }
        if (legacyFilename) File.Exists(canonical).Should().BeFalse("recording must preserve the original managed library filename");
    }

    [Fact]
    public async Task RecordingFaultDiagnostics_DuplicateDocumentIdentity_KeepsRealFaultAndRecovery()
    {
        using var folder = new RoutingTempFolder();
        var documents = new ProtectedMacroStore(folder.Library, new RecordingDiagnosticKeys());
        var recorder = new TestInputRecorder();
        using var vm = CreateRecordingViewModel(recorder, directory: folder.Library, documents: documents);
        string original = vm.SelectedScript!.FilePath!;
        string duplicate = Path.Combine(folder.Library, "duplicate-identity.zmacro");
        await vm.StartRecordingAsync();
        File.Copy(original, duplicate);
        byte[] duplicateBefore = File.ReadAllBytes(duplicate);
        recorder.Writer!.TryWrite(new(InputEventType.KeyDown, Stopwatch.GetTimestamp(), 0, 0, 87));
        await vm.StopRecordingAsync().WaitAsync(TimeSpan.FromSeconds(10));
        vm.SaveStateText.Should().Be("Saved");
        vm.RecordingStateText.Should().Be("Faulted");
        vm.Steps.Should().HaveCount(2);
        vm.ActivityLogs.Should().Contain(message => message.Contains("Multiple library documents"));
        File.ReadAllBytes(duplicate).Should().Equal(duplicateBefore);
        (await documents.RecordingRecovery.ReadPendingAsync()).Should().ContainSingle();
    }
}
