#if DEVELOPER_MODE
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using FluentAssertions;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.AI;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Usage;
using MacroSuite.Infrastructure.Storage;
using Xunit;
using Zenith.MacroHost.Contracts;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    private MacroEditorViewModel CreateCompanionEditor(string directory, IMacroDocumentStore documents,
        TestInputRecorder? recorder = null, bool deferred = false)
    {
        var gate = new DummyFeatureGate();
        return new MacroEditorViewModel(new DummyCaptureService(), new DummyInputPlayer(),
            new AIVisionRouter(new InMemorySecretVault()), new DummyApprovalGate(), gate,
            new UsageTracker(new DummyUsageRepository(), gate), new DummyAuditLogger(),
            customMacrosDirectory: directory, documentStore: documents, seedDemoData: false,
            inputRecorder: recorder ?? new TestInputRecorder(), recordingDesktop: new ExternalRecordingDesktop(),
            deferLibraryLoad: deferred) { RecordingGracePeriodMs = 0 };
    }

    private static Task<MacroCatalogPage> CompanionPage(MacroEditorViewModel vm)
        => vm.CompanionCatalog.GetPageAsync(new());

    private static async Task CompanionOnUi(Func<Task> body)
    {
        var exited = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            Exception? failure = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                Task work = body();
                Task shutdown = work.ContinueWith(_ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                Dispatcher.Run();
                shutdown.GetAwaiter().GetResult();
                work.GetAwaiter().GetResult();
            }
            catch (Exception error) { failure = error; }
            finally
            {
                try { if (!dispatcher.HasShutdownFinished) dispatcher.InvokeShutdown(); }
                catch (Exception error) { failure ??= error; }
                SynchronizationContext.SetSynchronizationContext(null);
                exited.TrySetResult(failure);
            }
        }) { IsBackground = true, Name = "Owned Personal catalog fixture dispatcher" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Exception? result = await exited.Task.ConfigureAwait(false);
        thread.Join(); // The body and dispatcher have terminated; do not abandon an owned UI thread.
        if (result is not null) ExceptionDispatchInfo.Capture(result).Throw();
    }

    [Fact]
    public Task CompanionCatalog_QueriesOnlyLoadedMetadata_AndNeverReadsDocumentsOrDrafts() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder();
        var saved = new MacroModel
        {
            Name = "Saved display name", Description = "private body description",
            Settings = new() { Hotkey = "Ctrl+6", IsActive = false,
                Target = new() { Mode = MacroTargetMode.SpecificWindow, ApplicationId = "private.exe", WindowTitle = "private title" } },
            Steps = [new DelayStep(TimeSpan.Zero)]
        };
        using var documents = new CompanionDocuments(folder.Library, saved);
        using var vm = CreateCompanionEditor(folder.Library, documents);
        int reads = documents.Reads;
        var first = await CompanionPage(vm);
        first.State.Should().Be(MacroCatalogState.Ready);
        var descriptor = first.Items.Should().ContainSingle().Subject;
        descriptor.Should().BeEquivalentTo(new SavedMacroDescriptor(saved.Id, descriptor.SavedRevision,
            saved.Name, "Ctrl+6", 1, false, "SpecificWindow"));
        vm.MacroName = "Unsaved draft name"; vm.Description = "another private draft";
        vm.SelectedScript!.Hotkey = "Ctrl+7";
        var second = await CompanionPage(vm);
        second.Items.Should().Equal(first.Items); second.CatalogRevision.Should().Be(first.CatalogRevision);
        documents.Reads.Should().Be(reads);
        string metadata = System.Text.Json.JsonSerializer.Serialize(second);
        metadata.Should().NotContain("private").And.NotContain("draft").And.NotContain(folder.Root);
    });

    [Fact]
    public Task CompanionCatalog_FailedSaveKeepsTheLastSavedRevision_ThenSuccessPublishesExactModel() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); using var documents = new CompanionDocuments(folder.Library);
        using var vm = CreateCompanionEditor(folder.Library, documents);
        vm.AddDelayCommand.Execute(null);
        var before = await CompanionPage(vm);
        vm.MacroName = "New saved name";
        documents.FailOnSave = documents.Saves + 1;
        vm.SaveMacroCommand.Execute(null);
        var failed = await CompanionPage(vm);
        failed.Items.Should().Equal(before.Items); failed.CatalogRevision.Should().Be(before.CatalogRevision);
        documents.FailOnSave = 0; vm.SaveMacroCommand.Execute(null);
        var after = await CompanionPage(vm);
        after.Items.Should().ContainSingle().Which.Name.Should().Be("New saved name");
        after.Items[0].TopLevelActions.Should().Be(documents.LastSaved!.Steps.Count);
        after.Items[0].SavedRevision.Should().NotBe(before.Items[0].SavedRevision);
    });

    [Fact]
    public Task CompanionCatalog_RecordingPublicationUsesTheSnapshotTakenBeforeTheWrite() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); using var documents = new CompanionDocuments(folder.Library);
        var recorder = new TestInputRecorder(); using var vm = CreateCompanionEditor(folder.Library, documents, recorder);
        vm.MacroName = "Recording snapshot"; vm.SaveMacroCommand.Execute(null);
        await vm.StartRecordingAsync();
        recorder.Writer!.TryWrite(new(InputEventType.KeyDown, Stopwatch.GetTimestamp(), 0, 0, 87));
        documents.BlockNextSave(); Task stopping = vm.StopRecordingAsync();
        try
        {
            await documents.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.SelectedScript!.Name = "Late draft name"; vm.SelectedScript.Hotkey = "Ctrl+9";
        }
        finally { documents.Release.Set(); }
        await stopping;
        var page = await CompanionPage(vm);
        var item = page.Items.Should().ContainSingle().Subject;
        item.Name.Should().Be("Recording snapshot");
        item.Hotkey.Should().Be(documents.LastSaved!.Settings.Hotkey);
        item.TopLevelActions.Should().Be(documents.LastSaved.Steps.Count);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CompanionCatalog_LateRecordingSaveCannotResurrectRemovedOrResetMetadata(bool reset) => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); using var documents = new CompanionDocuments(folder.Library);
        var recorder = new TestInputRecorder(); using var vm = CreateCompanionEditor(folder.Library, documents, recorder);
        var original = vm.SelectedScript!;
        await vm.StartRecordingAsync();
        recorder.Writer!.TryWrite(new(InputEventType.KeyDown, Stopwatch.GetTimestamp(), 0, 0, 87));
        documents.BlockNextSave(); Task stopping = vm.StopRecordingAsync();
        try
        {
            await documents.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (reset) vm.MacroScripts.Clear(); else vm.MacroScripts.Remove(original);
        }
        finally { documents.Release.Set(); }
        await stopping;
        var page = await CompanionPage(vm);
        page.Items.Should().NotContain(item => item.Id == original.Id);
        page.State.Should().Be(reset ? MacroCatalogState.Starting : MacroCatalogState.Ready);
    });

    [Fact]
    public Task CompanionCatalog_PartlyUnreadableLibraryIsUnavailable_InsteadOfHealthyPartialOrEmpty() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder();
        var valid = new MacroModel { Name = "Readable" }; var denied = new MacroModel { Name = "Unreadable" };
        using var documents = new CompanionDocuments(folder.Library, valid, denied) { DeniedId = denied.Id };
        using var vm = CreateCompanionEditor(folder.Library, documents);
        vm.MacroScripts.Should().ContainSingle().Which.Id.Should().Be(valid.Id);
        var page = await CompanionPage(vm);
        page.State.Should().Be(MacroCatalogState.Unavailable); page.Items.Should().BeEmpty();
    });

    [Fact]
    public Task CompanionCatalog_DeferredLibraryRemainsStartingWithoutDocumentReads() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); using var documents = new CompanionDocuments(folder.Library);
        using var vm = CreateCompanionEditor(folder.Library, documents, deferred: true);
        (await CompanionPage(vm)).State.Should().Be(MacroCatalogState.Starting);
        documents.Reads.Should().Be(0); documents.Saves.Should().Be(0);
        vm.LoadLibraryAfterActivation().Should().BeTrue();
        (await CompanionPage(vm)).State.Should().Be(MacroCatalogState.Ready);
    });

    [Fact]
    public Task CompanionCatalog_ImportedRowsAppearOnlyAfterTheWholeImportIsSaved_WithOwnedIds() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); using var documents = new CompanionDocuments(folder.Library);
        var source = new MacroModel { Name = "Imported", Steps = [new DelayStep(TimeSpan.Zero)] };
        documents.Imported = [source];
        using var vm = CreateCompanionEditor(folder.Library, documents);
        vm.ImportMacroFile(Path.Combine(folder.Root, "source.json")).Should().BeTrue();
        var page = await CompanionPage(vm);
        page.State.Should().Be(MacroCatalogState.Ready); page.Items.Should().HaveCount(2);
        var imported = page.Items.Single(item => item.Name == source.Name);
        imported.Id.Should().NotBe(source.Id); imported.TopLevelActions.Should().Be(1);
        imported.TargetMode.Should().Be("Legacy");
    });

    [Fact]
    public Task CompanionCatalog_FailedMultiImportNeverPublishesRolledBackRows() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); using var documents = new CompanionDocuments(folder.Library);
        documents.Imported = [new() { Name = "First import" }, new() { Name = "Second import" }];
        using var vm = CreateCompanionEditor(folder.Library, documents);
        var before = await CompanionPage(vm);
        documents.FailOnSave = documents.Saves + 2;
        vm.ImportMacroFile(Path.Combine(folder.Root, "source.json")).Should().BeFalse();
        var after = await CompanionPage(vm);
        after.Items.Should().Equal(before.Items); after.CatalogRevision.Should().Be(before.CatalogRevision);
    });

    [Fact]
    public Task CompanionCatalog_ImportRemainsPublishedWhenItsLaterSelectionAutosaveFails() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); using var documents = new CompanionDocuments(folder.Library);
        var source = new MacroModel { Name = "Successfully imported before autosave", Steps = [new DelayStep(TimeSpan.Zero)] };
        documents.Imported = [source];
        using var vm = CreateCompanionEditor(folder.Library, documents);
        int baseline = documents.Saves;
        // Import write, outgoing selection save, then the newly selected imported row's save.
        documents.FailOnSave = baseline + 3;
        vm.ImportMacroFile(Path.Combine(folder.Root, "source.json")).Should().BeTrue();
        documents.Failures.Should().Be(1);
        vm.SaveStateText.Should().Be("Save failed");
        var page = await CompanionPage(vm);
        page.State.Should().Be(MacroCatalogState.Ready);
        page.Items.Should().HaveCount(2);
        var imported = page.Items.Single(item => item.Name == source.Name);
        imported.Id.Should().NotBe(source.Id); imported.TopLevelActions.Should().Be(1);
        documents.Load(documents.GetLibraryPath(imported.Id)).Model.Name.Should().Be(source.Name);
    });

    [Fact]
    public Task CompanionCatalog_ImportRollbackAfterPublicationRemovesItsMetadataWithItsOwnedFiles() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); using var documents = new CompanionDocuments(folder.Library);
        documents.Imported = [new() { Name = "Rolled back after publication" }];
        using var vm = CreateCompanionEditor(folder.Library, documents);
        var originalId = vm.SelectedScript!.Id;
        vm.HotkeysUpdated += () => throw new IOException("Owned post-publication observer failure.");
        vm.ImportMacroFile(Path.Combine(folder.Root, "source.json")).Should().BeFalse();
        var page = await CompanionPage(vm);
        page.State.Should().Be(MacroCatalogState.Ready);
        page.Items.Should().ContainSingle().Which.Id.Should().Be(originalId);
        Directory.GetFiles(folder.Library, "*.zmacro").Should().ContainSingle();
    });

    [Fact]
    public Task CompanionCatalog_GeneratedMacroSavedBeforeItsRowInsertionPublishesTheActualOwnedId() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); using var documents = new CompanionDocuments(folder.Library);
        using var vm = CreateCompanionEditor(folder.Library, documents);
        var generated = new MacroModel { Name = "Generated saved macro", Steps = [new DelayStep(TimeSpan.Zero)] };
        vm.AddGeneratedMacro(generated).Should().BeTrue();
        var descriptor = (await CompanionPage(vm)).Items.Single(item => item.Name == generated.Name);
        descriptor.Id.Should().Be(documents.LastSaved!.Id).And.NotBe(generated.Id);
        descriptor.TopLevelActions.Should().Be(documents.LastSaved.Steps.Count);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CompanionCatalog_UndoPublishesOnlyTheExactRestoredEncryptedDocument(bool removeDeletedFile) => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var store = new ProtectedMacroStore(folder.Library);
        var original = new MacroModel { Name = "Saved before delete", Steps = [new DelayStep(TimeSpan.Zero)] };
        var other = new MacroModel { Name = "Other macro" };
        store.Save(original, store.GetLibraryPath(original.Id)); store.Save(other, store.GetLibraryPath(other.Id));
        using var vm = CreatePersistentEditor(folder.Library);
        var first = (await CompanionPage(vm)).Items.Single(item => item.Id == original.Id);
        var script = vm.MacroScripts.Single(item => item.Id == original.Id);
        script.Name = "Unsaved name before delete";
        vm.DeleteScript(script);
        (await CompanionPage(vm)).Items.Should().NotContain(item => item.Id == original.Id);
        if (removeDeletedFile)
        {
            File.Delete(Directory.GetFiles(Path.Combine(folder.Library, ".deleted")).Single());
            // Deny a separate new save so it cannot conceal the missing encrypted restoration.
            Directory.CreateDirectory(store.GetLibraryPath(original.Id));
        }
        FileStream? restoredRead = null;
        System.Collections.Specialized.NotifyCollectionChangedEventHandler holdRestoredFile = (_, change) =>
        {
            if (!removeDeletedFile && change.NewItems?.Contains(script) == true)
                restoredRead = new FileStream(store.GetLibraryPath(original.Id), FileMode.Open, FileAccess.Read, FileShare.Read);
        };
        vm.MacroScripts.CollectionChanged += holdRestoredFile;
        try
        {
            vm.UndoDeleteMacroCommand.Execute(null);
            // Selection intentionally attempts another save. Deny it to inspect the exact
            // successful encrypted restoration, rather than a later saved draft overwrite.
            vm.SaveStateText.Should().Be("Save failed");
            var after = await CompanionPage(vm);
            if (removeDeletedFile) after.Items.Should().NotContain(item => item.Id == original.Id);
            else
            {
                restoredRead.Should().NotBeNull();
                var restored = after.Items.Single(item => item.Id == original.Id);
                restored.Name.Should().Be(first.Name); restored.TopLevelActions.Should().Be(first.TopLevelActions);
                restored.SavedRevision.Should().NotBe(first.SavedRevision);
                store.Load(store.GetLibraryPath(original.Id)).Model.Name.Should().Be(first.Name);
            }
        }
        finally { vm.MacroScripts.CollectionChanged -= holdRestoredFile; restoredRead?.Dispose(); }
    });

    [Fact]
    public Task CompanionCatalog_InvalidSuccessfulMetadataDoesNotReportDocumentSaveFailure() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); using var documents = new CompanionDocuments(folder.Library);
        using var vm = CreateCompanionEditor(folder.Library, documents); var messages = new List<string>();
        vm.StatusChanged += messages.Add;
        vm.MacroName = new string('x', MacroCatalogLimits.MaximumNameLength + 1);
        vm.SaveMacroCommand.Execute(null);
        documents.LastSaved!.Name.Should().HaveLength(MacroCatalogLimits.MaximumNameLength + 1);
        messages.Should().Contain(message => message.StartsWith("Saved "));
        messages.Should().NotContain(message => message.StartsWith("Could not save"));
        (await CompanionPage(vm)).State.Should().Be(MacroCatalogState.Unavailable);
    });

    [Fact]
    public Task CompanionCatalog_FailedInitialSaveIsUnavailable_AndRestartRevisionsAreEphemeral() => CompanionOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); using var documents = new CompanionDocuments(folder.Library) { FailOnSave = 1 };
        using (var failed = CreateCompanionEditor(folder.Library, documents))
            (await CompanionPage(failed)).State.Should().Be(MacroCatalogState.Unavailable);
        documents.FailOnSave = 0;
        MacroCatalogPage first;
        using (var vm = CreateCompanionEditor(folder.Library, documents)) first = await CompanionPage(vm);
        using var reopened = CreateCompanionEditor(folder.Library, documents);
        var second = await CompanionPage(reopened);
        second.HostSessionId.Should().NotBe(first.HostSessionId);
        second.Items[0].Id.Should().Be(first.Items[0].Id);
        second.Items[0].SavedRevision.Should().NotBe(first.Items[0].SavedRevision);
    });

    /// <summary>Controlled document owner: only synthetic marker files under this fixture's temporary library.</summary>
    private sealed class CompanionDocuments : IMacroDocumentStore, IDisposable
    {
        private readonly string _directory;
        private readonly object _sync = new();
        private readonly Dictionary<string, MacroModel> _models = new(StringComparer.OrdinalIgnoreCase);
        private int _blockNext;
        public int Reads, Saves, Failures;
        public int FailOnSave;
        public Guid? DeniedId;
        public MacroModel? LastSaved;
        public IReadOnlyList<MacroModel> Imported = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public CompanionDocuments(string directory, params MacroModel[] models)
        {
            _directory = directory;
            foreach (var model in models)
            {
                string path = GetLibraryPath(model.Id); _models.Add(path, model);
                File.WriteAllText(path, "owned synthetic document-store marker");
            }
        }
        public bool CanWriteLibrary => true;
        public string GetLibraryPath(Guid id) => Path.Combine(_directory, id + ".zmacro");
        public IReadOnlyList<string> EnumerateLibraryFiles()
        { Interlocked.Increment(ref Reads); lock (_sync) return _models.Keys.Where(File.Exists).ToArray(); }
        public MacroDocumentLoadResult Load(string path, string? passphrase = null)
        {
            Interlocked.Increment(ref Reads);
            lock (_sync)
            {
                if (_models.TryGetValue(path, out var model))
                {
                    if (model.Id == DeniedId) throw new IOException("Owned fixture read denied.");
                    return new([model], path, false);
                }
                return new(Imported, path, false);
            }
        }
        public MacroDocumentLoadResult MigrateLibraryDocument(string path) => Load(path);
        public void BlockNextSave() => Interlocked.Exchange(ref _blockNext, 1);
        public void Save(MacroModel model, string path)
        {
            int number = Interlocked.Increment(ref Saves);
            if (Interlocked.Exchange(ref _blockNext, 0) != 0)
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Owned fixture save was not released.");
            }
            if (number == FailOnSave) { Interlocked.Increment(ref Failures); throw new IOException("Owned fixture write denied."); }
            lock (_sync)
            {
                _models[path] = model; LastSaved = model;
                File.WriteAllText(path, "owned synthetic document-store marker");
            }
        }
        public void ExportBackup(IReadOnlyList<MacroModel> models, string path, string passphrase) => throw new NotSupportedException();
        public void RecoverLibraryFromBackup(string path, string passphrase) => throw new NotSupportedException();
        public void Dispose() { Release.Set(); Release.Dispose(); }
    }
}
#endif
