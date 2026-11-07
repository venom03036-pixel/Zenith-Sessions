using FluentAssertions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using MacroSuite.Infrastructure.Storage;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Storage;

public sealed class RecordingCommittedAliasRecoveryTests
{
    [Fact]
    public async Task AlreadySavedDescriptiveDocument_WithPendingCommittedJournal_IsCleanedWithoutAppendingAgain()
    {
        string root = Path.Combine(Path.GetTempPath(), "zenith-committed-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var keys = new CommittedAliasKeys();
            var originalStore = new ProtectedMacroStore(root, keys);
            var baseline = new MacroModel { Name = "Existing saved recording", Settings = new() { Target = null } };
            string originalPath = Path.Combine(root, "New Macro_" + baseline.Id.ToString("N") + ".zmacro");
            originalStore.Save(baseline, originalPath);
            Guid sessionId = Guid.NewGuid();
            await using (var session = await originalStore.RecordingRecovery.BeginAsync(new(sessionId, baseline, 0)))
            {
                session.TryAppend(new(1, 0, InputEventType.KeyDown, 0, 0, 87)).Should().BeTrue();
                session.TryAppend(new(2, TimeSpan.TicksPerMillisecond * 10, InputEventType.KeyUp, 0, 0, 87)).Should().BeTrue();
                var saved = new MacroModel {
                    Id = baseline.Id, Name = baseline.Name, CreatedAt = baseline.CreatedAt, Settings = baseline.Settings,
                    RecordingCommit = session.CreateCapturedCommitMetadata(2),
                    Steps = new[] { new KeyPressStep(87), new KeyPressStep(87) } };
                await session.PrepareCommitAsync(saved);
                originalStore.Save(saved, originalPath);
                // Old releases failed their subsequent verification because this filename
                // was descriptive. Preserve exactly that saved-model/pending-journal state.
            }
            byte[] encryptedSavedDocument = await File.ReadAllBytesAsync(originalPath);
            var updatedStore = new ProtectedMacroStore(root, keys);
            var pending = (await updatedStore.RecordingRecovery.ReadPendingAsync()).Should().ContainSingle().Subject;
            pending.SessionId.Should().Be(sessionId);
            pending.ExpectedResultFingerprint.Should().NotBeNull();
            (await updatedStore.RecordingRecovery.RecoverAsync(pending)).Should().BeNull("the accepted recording is already saved");
            (await updatedStore.RecordingRecovery.ReadPendingAsync()).Should().BeEmpty();
            (await File.ReadAllBytesAsync(originalPath)).Should().Equal(encryptedSavedDocument, "cleaning a committed journal must not rewrite its macro");
            updatedStore.Load(originalPath).Model.Steps.Should().HaveCount(2);
            File.Exists(updatedStore.GetLibraryPath(baseline.Id)).Should().BeFalse("the existing descriptive filename remains authoritative");
            Directory.GetFiles(root, "*.zmacro").Should().ContainSingle();
            (await updatedStore.RecordingRecovery.RecoverAsync(pending)).Should().BeNull();
            updatedStore.Load(originalPath).Model.Steps.Should().HaveCount(2);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class CommittedAliasKeys : ILocalMacroKeyStore
    {
        private readonly byte[] _key = Enumerable.Range(0, 32).Select(i => (byte)(i + 23)).ToArray();
        public byte[] GetKey(bool createIfMissing) => _key.ToArray();
    }
}
