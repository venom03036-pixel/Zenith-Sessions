#if DEVELOPER_MODE
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using MacroSuite.Infrastructure.Storage;
using Xunit;
using Zenith.MacroHost.Contracts;
using Zenith.MacroHost.Execution;
using Zenith.Sessions.Core;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    private static async Task<SavedMacroReference> SavedReference(MacroEditorViewModel vm, Guid? id = null)
    {
        var page = await CompanionPage(vm);
        page.State.Should().Be(MacroCatalogState.Ready);
        var item = id is { } expected ? page.Items.Single(row => row.Id == expected) : page.Items.Single();
        return new(page.HostSessionId, item.Id, item.SavedRevision);
    }

    private static MacroModel ReceiptMacro(string name = "Owned saved document") => new()
    {
        Name = name, Description = "private fixture document body",
        Settings = new() { Target = null }, Steps = [new DelayStep(TimeSpan.Zero)]
    };

    private static MacroModel ReceiptWithName(MacroModel model, string name) => new()
    {
        Id = model.Id, SchemaVersion = model.SchemaVersion, Name = name, Description = model.Description,
        CreatedAt = model.CreatedAt, IsFavorite = model.IsFavorite, RecordingCommit = model.RecordingCommit,
        Settings = model.Settings, Steps = model.Steps
    };

    private static void ReloadReceiptLibrary(MacroEditorViewModel vm)
        => typeof(MacroEditorViewModel).GetMethod("InitializeScriptsLibrary", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [false]);

    [Fact]
    public Task CompanionDocument_RealSavedRevisionRunsRealInterpreterWithoutReadingVisibleDraftActions() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder();
        var saved = new MacroModel { Name = "Saved F24", Settings = new() { Target = null },
            Steps = [new KeyPressStep { KeyCode = 0x87, Stroke = "Press", DelayAfterMs = 0 }] };
        using var documents = new ReceiptDocuments(folder.Library, saved);
        using var vm = CreateCompanionEditor(folder.Library, documents);
        var reference = await SavedReference(vm);
        int writesBeforeDraft = documents.Saves;
        var draft = new KeyPressStep { KeyCode = 0x86, Stroke = "Press", DelayAfterMs = 0 };
        vm.SelectedScript!.Actions[0] = draft;
        vm.Steps.ReplaceAt(0, draft);
        vm.Steps[0].Step.Should().BeOfType<KeyPressStep>().Which.KeyCode.Should().Be(0x86);
        documents.Saves.Should().Be(writesBeforeDraft);
        await using var preparer = new SavedMacroPlanPreparer(vm.CompanionDocuments, static _ => { });
        var target = new TargetedInputTarget(Guid.NewGuid(), 71, new DateTime(2026, 1, 1).Ticks,
            (nint)171, new PixelRectangle(0, 0, 100, 100), 1);
        var transport = new ReceiptMemoryTransport(target);
        await using var input = new TargetedInputCoordinator(transport);
        await using var runner = new TargetedMacroRunner(input, new([0x87], TargetedMacroSignals.None),
            (candidate, scope) => candidate == target && scope == TargetedMacroScope.OwnedWindowDiagnostic);
        var operations = new CompanionMacroOperations(vm.InputOperations, preparer, runner);
        var outcome = await operations.RunAsync(reference, [target], vm.InputOperations.StopGeneration).WaitAsync(TimeSpan.FromSeconds(5));
        outcome.Status.Should().Be(CompanionMacroOperationStatus.Finished);
        outcome.Preparation.Should().Be(SavedMacroPreparationStatus.Ready);
        outcome.Recipients.Single().StartStatus.Should().Be(TargetedMacroStartStatus.Started);
        var completed = outcome.Recipients.Single().Run!;
        completed.Status.Should().Be(TargetedMacroResultStatus.Completed);
        vm.InputOperations.Snapshot.IsBusy.Should().BeFalse();
        transport.Commands.Select(command => command.Signal.Key).Should().OnlyContain(key => key == 0x87);
        transport.Commands.Select(command => command.Signal.Kind).Should().Equal(TargetedInputKind.KeyDown, TargetedInputKind.KeyUp);
        completed.Snapshot.AccountVerified.Should().BeFalse(); completed.Snapshot.RobloxQualified.Should().BeFalse();
        documents.Inner.Save(ReceiptWithName(saved, "Externally replaced saved bytes"), documents.GetLibraryPath(saved.Id));
        (await vm.CompanionDocuments.ReadAsync(reference)).Status.Should().Be(SavedMacroDocumentReadStatus.Changed);
        (await preparer.PrepareAsync(reference, TargetedMacroScope.OwnedWindowDiagnostic)).Status.Should().Be(SavedMacroPreparationStatus.Changed);
    });

    [Fact]
    public Task CompanionDocument_ExactLoadedRevisionPreparesWithoutPublishingBodyOrReadingForCatalog() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder();
        using var documents = new ReceiptDocuments(folder.Library, ReceiptMacro());
        using var vm = CreateCompanionEditor(folder.Library, documents);
        var reference = await SavedReference(vm);
        int reads = documents.Reads;
        await CompanionPage(vm);
        documents.Reads.Should().Be(reads);
        var read = await vm.CompanionDocuments.ReadAsync(reference);
        read.Status.Should().Be(SavedMacroDocumentReadStatus.Ready);
        read.Pin!.Reference.Should().Be(reference); read.Pin.IsCurrent().Should().BeTrue();
        await using var preparer = new SavedMacroPlanPreparer(vm.CompanionDocuments, static _ => { });
        var prepared = await preparer.PrepareAsync(reference, TargetedMacroScope.OwnedWindowDiagnostic);
        prepared.Status.Should().Be(SavedMacroPreparationStatus.Ready);
        prepared.Prepared!.Plan.StepCount.Should().Be(1);
        string publicText = JsonSerializer.Serialize(read) + read + read.Pin + prepared;
        publicText.Should().NotContain(folder.Root).And.NotContain("private fixture").And.NotContain("Ciphertext");
        read.Pin.GetType().GetProperties().Select(property => property.Name).Should().Equal("Reference");
    });

    [Fact]
    public Task CompanionDocument_SavePublishesActualPreparedMetadataAndOwnedReceipt() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); using var documents = new ReceiptDocuments(folder.Library, ReceiptMacro());
        using var vm = CreateCompanionEditor(folder.Library, documents);
        var before = await SavedReference(vm);
        documents.PreparedName = "Actual prepared document";
        vm.MacroName = "Editor request"; vm.SaveMacroCommand.Execute(null);
        var page = await CompanionPage(vm);
        page.Items.Single().Name.Should().Be("Actual prepared document");
        var after = await SavedReference(vm);
        after.SavedRevision.Should().NotBe(before.SavedRevision);
        (await vm.CompanionDocuments.ReadAsync(after)).Status.Should().Be(SavedMacroDocumentReadStatus.Ready);
        (await vm.CompanionDocuments.ReadAsync(before)).Status.Should().Be(SavedMacroDocumentReadStatus.Changed);
    });

    [Fact]
    public Task CompanionDocument_ReceiptlessStoreKeepsOrdinarySaveAndCatalogButCannotResolve() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); using var documents = new CompanionDocuments(folder.Library, ReceiptMacro());
        using var vm = CreateCompanionEditor(folder.Library, documents);
        vm.MacroName = "Ordinary saved name"; vm.SaveMacroCommand.Execute(null);
        var reference = await SavedReference(vm);
        int reads = documents.Reads;
        (await vm.CompanionDocuments.ReadAsync(reference)).Status.Should().Be(SavedMacroDocumentReadStatus.Unavailable);
        documents.Reads.Should().Be(reads);
        (await CompanionPage(vm)).Items.Single().Name.Should().Be("Ordinary saved name");
    });

    [Theory]
    [InlineData("replace")]
    [InlineData("missing")]
    [InlineData("corrupt")]
    public Task CompanionDocument_CurrentDiskBodyIsRequiredEvenWhenDescriptorIsUnchanged(string change) => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var model = ReceiptMacro();
        using var documents = new ReceiptDocuments(folder.Library, model); using var vm = CreateCompanionEditor(folder.Library, documents);
        var reference = await SavedReference(vm);
        string path = documents.GetLibraryPath(model.Id);
        if (change == "replace") documents.Inner.Save(ReceiptWithName(model, "Replaced bytes"), path);
        else if (change == "missing") File.Delete(path);
        else File.WriteAllText(path, "private corrupt fixture body");
        var result = await vm.CompanionDocuments.ReadAsync(reference);
        result.Status.Should().Be(change == "replace" ? SavedMacroDocumentReadStatus.Changed : SavedMacroDocumentReadStatus.Unavailable);
        result.Pin.Should().BeNull();
        (await SavedReference(vm)).Should().Be(reference);
        result.ToString().Should().NotContain("private").And.NotContain(path);
    });

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    public Task CompanionDocument_UnreadableBodyReportsOnlyItsErrorTypeToThePreparer(string change) => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var model = ReceiptMacro();
        using var documents = new ReceiptDocuments(folder.Library, model); using var vm = CreateCompanionEditor(folder.Library, documents);
        var reference = await SavedReference(vm);
        string path = documents.GetLibraryPath(model.Id);
        if (change == "missing") File.Delete(path);
        else File.WriteAllText(path, "private corrupt fixture body");
        var expected = Record.Exception(() => documents.Inner.Load(path));
        expected.Should().NotBeNull();
        var faults = new List<Type>();
        await using var preparer = new SavedMacroPlanPreparer(vm.CompanionDocuments, faults.Add);
        var result = await preparer.PrepareAsync(reference, TargetedMacroScope.OwnedWindowDiagnostic);
        result.Status.Should().Be(SavedMacroPreparationStatus.Unavailable);
        faults.Should().Equal(expected!.GetType());
        string reported = string.Join(",", faults.Select(type => type.FullName)) + result;
        reported.Should().NotContain("private").And.NotContain(path).And.NotContain(folder.Root);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CompanionDocument_FailedSaveRetainsMetadataButNeverAssumesOldCiphertextSurvived(bool committed) => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); using var documents = new ReceiptDocuments(folder.Library, ReceiptMacro());
        using var vm = CreateCompanionEditor(folder.Library, documents);
        var reference = await SavedReference(vm);
        var pin = (await vm.CompanionDocuments.ReadAsync(reference)).Pin!;
        if (committed) documents.FailAfterSave = documents.Saves + 1;
        else documents.FailBeforeSave = documents.Saves + 1;
        vm.MacroName = "Attempted changed name"; vm.SaveMacroCommand.Execute(null);
        vm.SaveStateText.Should().Be("Save failed");
        (await SavedReference(vm)).Should().Be(reference);
        pin.IsCurrent().Should().BeFalse();
        (await vm.CompanionDocuments.ReadAsync(reference)).Status.Should().Be(committed
            ? SavedMacroDocumentReadStatus.Changed : SavedMacroDocumentReadStatus.Ready);
    });

    [Theory]
    [InlineData("save")]
    [InlineData("remove")]
    [InlineData("reset")]
    [InlineData("reload")]
    public Task CompanionDocument_ReadInFlightCannotOutliveSaveMembershipOrLibraryEpoch(string mutation) => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); using var documents = new ReceiptDocuments(folder.Library, ReceiptMacro());
        using var vm = CreateCompanionEditor(folder.Library, documents);
        var reference = await SavedReference(vm); documents.BlockNextRead();
        Task<SavedMacroDocumentReadResult> read = vm.CompanionDocuments.ReadAsync(reference);
        try
        {
            await documents.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (mutation == "save") { vm.MacroName = "New saved body"; vm.SaveMacroCommand.Execute(null); }
            else if (mutation == "remove") vm.MacroScripts.Remove(vm.SelectedScript!);
            else if (mutation == "reset") vm.MacroScripts.Clear();
            else ReloadReceiptLibrary(vm);
        }
        finally { documents.ReadRelease.Set(); await read.WaitAsync(TimeSpan.FromSeconds(5)); }
        (await read.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(SavedMacroDocumentReadStatus.Changed);
    });

    [Fact]
    public Task CompanionDocument_CancelledUncooperativeReadKeepsItsSlotUntilActualReturn() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); using var documents = new ReceiptDocuments(folder.Library, ReceiptMacro());
        using var vm = CreateCompanionEditor(folder.Library, documents);
        var reference = await SavedReference(vm); documents.BlockNextRead(); using var stop = new CancellationTokenSource();
        var read = vm.CompanionDocuments.ReadAsync(reference, stop.Token);
        try
        {
            await documents.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
            read.IsCompleted.Should().BeFalse();
            (await vm.CompanionDocuments.ReadAsync(reference)).Status.Should().Be(SavedMacroDocumentReadStatus.Busy);
        }
        finally { documents.ReadRelease.Set(); await read.WaitAsync(TimeSpan.FromSeconds(5)); }
        (await read.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(SavedMacroDocumentReadStatus.Cancelled);
        (await vm.CompanionDocuments.ReadAsync(reference)).Status.Should().Be(SavedMacroDocumentReadStatus.Ready);
    });

    [Fact]
    public Task CompanionDocument_ShutdownClosesImmediatelyAndJoinsActualReadBeforeReturning() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); using var documents = new ReceiptDocuments(folder.Library, ReceiptMacro());
        using var vm = CreateCompanionEditor(folder.Library, documents);
        var reference = await SavedReference(vm); documents.BlockNextRead();
        var read = vm.CompanionDocuments.ReadAsync(reference);
        Task? shutdown = null;
        try
        {
            await documents.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); shutdown = vm.ShutdownAsync();
            shutdown.IsCompleted.Should().BeFalse();
            (await vm.CompanionDocuments.ReadAsync(reference)).Status.Should().Be(SavedMacroDocumentReadStatus.Closed);
        }
        finally { documents.ReadRelease.Set(); await read.WaitAsync(TimeSpan.FromSeconds(5)); }
        (await read.WaitAsync(TimeSpan.FromSeconds(5))).Pin.Should().BeNull();
        await shutdown!.WaitAsync(TimeSpan.FromSeconds(5));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CompanionDocument_DisposeFencesAndAsyncLifetimeJoinsTheBorrowedRead(bool useAsyncDispose) => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); using var documents = new ReceiptDocuments(folder.Library, ReceiptMacro());
        using var vm = CreateCompanionEditor(folder.Library, documents);
        var reference = await SavedReference(vm); documents.BlockNextRead();
        var read = vm.CompanionDocuments.ReadAsync(reference);
        Task? joined = null;
        try
        {
            await documents.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (useAsyncDispose) joined = vm.DisposeAsync().AsTask();
            else { vm.Dispose(); joined = vm.ShutdownAsync(); }
            joined.IsCompleted.Should().BeFalse();
            documents.Disposed.Should().BeFalse();
            var tracked = (Task?)typeof(MacroEditorViewModel).GetField("_companionReadTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm);
            tracked.Should().NotBeNull(); tracked!.IsCompleted.Should().BeFalse();
            (await vm.CompanionDocuments.ReadAsync(reference)).Status.Should().Be(SavedMacroDocumentReadStatus.Closed);
        }
        finally { documents.ReadRelease.Set(); await read.WaitAsync(TimeSpan.FromSeconds(5)); }
        (await read.WaitAsync(TimeSpan.FromSeconds(5))).Pin.Should().BeNull();
        await joined!.WaitAsync(TimeSpan.FromSeconds(5));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CompanionDocument_ActualOlderWriterRemainsBusyAfterNewerAttemptCompletes(bool newerSucceeded) => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); using var documents = new ReceiptDocuments(folder.Library, ReceiptMacro());
        var recorder = new TestInputRecorder(); using var vm = CreateCompanionEditor(folder.Library, documents, recorder);
        var reference = await SavedReference(vm);
        await vm.StartRecordingAsync();
        recorder.Writer!.TryWrite(new(InputEventType.KeyDown, Stopwatch.GetTimestamp(), 0, 0, 87));
        documents.BlockNextWrite(); var stop = vm.StopRecordingAsync();
        try
        {
            await documents.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            object?[] recoveryRead = [reference.MacroId, documents.GetLibraryPath(reference.MacroId), null];
            typeof(MacroEditorViewModel).GetMethod("ObserveCompanionRecovery", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, recoveryRead);
            recoveryRead[2].Should().BeNull("recovery cannot supersede an actual outstanding writer");
            if (!newerSucceeded) documents.FailBeforeSave = documents.Saves + 1;
            vm.SaveMacroScript(vm.SelectedScript!);
            (await vm.CompanionDocuments.ReadAsync(reference)).Status.Should().Be(SavedMacroDocumentReadStatus.Busy);
        }
        finally { documents.WriteRelease.Set(); await stop.WaitAsync(TimeSpan.FromSeconds(5)); }
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        var current = await SavedReference(vm);
        if (newerSucceeded) current.SavedRevision.Should().NotBe(reference.SavedRevision);
        else current.Should().Be(reference);
        (await vm.CompanionDocuments.ReadAsync(current)).Status.Should().Be(SavedMacroDocumentReadStatus.Changed);
        ReloadReceiptLibrary(vm);
        (await vm.CompanionDocuments.ReadAsync(await SavedReference(vm))).Status.Should().Be(SavedMacroDocumentReadStatus.Ready);
    });

    [Fact]
    public Task CompanionDocument_RecoveryRefreshDoesNotSupersedeAnAlreadyPendingPublisher() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var model = ReceiptMacro();
        using var documents = new ReceiptDocuments(folder.Library, model); using var vm = CreateCompanionEditor(folder.Library, documents);
        var reference = await SavedReference(vm);
        object?[] captured = [model, null];
        typeof(MacroEditorViewModel).GetMethod("CaptureCompanionSave", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, captured);
        try
        {
            int reads = documents.Reads;
            object?[] recoveryRead = [model.Id, documents.GetLibraryPath(model.Id), null];
            typeof(MacroEditorViewModel).GetMethod("ObserveCompanionRecovery", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, recoveryRead);
            recoveryRead[2].Should().BeNull(); documents.Reads.Should().Be(reads);
            (await vm.CompanionDocuments.ReadAsync(reference)).Status.Should().Be(SavedMacroDocumentReadStatus.Busy);
        }
        finally
        {
            typeof(MacroEditorViewModel).GetMethod("CompleteCompanionSave", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [vm.SelectedScript, null, captured[1]]);
        }
        (await SavedReference(vm)).Should().Be(reference);
        (await vm.CompanionDocuments.ReadAsync(reference)).Status.Should().Be(SavedMacroDocumentReadStatus.Ready);
    });

    [Fact]
    public Task CompanionDocument_RecordingBindsTheActualPostCaptureCommitDocument() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var model = ReceiptMacro();
        using var documents = new ReceiptDocuments(folder.Library, model); var recorder = new TestInputRecorder();
        using var vm = CreateCompanionEditor(folder.Library, documents, recorder);
        await vm.StartRecordingAsync();
        long tick = Stopwatch.GetTimestamp();
        recorder.Writer!.TryWrite(new(InputEventType.KeyDown, tick, 0, 0, 0x87));
        recorder.Writer.TryWrite(new(InputEventType.KeyUp, tick + Stopwatch.Frequency / 100, 0, 0, 0x87));
        await vm.StopRecordingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var actual = documents.Inner.Load(documents.GetLibraryPath(model.Id));
        actual.Model.RecordingCommit.Should().NotBeNull(); actual.Receipt.Should().NotBeNull();
        actual.Model.RecordingCommit!.ThroughSequence.Should().BeGreaterThanOrEqualTo(2);
        vm.SelectedScript!.RecordingCommit.Should().Be(actual.Model.RecordingCommit);
        var page = await CompanionPage(vm);
        page.Items.Single().TopLevelActions.Should().Be(actual.Model.Steps.Count);
        (await vm.CompanionDocuments.ReadAsync(await SavedReference(vm))).Status.Should().Be(SavedMacroDocumentReadStatus.Ready);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CompanionDocument_RecordingRecoveryPublishesOnlyAfterFreshOwnedRead(bool newerBaseline) => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var model = ReceiptMacro();
        using var documents = new ReceiptDocuments(folder.Library, model);
        using var vm = CreateCompanionEditor(folder.Library, documents);
        // Capture the actual saved editor baseline, rather than assuming construction and
        // the editor's normal pre-recovery save preserve every original input field.
        vm.Description = model.Description;
        vm.SaveMacroCommand.Execute(null);
        var baseline = documents.Inner.Load(documents.GetLibraryPath(model.Id)).Model;
        Guid sessionId = Guid.NewGuid();
        await using (var journal = await documents.Inner.RecordingRecovery.BeginAsync(new(sessionId, baseline, 0)))
        {
            journal.TryAppend(new(1, 0, InputEventType.KeyDown, 0, 0, 0x87)).Should().BeTrue();
            journal.TryAppend(new(2, TimeSpan.TicksPerMillisecond * 10, InputEventType.KeyUp, 0, 0, 0x87)).Should().BeTrue();
            await journal.CheckpointAsync();
        }
        const string changedDescription = "Newer saved document must survive recovery separately";
        if (newerBaseline)
        {
            vm.Description = changedDescription;
            vm.SaveMacroCommand.Execute(null);
        }
        vm.CheckForRecordingRecovery();
        Task RecoveryTask() => (Task)typeof(MacroEditorViewModel).GetField("_recordingRecoveryTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        await RecoveryTask().WaitAsync(TimeSpan.FromSeconds(5));
        vm.HasPendingRecordingRecovery.Should().BeTrue();
        vm.RecoverRecordingCommand.Execute(null);
        await RecoveryTask().WaitAsync(TimeSpan.FromSeconds(5));
        vm.HasPendingRecordingRecovery.Should().BeFalse();
        Guid recoveredId = newerBaseline ? sessionId : model.Id;
        var page = await CompanionPage(vm);
        page.Items.Should().HaveCount(newerBaseline ? 2 : 1);
        vm.SelectedScript!.Id.Should().Be(recoveredId);
        var reference = await SavedReference(vm, recoveredId);
        (await vm.CompanionDocuments.ReadAsync(reference)).Status.Should().Be(SavedMacroDocumentReadStatus.Ready);
        var recovered = documents.Inner.Load(documents.GetLibraryPath(recoveredId)).Model;
        recovered.RecordingCommit!.SessionId.Should().Be(sessionId);
        recovered.Steps.OfType<KeyPressStep>().Should().NotBeEmpty();
        if (newerBaseline)
        {
            var preserved = documents.Inner.Load(documents.GetLibraryPath(model.Id)).Model;
            preserved.Description.Should().Be(changedDescription);
            preserved.Steps.OfType<KeyPressStep>().Should().BeEmpty();
        }
    });

    [Fact]
    public Task CompanionDocument_GeneratedAndImportedEntriesResolveOnlyOwnedSavedIds() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); using var documents = new ReceiptDocuments(folder.Library, ReceiptMacro());
        using var vm = CreateCompanionEditor(folder.Library, documents);
        var generated = ReceiptMacro("Generated document"); vm.AddGeneratedMacro(generated).Should().BeTrue();
        var generatedRow = (await CompanionPage(vm)).Items.Single(row => row.Name == generated.Name);
        generatedRow.Id.Should().NotBe(generated.Id);
        (await vm.CompanionDocuments.ReadAsync(await SavedReference(vm, generatedRow.Id))).Status.Should().Be(SavedMacroDocumentReadStatus.Ready);
        var imported = ReceiptMacro("Imported document"); string external = Path.Combine(folder.Root, "source.json");
        File.WriteAllText(external, new MacroJsonSerializer().Serialize(imported));
        vm.ImportMacroFile(external).Should().BeTrue();
        var importedRow = (await CompanionPage(vm)).Items.Single(row => row.Name == imported.Name);
        importedRow.Id.Should().NotBe(imported.Id);
        (await vm.CompanionDocuments.ReadAsync(await SavedReference(vm, importedRow.Id))).Status.Should().Be(SavedMacroDocumentReadStatus.Ready);
        File.Exists(external).Should().BeTrue();
    });

    [Fact]
    public Task CompanionDocument_ImportRollbackRemovesReferencesAndKeepsPreviousSavedEntry() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); using var documents = new ReceiptDocuments(folder.Library, ReceiptMacro());
        using var vm = CreateCompanionEditor(folder.Library, documents); var before = await SavedReference(vm);
        const string passphrase = "owned fixture transfer passphrase";
        string source = Path.Combine(folder.Root, "source.zbackup");
        documents.Inner.ExportBackup([ReceiptMacro("First import"), ReceiptMacro("Second import")], source, passphrase);
        documents.FailBeforeSave = documents.Saves + 2;
        vm.ImportMacroFile(source, passphrase).Should().BeFalse();
        var page = await CompanionPage(vm); page.Items.Should().ContainSingle().Which.Id.Should().Be(before.MacroId);
        Directory.GetFiles(folder.Library, "*.zmacro").Should().ContainSingle();
        (await vm.CompanionDocuments.ReadAsync(before)).Status.Should().Be(SavedMacroDocumentReadStatus.Ready);
    });

    [Fact]
    public Task CompanionDocument_AlteredUndoCandidateRequiresExactAuthenticatedBytesBeforeResolution() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var original = ReceiptMacro("Original saved name");
        using var documents = new ReceiptDocuments(folder.Library, original, ReceiptMacro("Other document"));
        using var vm = CreateCompanionEditor(folder.Library, documents);
        var script = vm.MacroScripts.Single(row => row.Id == original.Id); vm.DeleteScript(script);
        string deleted = Directory.GetFiles(Path.Combine(folder.Library, ".deleted")).Single();
        string scratch = Path.Combine(folder.Root, "changed.zmacro");
        documents.Inner.Save(ReceiptWithName(original, "Actual restored ciphertext"), scratch);
        File.Copy(scratch, deleted, overwrite: true);
        FileStream? held = null;
        int handlesOpened = 0;
        System.Collections.Specialized.NotifyCollectionChangedEventHandler blockSelectionSave = (_, change) =>
        {
            if (change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add
                && held is null && change.NewItems?.Contains(script) == true)
            {
                held = new FileStream(documents.GetLibraryPath(original.Id), FileMode.Open, FileAccess.Read, FileShare.Read);
                handlesOpened++;
            }
        };
        vm.MacroScripts.CollectionChanged += blockSelectionSave;
        try
        {
            vm.UndoDeleteMacroCommand.Execute(null);
            handlesOpened.Should().Be(1);
            var row = (await CompanionPage(vm)).Items.Single(item => item.Id == original.Id);
            row.Name.Should().Be("Original saved name");
            (await vm.CompanionDocuments.ReadAsync(await SavedReference(vm, original.Id))).Status.Should().Be(SavedMacroDocumentReadStatus.Changed);
        }
        finally { vm.MacroScripts.CollectionChanged -= blockSelectionSave; held?.Dispose(); }
    });

    [Fact]
    public Task CompanionDocument_FailedDeleteKeepsOnlyAnExpectedCandidateAndRereadsUnchangedBytes() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var model = ReceiptMacro("Deletion is denied");
        using var documents = new ReceiptDocuments(folder.Library, model, ReceiptMacro("Other saved document"));
        using var vm = CreateCompanionEditor(folder.Library, documents);
        var reference = await SavedReference(vm, model.Id);
        var oldPin = (await vm.CompanionDocuments.ReadAsync(reference)).Pin!;
        string path = documents.GetLibraryPath(model.Id); byte[] original = File.ReadAllBytes(path);
        using var denyRename = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        vm.DeleteScript(vm.MacroScripts.Single(row => row.Id == model.Id));
        vm.MacroScripts.Should().HaveCount(2);
        File.ReadAllBytes(path).Should().Equal(original);
        oldPin.IsCurrent().Should().BeFalse();
        (await SavedReference(vm, model.Id)).Should().Be(reference);
        (await vm.CompanionDocuments.ReadAsync(reference)).Status.Should().Be(SavedMacroDocumentReadStatus.Ready);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task CompanionDocument_LegacyMigrationRequiresCanonicalOwnedReceipt(bool canonical) => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var model = ReceiptMacro();
        using var documents = new ReceiptDocuments(folder.Library);
        string source = Path.Combine(folder.Library, canonical ? model.Id.ToString("N") + ".json" : "descriptive.json");
        File.WriteAllText(source, new MacroJsonSerializer().Serialize(model));
        using var vm = CreateCompanionEditor(folder.Library, documents);
        var reference = await SavedReference(vm);
        (await vm.CompanionDocuments.ReadAsync(reference)).Status.Should().Be(canonical
            ? SavedMacroDocumentReadStatus.Ready : SavedMacroDocumentReadStatus.Unavailable);
        File.Exists(Path.ChangeExtension(source, ".zmacro")).Should().BeTrue();
    });

    [Fact]
    public Task CompanionDocument_WriterBudgetQuarantineRequiresAllOwnersAndExplicitReload() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); using var documents = new ReceiptDocuments(folder.Library, ReceiptMacro());
        using var vm = CreateCompanionEditor(folder.Library, documents); var reference = await SavedReference(vm);
        var begin = typeof(MacroEditorViewModel).GetMethod("BeginCompanionMutation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var end = typeof(MacroEditorViewModel).GetMethod("CompleteCompanionMutation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var owners = new List<object>();
        try
        {
            for (int index = 0; index < 65; index++) { object?[] args = [null, null]; begin.Invoke(vm, args); owners.Add(args[1]!); }
            (await vm.CompanionDocuments.ReadAsync(reference)).Status.Should().Be(SavedMacroDocumentReadStatus.Unavailable);
            ReloadReceiptLibrary(vm);
            (await vm.CompanionDocuments.ReadAsync(await SavedReference(vm))).Status.Should().Be(SavedMacroDocumentReadStatus.Unavailable);
        }
        finally { foreach (var owner in owners) end.Invoke(vm, [owner]); }
        (await vm.CompanionDocuments.ReadAsync(await SavedReference(vm))).Status.Should().Be(SavedMacroDocumentReadStatus.Unavailable);
        ReloadReceiptLibrary(vm);
        (await vm.CompanionDocuments.ReadAsync(await SavedReference(vm))).Status.Should().Be(SavedMacroDocumentReadStatus.Ready);
    });

    private sealed class ReceiptDocuments : ISavedMacroDocumentStore, IRecordingRecoveryProvider, IDisposable
    {
        public ProtectedMacroStore Inner { get; }
        public int Reads, Saves, FailBeforeSave, FailAfterSave;
        public bool Disposed { get; private set; }
        public string? PreparedName;
        private int _blockRead, _blockWrite;
        public TaskCompletionSource ReadEntered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteEntered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReadRelease { get; } = new(false);
        public ManualResetEventSlim WriteRelease { get; } = new(false);
        public ReceiptDocuments(string directory, params MacroModel[] models)
        {
            Inner = new(directory, new ReceiptKeyStore());
            foreach (var model in models) Inner.Save(model, Inner.GetLibraryPath(model.Id));
        }
        public bool CanWriteLibrary => Inner.CanWriteLibrary;
        public IRecordingRecoveryStore RecordingRecovery => Inner.RecordingRecovery;
        public string GetLibraryPath(Guid id) => Inner.GetLibraryPath(id);
        public IReadOnlyList<string> EnumerateLibraryFiles() => Inner.EnumerateLibraryFiles();
        public MacroDocumentLoadResult MigrateLibraryDocument(string path) => Inner.MigrateLibraryDocument(path);
        public MacroDocumentLoadResult Load(string path, string? passphrase = null)
        {
            Interlocked.Increment(ref Reads);
            var loaded = Inner.Load(path, passphrase);
            if (Interlocked.Exchange(ref _blockRead, 0) != 0)
            {
                ReadEntered.TrySetResult();
                if (!ReadRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Owned receipt read gate was not released.");
            }
            return loaded;
        }
        public void Save(MacroModel model, string path) => SaveWithReceipt(model, path);
        public SavedMacroDocument SaveWithReceipt(MacroModel model, string path)
        {
            int count = Interlocked.Increment(ref Saves);
            if (Interlocked.Exchange(ref _blockWrite, 0) != 0)
            {
                WriteEntered.TrySetResult();
                if (!WriteRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Owned receipt write gate was not released.");
            }
            if (count == FailBeforeSave) throw new IOException("Owned fixture save failed before publication.");
            if (PreparedName is { } name) model = ReceiptWithName(model, name);
            var saved = Inner.SaveWithReceipt(model, path);
            if (count == FailAfterSave) throw new IOException("Owned fixture save failed after publication.");
            return saved;
        }
        public void BlockNextRead() { ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); ReadRelease.Reset(); Interlocked.Exchange(ref _blockRead, 1); }
        public void BlockNextWrite() { WriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); WriteRelease.Reset(); Interlocked.Exchange(ref _blockWrite, 1); }
        public void ExportBackup(IReadOnlyList<MacroModel> models, string path, string passphrase) => Inner.ExportBackup(models, path, passphrase);
        public void RecoverLibraryFromBackup(string path, string passphrase) => Inner.RecoverLibraryFromBackup(path, passphrase);
        public void Dispose() { Disposed = true; ReadRelease.Set(); WriteRelease.Set(); ReadRelease.Dispose(); WriteRelease.Dispose(); }
    }

    private sealed class ReceiptKeyStore : ILocalMacroKeyStore
    {
        public byte[] GetKey(bool createIfMissing) => Enumerable.Repeat((byte)93, 32).ToArray();
    }

    private sealed class ReceiptMemoryTransport(TargetedInputTarget expected) : ITargetedInputTransport
    {
        public List<TargetedInputCommand> Commands { get; } = new();
        public ValueTask<TargetedInputValidation> ValidateAsync(TargetedInputTarget target, bool cleanup, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(target == expected ? TargetedInputValidation.Current : TargetedInputValidation.Unavailable);
        }
        public ValueTask<TargetedInputSubmission> SubmitAsync(TargetedInputCommand command, Func<bool> admissionOpen, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (command.Target != expected || !admissionOpen() || Commands.Count >= 16)
                return ValueTask.FromResult(TargetedInputSubmission.Rejected);
            Commands.Add(command);
            return ValueTask.FromResult(TargetedInputSubmission.Queued);
        }
    }
}
#endif
