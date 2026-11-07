using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class VaultMetadataTests : IDisposable
{
    private readonly string _name = "zenith-vault-metadata-tests-" + Guid.NewGuid().ToString("N");
    private string Root => Path.Combine(Path.GetTempPath(), _name);
    private string VaultRoot => Path.Combine(Root, "accounts.vault");
    private static readonly byte[] Magic = "ZENITH-VAULT\n"u8.ToArray();
    private string RecordPath(Guid id) => Path.Combine(VaultRoot, id.ToString("N") + ".dpapi");
    private string GenerationPath(Guid id) => Path.Combine(VaultRoot, "generations", id.ToString("N") + ".json");
    private static AuthorizationRecord Record(Guid id, long generation = 1, long userId = 123, string? subject = null) => new(id,
        RobloxOAuthIdentity.Provider, userId, generation, "synthetic-access-never-persist", "synthetic-refresh-protected",
        DateTimeOffset.UtcNow.AddMinutes(10), "Synthetic_Account", Issuer: RobloxOAuthIdentity.Issuer, Subject: subject ?? userId.ToString());

    [Fact]
    public async Task AShortNonDeleteSharedReaderDoesNotLoseAnAllocatedGeneration()
    {
        using var vault=new ProtectedCredentialVault(Root);Guid id=Guid.NewGuid();
        Assert.Equal(1,await vault.AllocateNextGenerationAsync(id,0,default));
        using var reader=new FileStream(GenerationPath(id),FileMode.Open,FileAccess.Read,FileShare.Read);
        var next=vault.AllocateNextGenerationAsync(id,1,default);
        await Task.Delay(120);reader.Dispose();
        Assert.Equal(2,await next.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(3,await vault.AllocateNextGenerationAsync(id,2,default));
    }
    [Fact]
    public async Task CancellingABlockedReplacementPreservesTheOriginalAndAllowsAFreshAllocation()
    {
        using var vault=new ProtectedCredentialVault(Root);Guid id=Guid.NewGuid();
        await vault.AllocateNextGenerationAsync(id,0,default);byte[] before=await File.ReadAllBytesAsync(GenerationPath(id));
        using var reader=new FileStream(GenerationPath(id),FileMode.Open,FileAccess.Read,FileShare.Read);
        using var stop=new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>vault.AllocateNextGenerationAsync(id,1,stop.Token));
        Assert.Equal(before,await File.ReadAllBytesAsync(GenerationPath(id)));reader.Dispose();
        Assert.Equal(2,await vault.AllocateNextGenerationAsync(id,1,default));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(VaultRoot,"generations"),"*.tmp"));
    }
    [Fact]
    public async Task AccessTokenIsAbsentEvenInsideDecryptedDurablePayloadAndAllFilesExcludeSecretFixtures()
    {
        using var vault = new ProtectedCredentialVault(Root);
        var record = Record(Guid.NewGuid());
        await vault.SaveAsync(record, default);
        Assert.Equal(record with { AccessToken = "" }, await vault.ReadAsync(record.ProfileId, default));
        JsonObject envelope = ReadEnvelope(record.ProfileId);
        byte[] cipher = Convert.FromBase64String(envelope["ProtectedPayload"]!.GetValue<string>());
        byte[] entropy = Encoding.UTF8.GetBytes("ZenithSessions/Personal/roblox-oauth/v2/" + record.ProfileId.ToString("N"));
        byte[] clear = ProtectedData.Unprotect(cipher, entropy, DataProtectionScope.CurrentUser);
        try
        {
            using var payload = JsonDocument.Parse(clear);
            var durable = payload.RootElement.GetProperty("Authorization");
            Assert.Equal("", durable.GetProperty("AccessToken").GetString());
            Assert.Equal(record.RefreshToken, durable.GetProperty("RefreshToken").GetString());
            Assert.DoesNotContain(record.AccessToken, Encoding.UTF8.GetString(clear));
            foreach (string file in Directory.GetFiles(VaultRoot, "*", SearchOption.AllDirectories))
            {
                string contents = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
                Assert.DoesNotContain(record.AccessToken, contents);
                Assert.DoesNotContain(record.RefreshToken, contents);
            }
        }
        finally { CryptographicOperations.ZeroMemory(clear); CryptographicOperations.ZeroMemory(cipher); CryptographicOperations.ZeroMemory(entropy); }
        var metadata = await vault.GetSummaryAsync(record.ProfileId, default);
        Assert.NotNull(metadata);
        Assert.Equal(record.ProfileId.ToString("N"), metadata.CredentialRef);
        Assert.Equal(record.Issuer, metadata.Issuer);
        Assert.Equal(record.Subject, metadata.Subject);
        Assert.False(metadata.IsCurrentAuthorization);
        Assert.Contains("Last-known", metadata.EvidenceLabel);
    }

    [Fact]
    public async Task RequestedMetadataDoesNotDecryptPayloadsOrReadUnrelatedInventory()
    {
        using var vault = new ProtectedCredentialVault(Root);
        var a = Record(Guid.NewGuid());
        var b = Record(Guid.NewGuid(), userId: 456);
        await vault.SaveAsync(a, default);
        await vault.SaveAsync(b, default);
        JsonObject envelope = ReadEnvelope(a.ProfileId);
        // An invalid DPAPI payload with a matching public checksum proves summary reads do not
        // decrypt it. This deliberately demonstrates why the summary cannot authorize an action.
        byte[] invalidCipher = [1, 2, 3, 4];
        envelope["ProtectedPayload"] = Convert.ToBase64String(invalidCipher);
        envelope["PayloadDigest"] = Convert.ToHexString(SHA256.HashData(invalidCipher));
        WriteEnvelope(a.ProfileId, envelope);
        JsonObject unrelated = ReadEnvelope(b.ProfileId);
        unrelated["Metadata"] = null;
        WriteEnvelope(b.ProfileId, unrelated);
        var summary = Assert.Single(await vault.ReadSummariesAsync([a.ProfileId, Guid.NewGuid()], default));
        Assert.Equal(a.ProfileId, summary.ProfileId);
        Assert.False(summary.IsCurrentAuthorization);
        await Assert.ThrowsAsync<CryptographicException>(() => vault.ReadAsync(a.ProfileId, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => vault.ReadSummariesAsync(Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToArray(), default));
    }

    [Fact]
    public async Task MissingOrCorruptSummaryNeverFallsBackToUsableAuthorizationOrOverwritesOriginal()
    {
        using var vault = new ProtectedCredentialVault(Root);
        var record = Record(Guid.NewGuid());
        Assert.Null(await vault.GetSummaryAsync(record.ProfileId, default));
        await vault.SaveAsync(record, default);
        JsonObject envelope = ReadEnvelope(record.ProfileId);
        envelope["Metadata"] = null;
        WriteEnvelope(record.ProfileId, envelope);
        byte[] original = await File.ReadAllBytesAsync(RecordPath(record.ProfileId));
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.GetSummaryAsync(record.ProfileId, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.ReadAsync(record.ProfileId, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.SaveAsync(record with { Generation = 2 }, default));
        Assert.Equal(original, await File.ReadAllBytesAsync(RecordPath(record.ProfileId)));
    }

    [Fact]
    public async Task LegacyV1RequiresSignInAndOnlyFreshVerifiedContextExplicitlyMigratesIt()
    {
        var id = Guid.NewGuid();
        var legacy = new AuthorizationRecord(id, RobloxOAuthIdentity.Provider, 123, 1, "legacy-synthetic-access", "legacy-synthetic-refresh", DateTimeOffset.UtcNow.AddMinutes(10));
        Directory.CreateDirectory(VaultRoot);
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(legacy);
        byte[] entropy = Encoding.UTF8.GetBytes("ZenithSessions/Personal/roblox-oauth/v1/" + id.ToString("N"));
        byte[] cipher = ProtectedData.Protect(clear, entropy, DataProtectionScope.CurrentUser);
        try { await File.WriteAllBytesAsync(RecordPath(id), cipher); }
        finally { CryptographicOperations.ZeroMemory(clear); CryptographicOperations.ZeroMemory(entropy); }
        using var vault = new ProtectedCredentialVault(Root);
        var historical = await vault.ReadAsync(id, default);
        Assert.NotNull(historical);
        Assert.Equal(AuthorizationHealth.SignInRequired, historical.Health);
        Assert.Equal("", historical.AccessToken);
        Assert.Equal("", historical.RefreshToken);
        Assert.Equal("", historical.Issuer);
        Assert.Equal("", historical.Subject);
        Assert.Null(await vault.GetSummaryAsync(id, default));
        Assert.Equal(cipher, await File.ReadAllBytesAsync(RecordPath(id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.SaveAsync(legacy with { Generation = 2 }, default));
        await vault.SaveAsync(Record(id, 2), default);
        Assert.Equal(AuthorizationHealth.Linked, (await vault.ReadAsync(id, default))!.Health);
        Assert.NotNull(await vault.GetSummaryAsync(id, default));
        CryptographicOperations.ZeroMemory(cipher);
    }

    [Fact]
    public async Task UnsupportedVersionAndCorruptOriginalArePreservedWithoutBackupRestore()
    {
        using var vault = new ProtectedCredentialVault(Root);
        var record = Record(Guid.NewGuid());
        await vault.SaveAsync(record, default);
        byte[] valid = await File.ReadAllBytesAsync(RecordPath(record.ProfileId));
        JsonObject envelope = ReadEnvelope(record.ProfileId);
        envelope["Version"] = 99;
        WriteEnvelope(record.ProfileId, envelope);
        byte[] unsupported = await File.ReadAllBytesAsync(RecordPath(record.ProfileId));
        await File.WriteAllBytesAsync(RecordPath(record.ProfileId) + ".bak", valid);
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.ReadAsync(record.ProfileId, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.SaveAsync(record with { Generation = 2 }, default));
        Assert.Equal(unsupported, await File.ReadAllBytesAsync(RecordPath(record.ProfileId)));
        Assert.Equal(valid, await File.ReadAllBytesAsync(RecordPath(record.ProfileId) + ".bak"));
    }

    [Fact]
    public async Task RotationRemovesObsoleteAutomaticCopiesAndDoesNotCreateNewBackups()
    {
        using var vault = new ProtectedCredentialVault(Root);
        var record = Record(Guid.NewGuid());
        await vault.SaveAsync(record, default);
        string path = RecordPath(record.ProfileId);
        File.Copy(path, path + ".bak");
        File.Copy(path, path + ".tmp");
        await vault.SaveAsync(record with { Generation = 2, RefreshToken = "synthetic-rotated-refresh" }, default);
        Assert.False(File.Exists(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal("synthetic-rotated-refresh", (await vault.ReadAsync(record.ProfileId, default))!.RefreshToken);
        Assert.Empty(Directory.GetFiles(VaultRoot, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteRemovesCrashOrphanedAtomicWriteTemporariesButKeepsAnotherProfilesFiles(bool throughDisconnectSnapshot)
    {
        using var vault = new ProtectedCredentialVault(Root);
        var record = Record(Guid.NewGuid());
        var sibling = Record(Guid.NewGuid(), userId: 456);
        await vault.SaveAsync(record, default);
        await vault.SaveAsync(sibling, default);
        string path = RecordPath(record.ProfileId), siblingPath = RecordPath(sibling.ProfileId);
        var pointer = JsonNode.Parse(await File.ReadAllTextAsync(path + ".identity"))!.AsObject();
        string index = Path.Combine(VaultRoot, "identity-index");
        string[] claims = [Path.Combine(index, pointer["IdentityKey"]!.GetValue<string>() + ".json"), Path.Combine(index, pointer["UserKey"]!.GetValue<string>() + ".json")];
        // A process kill or power loss between creating and publishing an atomic write leaves
        // these exact names behind: the envelope, the pointer and both plaintext identity claims.
        foreach (string target in claims.Prepend(path + ".identity").Prepend(path))
            File.Copy(target, target + "." + Guid.NewGuid().ToString("N") + ".tmp");
        string siblingTemporary = siblingPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.Copy(siblingPath, siblingTemporary);
        static string[] Names(IEnumerable<string> files) => files.Select(file => Path.GetFileName(file)!).Order(StringComparer.Ordinal).ToArray();
        string[] siblingIndex = Names(Directory.GetFiles(index).Where(file => !claims.Contains(file) && !file.EndsWith(".tmp", StringComparison.Ordinal)));

        if (throughDisconnectSnapshot)
        {
            AuthorizationWriteSnapshot snapshot = await vault.ReadWriteSnapshotAsync(record.ProfileId, default);
            Assert.Equal(AuthorizationMutationStatus.Applied, (await vault.TryDeleteSnapshotAsync(snapshot, default)).Status);
        }
        else await vault.DeleteAsync(record.ProfileId, default);

        Assert.Equal(Names([siblingPath, siblingPath + ".identity", siblingTemporary, ProtectedCredentialVault.WriterLockFileName]), Names(Directory.GetFiles(VaultRoot)));
        Assert.Equal(siblingIndex, Names(Directory.GetFiles(index)));
        Assert.Equal(sibling with { AccessToken = "" }, await vault.ReadAsync(sibling.ProfileId, default));
    }

    [Fact]
    public async Task IdentityAndMappedUserDeduplicateAtomicallyAcrossVaultInstances()
    {
        using var a = new ProtectedCredentialVault(Root);
        using var b = new ProtectedCredentialVault(Root);
        var first = Record(Guid.NewGuid());
        var other = Record(Guid.NewGuid());
        Exception?[] results = await Task.WhenAll(Xunit.Record.ExceptionAsync(() => a.SaveAsync(first, default)), Xunit.Record.ExceptionAsync(() => b.SaveAsync(other, default)));
        Assert.Single(results, result => result is null);
        Assert.Single(results, result => result is InvalidOperationException);
        var identity = await a.FindByIdentityAsync(RobloxOAuthIdentity.Provider, RobloxOAuthIdentity.Issuer, "123", default);
        Assert.NotNull(identity);
        Assert.Contains(identity.ProfileId, new[] { first.ProfileId, other.ProfileId });
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.SaveAsync(Record(Guid.NewGuid(), subject: "different-opaque-subject"), default));
    }

    [Fact]
    public async Task InterruptedIdentityClaimBlocksDuplicatesUntilExplicitDisconnect()
    {
        using var vault = new ProtectedCredentialVault(Root);
        var first = Record(Guid.NewGuid());
        await vault.SaveAsync(first, default);
        File.Delete(RecordPath(first.ProfileId));
        Assert.Null(await vault.GetSummaryAsync(first.ProfileId, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.FindByIdentityAsync(first.Provider, first.Issuer, first.Subject, default));
        var other = Record(Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.SaveAsync(other, default));
        await vault.DeleteAsync(first.ProfileId, default);
        await vault.SaveAsync(other, default);
        Assert.Equal(other.ProfileId, (await vault.FindByIdentityAsync(other.Provider, other.Issuer, other.Subject, default))!.ProfileId);
    }

    [Theory]
    [InlineData("IdentityKey")]
    [InlineData("UserKey")]
    public async Task FailedClaimDeletionRetainsPointerAndRetryReleasesIdentityForAnotherProfile(string blockedKey)
    {
        using var vault = new ProtectedCredentialVault(Root);
        var record = Record(Guid.NewGuid());
        await vault.SaveAsync(record, default);
        string pointerPath = RecordPath(record.ProfileId) + ".identity";
        var pointer = JsonNode.Parse(await File.ReadAllTextAsync(pointerPath))!.AsObject();
        string identityClaim = Path.Combine(VaultRoot, "identity-index", pointer["IdentityKey"]!.GetValue<string>() + ".json");
        string userClaim = Path.Combine(VaultRoot, "identity-index", pointer["UserKey"]!.GetValue<string>() + ".json");
        string blockedClaim = blockedKey == "IdentityKey" ? identityClaim : userClaim;
        byte[] originalClaim = await File.ReadAllBytesAsync(blockedClaim);
        byte[] originalPointer = await File.ReadAllBytesAsync(pointerPath);
        FileAttributes originalAttributes = File.GetAttributes(blockedClaim);
        File.SetAttributes(blockedClaim, originalAttributes | FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.DeleteAsync(record.ProfileId, default));
            Assert.False(File.Exists(RecordPath(record.ProfileId)));
            Assert.Equal(originalPointer, await File.ReadAllBytesAsync(pointerPath));
            Assert.Equal(originalClaim, await File.ReadAllBytesAsync(blockedClaim));
            if (blockedKey == "UserKey") Assert.False(File.Exists(identityClaim));
            else Assert.True(File.Exists(userClaim));
        }
        finally
        {
            if (File.Exists(blockedClaim)) File.SetAttributes(blockedClaim, originalAttributes);
        }
        await vault.DeleteAsync(record.ProfileId, default);
        Assert.False(File.Exists(pointerPath));
        Assert.False(File.Exists(identityClaim));
        Assert.False(File.Exists(userClaim));
        Assert.Null(await vault.FindByIdentityAsync(record.Provider, record.Issuer, record.Subject, default));
        var replacement = Record(Guid.NewGuid());
        await vault.SaveAsync(replacement, default);
        Assert.Equal(replacement.ProfileId, (await vault.FindByIdentityAsync(replacement.Provider, replacement.Issuer, replacement.Subject, default))!.ProfileId);
    }

    [Fact]
    public async Task GenerationsSurviveDeleteAndReopenAndCannotReviveStaleQueuedBindings()
    {
        var id = Guid.NewGuid();
        using (var vault = new ProtectedCredentialVault(Root))
        {
            long initial = await vault.AllocateNextGenerationAsync(id, 0, default);
            Assert.Equal(1, initial);
            await vault.SaveAsync(Record(id, initial), default);
            await vault.DeleteAsync(id, default);
            Assert.Null(await vault.ReadAsync(id, default));
            Assert.Null(await vault.GetSummaryAsync(id, default));
            Assert.True(File.Exists(GenerationPath(id)));
        }
        using var reopened = new ProtectedCredentialVault(Root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.SaveAsync(Record(id, 1), default));
        long relink = await reopened.AllocateNextGenerationAsync(id, 0, default);
        Assert.True(relink > 1);
        await reopened.SaveAsync(Record(id, relink), default);
        Assert.Equal(relink, (await reopened.ReadAsync(id, default))!.Generation);
    }

    [Fact]
    public async Task AllocationImmediatelyFencesOldPayloadAndCanceledWorkDoesNotReuseItsGeneration()
    {
        using var vault = new ProtectedCredentialVault(Root);
        var record = Record(Guid.NewGuid());
        await vault.SaveAsync(record, default);
        byte[] original = await File.ReadAllBytesAsync(RecordPath(record.ProfileId));
        long reserved = await vault.AllocateNextGenerationAsync(record.ProfileId, record.Generation, default);
        var fenced = await vault.ReadAsync(record.ProfileId, default);
        Assert.Equal(AuthorizationHealth.SignInRequired, fenced!.Health);
        Assert.Equal("", fenced.RefreshToken);
        Assert.Equal("", fenced.AccessToken);
        Assert.Equal(AuthorizationHealth.SignInRequired, (await vault.GetSummaryAsync(record.ProfileId, default))!.Health);
        Assert.Equal(original, await File.ReadAllBytesAsync(RecordPath(record.ProfileId)));
        long later = await vault.AllocateNextGenerationAsync(record.ProfileId, 0, default);
        Assert.True(later > reserved);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.SaveAsync(record with { Generation = reserved }, default));
        await vault.SaveAsync(record with { Generation = later }, default);
        Assert.Equal(AuthorizationHealth.Linked, (await vault.ReadAsync(record.ProfileId, default))!.Health);
    }

    [Fact]
    public async Task IncompleteCommitOrCorruptTombstoneCannotAdvertiseUsableAuthorization()
    {
        using var vault = new ProtectedCredentialVault(Root);
        var record = Record(Guid.NewGuid());
        await vault.SaveAsync(record, default);
        string generationPath = GenerationPath(record.ProfileId);
        var state = JsonNode.Parse(await File.ReadAllTextAsync(generationPath))!.AsObject();
        state["LastCommitted"] = 0;
        await File.WriteAllTextAsync(generationPath, state.ToJsonString());
        Assert.Equal(AuthorizationHealth.SignInRequired, (await vault.ReadAsync(record.ProfileId, default))!.Health);
        Assert.Equal(AuthorizationHealth.SignInRequired, (await vault.GetSummaryAsync(record.ProfileId, default))!.Health);
        await File.WriteAllTextAsync(generationPath, "corrupt-generation-marker");
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.ReadAsync(record.ProfileId, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.AllocateNextGenerationAsync(record.ProfileId, 0, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.SaveAsync(record with { Generation = 2 }, default));
        Assert.Equal("corrupt-generation-marker", await File.ReadAllTextAsync(generationPath));
    }

    [Fact]
    public async Task CanceledSavePreservesCurrentRecordAndFailedPublicationCreatesNoUsableSummary()
    {
        using var vault = new ProtectedCredentialVault(Root);
        var record = Record(Guid.NewGuid());
        await vault.SaveAsync(record, default);
        byte[] original = await File.ReadAllBytesAsync(RecordPath(record.ProfileId));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.SaveAsync(record with { Generation = 2 }, canceled.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(RecordPath(record.ProfileId)));
        Assert.Equal(AuthorizationHealth.Linked, (await vault.ReadAsync(record.ProfileId, default))!.Health);
        var failed = Record(Guid.NewGuid(), userId: 456);
        Directory.CreateDirectory(RecordPath(failed.ProfileId) + ".identity");
        await Assert.ThrowsAnyAsync<IOException>(() => vault.SaveAsync(failed, default));
        Assert.Null(await vault.GetSummaryAsync(failed.ProfileId, default));
        Assert.Null(await vault.ReadAsync(failed.ProfileId, default));
        Assert.Empty(Directory.GetFiles(VaultRoot, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task VaultDirectoriesAndFilesHaveProtectedCurrentUserAndSystemOnlyAcls()
    {
        using var vault = new ProtectedCredentialVault(Root);
        await vault.SaveAsync(Record(Guid.NewGuid()), default);
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User!;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        foreach (string directory in Directory.GetDirectories(VaultRoot, "*", SearchOption.AllDirectories).Prepend(VaultRoot))
        {
            DirectorySecurity acl = new DirectoryInfo(directory).GetAccessControl();
            Assert.True(acl.AreAccessRulesProtected);
            Assert.All(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), rule =>
            { Assert.True(rule.IdentityReference.Equals(currentUser) || rule.IdentityReference.Equals(system)); Assert.Equal(AccessControlType.Allow, rule.AccessControlType); });
        }
        foreach (string file in Directory.GetFiles(VaultRoot, "*", SearchOption.AllDirectories))
        {
            FileSecurity acl = new FileInfo(file).GetAccessControl();
            Assert.True(acl.AreAccessRulesProtected);
            Assert.All(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), rule =>
            { Assert.True(rule.IdentityReference.Equals(currentUser) || rule.IdentityReference.Equals(system)); Assert.Equal(AccessControlType.Allow, rule.AccessControlType); });
        }
    }

    [Theory]
    [InlineData(nameof(ProtectedCredentialVault.ReadSummariesAsync))]
    [InlineData(nameof(ProtectedCredentialVault.GetSummaryAsync))]
    [InlineData(nameof(ProtectedCredentialVault.ReadAsync))]
    [InlineData(nameof(ProtectedCredentialVault.FindByIdentityAsync))]
    [InlineData(nameof(ProtectedCredentialVault.FindByRobloxUserIdAsync))]
    [InlineData(nameof(ProtectedCredentialVault.AllocateNextGenerationAsync))]
    [InlineData(nameof(ProtectedCredentialVault.SaveAsync))]
    [InlineData(nameof(ProtectedCredentialVault.DeleteAsync))]
    [InlineData(nameof(ProtectedCredentialVault.TryReplaceNextAsync))]
    [InlineData(nameof(ProtectedCredentialVault.TryCommitSnapshotAsync))]
    [InlineData(nameof(ProtectedCredentialVault.TryDeleteSnapshotAsync))]
    [InlineData(nameof(ProtectedCredentialVault.ReadWriteSnapshotAsync))]
    [InlineData(nameof(ProtectedCredentialVault.TryFenceOwnedMutationAsync))]
    [InlineData(nameof(ProtectedCredentialVault.TryDispatchCurrentAsync))]
    public async Task VaultOperationChecksPrivateDirectoriesOncePerCall(string operation)
    {
        using var vault = new ProtectedCredentialVault(Root);
        var record = Record(Guid.NewGuid());
        var other = Record(Guid.NewGuid(), userId: 456);
        await vault.SaveAsync(record, default);
        await vault.SaveAsync(other, default);
        AuthorizationRecord stored = record with { AccessToken = "" };
        AuthorizationWriteSnapshot snapshot = await vault.ReadWriteSnapshotAsync(record.ProfileId, default);
        int before = vault.PrivateDirectoryChecks;
        Task call = operation switch
        {
            nameof(vault.ReadSummariesAsync) => vault.ReadSummariesAsync([record.ProfileId, other.ProfileId], default),
            nameof(vault.GetSummaryAsync) => vault.GetSummaryAsync(record.ProfileId, default),
            nameof(vault.ReadAsync) => vault.ReadAsync(record.ProfileId, default),
            nameof(vault.FindByIdentityAsync) => vault.FindByIdentityAsync(record.Provider, record.Issuer, record.Subject, default),
            nameof(vault.FindByRobloxUserIdAsync) => vault.FindByRobloxUserIdAsync(record.VerifiedUserId, default),
            nameof(vault.AllocateNextGenerationAsync) => vault.AllocateNextGenerationAsync(record.ProfileId, 0, default),
            nameof(vault.SaveAsync) => vault.SaveAsync(record with { Generation = 2 }, default),
            nameof(vault.DeleteAsync) => vault.DeleteAsync(record.ProfileId, default),
            nameof(vault.TryReplaceNextAsync) => vault.TryReplaceNextAsync(stored, stored with { RefreshToken = "synthetic-rotated-refresh" }, Guid.NewGuid(), default),
            nameof(vault.TryCommitSnapshotAsync) => vault.TryCommitSnapshotAsync(snapshot, record with { Generation = snapshot.NextGeneration }, Guid.NewGuid(), default),
            nameof(vault.TryDeleteSnapshotAsync) => vault.TryDeleteSnapshotAsync(snapshot, default),
            nameof(vault.ReadWriteSnapshotAsync) => vault.ReadWriteSnapshotAsync(record.ProfileId, default),
            nameof(vault.TryFenceOwnedMutationAsync) => vault.TryFenceOwnedMutationAsync(record.ProfileId, Guid.NewGuid(), default),
            nameof(vault.TryDispatchCurrentAsync) => vault.TryDispatchCurrentAsync(snapshot, () => throw new InvalidOperationException("A synthetic OAuth record must not dispatch."), default),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        await call;
        Assert.Equal(1, vault.PrivateDirectoryChecks - before);
    }

    [Theory]
    [InlineData(nameof(ProtectedCredentialVault.ReadWriteSnapshotAsync))]
    [InlineData(nameof(ProtectedCredentialVault.ReadSummariesAsync))]
    public async Task DirectoryLoosenedWhileWaitingForTheLeaseIsRepairedBeforeTheOperationReads(string operation)
    {
        using var vault = new ProtectedCredentialVault(Root);
        var record = Record(Guid.NewGuid());
        await vault.SaveAsync(record, default);
        var index = new DirectoryInfo(Path.Combine(VaultRoot, "identity-index"));
        Task call;
        // Another holder owns the coordination file, so the operation waits for the lease.
        using (var held = new FileStream(Path.Combine(VaultRoot, ProtectedCredentialVault.WriterLockFileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            call = operation == nameof(vault.ReadWriteSnapshotAsync)
                ? vault.ReadWriteSnapshotAsync(record.ProfileId, default)
                : vault.ReadSummariesAsync([record.ProfileId], default);
            await Task.Delay(300);
            Assert.False(call.IsCompleted);
            // Inheriting from the private vault directory grants nobody new, but is no longer an
            // explicit protected ACL, so the check must repair it.
            DirectorySecurity loosened = index.GetAccessControl();
            loosened.SetAccessRuleProtection(false, true);
            index.SetAccessControl(loosened);
            Assert.False(index.GetAccessControl().AreAccessRulesProtected);
        }
        await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(index.GetAccessControl().AreAccessRulesProtected);
    }

    private JsonObject ReadEnvelope(Guid id) => JsonNode.Parse(File.ReadAllBytes(RecordPath(id)).AsSpan(Magic.Length))!.AsObject();
    private void WriteEnvelope(Guid id, JsonObject envelope)
    {
        byte[] json = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        byte[] bytes = new byte[Magic.Length + json.Length];
        Magic.CopyTo(bytes, 0);
        json.CopyTo(bytes, Magic.Length);
        File.WriteAllBytes(RecordPath(id), bytes);
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(Root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || Path.GetFileName(full) != _name)
            throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
