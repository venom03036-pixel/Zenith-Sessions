using FluentAssertions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using MacroSuite.Infrastructure.Storage;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Storage;

public sealed class RecordingDocumentIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-recording-identity-" + Guid.NewGuid().ToString("N"));
    private readonly ILocalMacroKeyStore _keys = new IdentityKeys();
    private ProtectedMacroStore Store => new(_root, _keys);
    public RecordingDocumentIdentityTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Recovery_ExistingDescriptiveFilename_AppendsOnceAndPreservesFilename()
    {
        var store = Store;
        var baseline = new MacroModel { Name = "Existing descriptive document", Settings = new() { Target = null } };
        string path = Path.Combine(_root, "Existing descriptive document_" + baseline.Id.ToString("N") + ".zmacro");
        store.Save(baseline, path);
        await using (var session = await store.RecordingRecovery.BeginAsync(new(Guid.NewGuid(), baseline, 0)))
        {
            session.TryAppend(new(1, 0, InputEventType.KeyDown, 0, 0, 87)).Should().BeTrue();
            session.TryAppend(new(2, TimeSpan.TicksPerMillisecond * 10, InputEventType.KeyUp, 0, 0, 87)).Should().BeTrue();
        }
        var candidate = (await store.RecordingRecovery.ReadPendingAsync()).Should().ContainSingle().Subject;
        var recovered = await store.RecordingRecovery.RecoverAsync(candidate);
        recovered.Should().NotBeNull();
        recovered!.Id.Should().Be(baseline.Id);
        store.Load(path).Model.Steps.Should().HaveCount(2);
        File.Exists(store.GetLibraryPath(baseline.Id)).Should().BeFalse();
        (await store.RecordingRecovery.ReadPendingAsync()).Should().BeEmpty();
        (await store.RecordingRecovery.RecoverAsync(candidate)).Should().BeNull();
        store.Load(path).Model.Steps.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("wrong-canonical-identity")]
    [InlineData("unreadable")]
    public async Task Commit_UnverifiableManagedIdentity_PreservesDocumentsAndJournal(string problem)
    {
        var store = Store;
        var baseline = new MacroModel { Name = "Retained original" };
        string alias = Path.Combine(_root, "existing-name.zmacro");
        store.Save(baseline, alias);
        await using var session = await store.RecordingRecovery.BeginAsync(new(Guid.NewGuid(), baseline, 0));
        session.TryAppend(new(1, 0, InputEventType.KeyDown, 0, 0, 87)).Should().BeTrue();
        var final = new MacroModel {
            Id = baseline.Id, Name = baseline.Name, CreatedAt = baseline.CreatedAt, Settings = baseline.Settings,
            Steps = new[] { new KeyPressStep(87) }, RecordingCommit = session.CreateCapturedCommitMetadata(1) };
        await session.PrepareCommitAsync(final);
        store.Save(final, alias);
        string protectedPath = problem == "duplicate" ? Path.Combine(_root, "other-name.zmacro") : store.GetLibraryPath(baseline.Id);
        if (problem == "duplicate") store.Save(baseline, protectedPath);
        else if (problem == "wrong-canonical-identity") store.Save(new MacroModel { Name = "Different identity" }, protectedPath);
        else await File.WriteAllBytesAsync(protectedPath, new byte[] { 0x5a, 0x4d, 0x43, 0x52, 2 });
        byte[] original = await File.ReadAllBytesAsync(protectedPath);
        Func<Task> complete = () => session.CompleteCommitAsync();
        await complete.Should().ThrowAsync<IOException>();
        (await File.ReadAllBytesAsync(protectedPath)).Should().Equal(original);
        store.Load(alias).Model.Steps.Should().ContainSingle();
        (await store.RecordingRecovery.ReadPendingAsync()).Should().ContainSingle();
    }

    private sealed class IdentityKeys : ILocalMacroKeyStore
    {
        private readonly byte[] _key = Enumerable.Range(0, 32).Select(i => (byte)(i + 19)).ToArray();
        public byte[] GetKey(bool createIfMissing) => _key.ToArray();
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
