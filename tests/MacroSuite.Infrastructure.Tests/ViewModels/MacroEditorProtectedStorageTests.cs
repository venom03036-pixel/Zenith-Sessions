using FluentAssertions;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.AI;
using MacroSuite.Core.Models;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Usage;
using MacroSuite.Core.Licensing;
using MacroSuite.Infrastructure.Storage;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    [Fact]
    public void ProtectedLibrary_DeferredStartupDoesNotReadOrCreateDocumentsBeforeActivation()
    {
        using var folder = new RoutingTempFolder(); var documents = new DeferredDocumentSpy(folder.Library);
        var gate = new DummyFeatureGate();
        using var vm = new MacroEditorViewModel(new DummyCaptureService(), new DummyInputPlayer(),
            new AIVisionRouter(new InMemorySecretVault()), new DummyApprovalGate(), gate,
            new UsageTracker(new DummyUsageRepository(), gate), new DummyAuditLogger(),
            customMacrosDirectory: folder.Library, documentStore: documents, deferLibraryLoad: true);
        documents.Calls.Should().Be(0); vm.MacroScripts.Should().BeEmpty();
        vm.LoadLibraryAfterActivation().Should().BeTrue();
        documents.Calls.Should().BeGreaterThan(0); vm.MacroScripts.Should().ContainSingle();
    }

    [Fact]
    public void ProtectedLibrary_LostKeyReportsLockedStateWithoutCreatingAnEmptyReplacement()
    {
        using var folder = new RoutingTempFolder();
        var store = new ProtectedMacroStore(folder.Library); var model = new MacroModel { Name = "Existing encrypted macro" };
        string path = store.GetLibraryPath(model.Id); store.Save(model, path); byte[] previous = File.ReadAllBytes(path);
        string keyPath = Path.Combine(folder.Library, "Security", "macro-data-key.bin"); File.Delete(keyPath);
        using var vm = CreatePersistentEditor(folder.Library);
        vm.LibraryKeyUnavailable.Should().BeTrue(); vm.LibraryAccessError.Should().NotBeEmpty();
        vm.ActivationService = new StorageActivation(true);
        vm.CanEditMacro.Should().BeFalse(); vm.CanImportMacro.Should().BeTrue(); vm.LoadMacroCommand.CanExecute(null).Should().BeTrue();
        vm.MacroScripts.Should().BeEmpty(); Directory.GetFiles(folder.Library, "*.zmacro").Should().ContainSingle();
        File.ReadAllBytes(path).Should().Equal(previous); File.Exists(keyPath).Should().BeFalse();
    }

    [Fact]
    public void ProtectedLibrary_OneDamagedDocumentPreservesOtherMacrosAndAvailableKey()
    {
        using var folder = new RoutingTempFolder(); var store = new ProtectedMacroStore(folder.Library);
        var valid = new MacroModel { Name = "Still usable" }; var damaged = new MacroModel { Name = "Damaged file" };
        store.Save(valid, store.GetLibraryPath(valid.Id)); string badPath = store.GetLibraryPath(damaged.Id); store.Save(damaged, badPath);
        byte[] changed = File.ReadAllBytes(badPath); changed[^1] ^= 32; File.WriteAllBytes(badPath, changed);
        using var vm = CreatePersistentEditor(folder.Library);
        vm.LibraryKeyUnavailable.Should().BeFalse(); vm.MacroScripts.Should().ContainSingle().Which.Name.Should().Be(valid.Name);
        vm.LibraryAccessError.Should().NotBeEmpty(); File.ReadAllBytes(badPath).Should().Equal(changed);
    }

    [Fact]
    public void ProtectedBackup_WrongPassphraseNeverAsksToReplaceTheLocalKey()
    {
        using var folder = new RoutingTempFolder(); var store = new ProtectedMacroStore(folder.Library);
        string backup = Path.Combine(folder.Root, "macro.zbackup"); store.ExportBackup([new MacroModel { Name = "Backup" }], backup, StorageTestPassphrase);
        using var vm = CreatePersistentEditor(folder.Library); int prompts = 0;
        vm.ConfirmBackupRecoveryPrompter = () => { prompts++; return true; };
        int before = vm.MacroScripts.Count;
        vm.ImportMacroFile(backup, "wrong passphrase").Should().BeFalse(); prompts.Should().Be(0); vm.MacroScripts.Count.Should().Be(before);
    }

    [Fact]
    public void ProtectedBackup_VerifiedLostKeyRecoveryRequiresConfirmationAndRestoresTheLibrary()
    {
        using var folder = new RoutingTempFolder(); var store = new ProtectedMacroStore(folder.Library);
        var original = new MacroModel { Name = "Original unavailable" }; store.Save(original, store.GetLibraryPath(original.Id));
        string backup = Path.Combine(folder.Root, "macro.zbackup"); var restored = new MacroModel { Name = "Recovered macro" };
        store.ExportBackup([restored], backup, StorageTestPassphrase);
        File.WriteAllBytes(Path.Combine(folder.Library, "Security", "macro-data-key.bin"), [1, 2, 3]);
        using var vm = CreatePersistentEditor(folder.Library); vm.LibraryKeyUnavailable.Should().BeTrue();
        int confirmations = 0; vm.ConfirmBackupRecoveryPrompter = () => { confirmations++; return true; };
        vm.ImportMacroFile(backup, StorageTestPassphrase).Should().BeTrue(); confirmations.Should().Be(1);
        vm.LibraryKeyUnavailable.Should().BeFalse(); vm.LibraryAccessError.Should().BeEmpty();
        vm.MacroScripts.Should().ContainSingle().Which.Name.Should().Be(restored.Name);
        Directory.Exists(Path.Combine(folder.Library, "Recovery")).Should().BeTrue();
    }

    [Fact]
    public void ProtectedImport_InactiveLicenseAndExternalInputBlockImport()
    {
        using var folder = new RoutingTempFolder(); using var vm = CreatePersistentEditor(folder.Library);
        vm.ActivationService = new StorageActivation(false);
        vm.CanImportMacro.Should().BeFalse(); vm.LoadMacroCommand.CanExecute(null).Should().BeFalse();
        vm.ActivationService = new StorageActivation(true);
        vm.ExternalInputIsActive = () => true; vm.RefreshImportAvailability();
        vm.CanImportMacro.Should().BeFalse(); vm.LoadMacroCommand.CanExecute(null).Should().BeFalse();
        vm.ExternalInputIsActive = () => false; vm.RefreshImportAvailability(); vm.CanImportMacro.Should().BeTrue();
    }

    private sealed class StorageActivation(bool active) : IActivationService
    {
        public bool IsActive => active;
        public string StatusMessage => active ? "Active" : "Activation required";
        public VerifiedLicense? CurrentLicense => null;
        public event EventHandler? StateChanged { add { } remove { } }
        public Task<ActivationOperationResult> RefreshAsync(CancellationToken ct = default) => Task.FromResult(new ActivationOperationResult(active, StatusMessage));
        public Task<ActivationRequestResult> CreateRequestAsync(bool allowSoftwareFallback = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ActivationOperationResult> ImportAsync(string licenseText, CancellationToken ct = default) => throw new NotSupportedException();
        public void RequireActive() { if (!active) throw new ActivationRequiredException(StatusMessage); }
    }

    private sealed class DeferredDocumentSpy(string directory) : IMacroDocumentStore
    {
        public int Calls { get; private set; }
        public bool CanWriteLibrary { get { Calls++; return true; } }
        public IReadOnlyList<string> EnumerateLibraryFiles() { Calls++; return []; }
        public MacroDocumentLoadResult Load(string path, string? passphrase = null) { Calls++; throw new NotSupportedException(); }
        public MacroDocumentLoadResult MigrateLibraryDocument(string path) { Calls++; throw new NotSupportedException(); }
        public void Save(MacroModel model, string path) => Calls++;
        public void ExportBackup(IReadOnlyList<MacroModel> models, string path, string passphrase) => Calls++;
        public void RecoverLibraryFromBackup(string path, string passphrase) => Calls++;
        public string GetLibraryPath(Guid id) { Calls++; return Path.Combine(directory, id.ToString("N") + ".zmacro"); }
    }
}
