using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// Current-user DPAPI records with atomic, versioned, last-known identity envelopes. All live vault
/// instances for a directory share one writer. Callers cancel/await operations before disposing.
/// Metadata reads never decrypt credentials. There is no token cache or automatic backup restore.
/// </summary>
public sealed class ProtectedCredentialVault : IAccountCredentialVault, IAuthorizationMetadataReader, IAuthorizationGenerationStore, IRobloxAuthorizationIdentityReader, ISnapshotAuthorizationVault, IAuthorizationDispatchVault, IDisposable
{
    public const string WriterLockFileName = ".writer.lock";
    public const int MaximumPendingOperations = 4;
    private const int Version = 2, MaximumFileBytes = 128 * 1024, MaximumIndexBytes = 8192, MaximumOrphanedTemporaries = 64;
    private static readonly byte[] Magic = "ZENITH-VAULT\n"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 8 };
    private static readonly (string Provider, string Issuer)[] RobloxContexts =
    [
        (RobloxOAuthIdentity.Provider, RobloxOAuthIdentity.Issuer),
        (RobloxSessionIdentity.Provider, RobloxSessionIdentity.Issuer)
    ];
    private static readonly object RegistryGate = new();
    private static readonly Dictionary<string, SharedWriter> Writers = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _directory, _indexDirectory, _generationDirectory;
    private readonly SharedWriter _writer;
    private readonly object _lifetime = new();
    private int _activeCalls;
    private bool _disposed;

    public ProtectedCredentialVault(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _directory = Path.GetFullPath(Path.Combine(root, "accounts.vault"));
        _indexDirectory = Path.Combine(_directory, "identity-index");
        _generationDirectory = Path.Combine(_directory, "generations");
        lock (RegistryGate)
        {
            if (!Writers.TryGetValue(_directory, out SharedWriter? writer)) Writers.Add(_directory, writer = new());
            _writer = writer;
            _writer.Owners++;
        }
    }

    public async Task<long> AllocateNextGenerationAsync(Guid profileId, long minimumExclusive, CancellationToken ct)
    {
        PathFor(profileId);
        if (minimumExclusive < 0) throw new ArgumentOutOfRangeException(nameof(minimumExclusive));
        using var lease = await EnterAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        EnsurePrivateDirectories();
        GenerationState state = ReadGeneration(profileId);
        long next = NextGeneration(Math.Max(minimumExclusive, state.HighestAllocated));
        await WriteGenerationAsync(state with { HighestAllocated = next, HighestMutationId = Guid.Empty }, ct).ConfigureAwait(false);
        return next;
    }

    public async Task SaveAsync(AuthorizationRecord record, CancellationToken ct)
    {
        AuthorizationRecord durable = DurableRecord(record);
        using var lease = await EnterAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        await SaveOwnedAsync(durable, Guid.Empty, ct).ConfigureAwait(false);
    }

    private static AuthorizationRecord DurableRecord(AuthorizationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        // Strip operational access material before any serialization or persistent indexing.
        AuthorizationRecord durable = record.Provider == RobloxSessionIdentity.Provider
            ? record.Validated() : (record with { AccessToken = "" }).Validated();
        if (!AuthorizationIdentity.HasVerifiedContext(durable.Provider, durable.Issuer, durable.Subject, durable.VerifiedUserId))
            throw new InvalidOperationException("Fresh verified identity context is required before saving authorization. Legacy records require sign-in.");
        return durable;
    }

    // Called only while this instance owns both the in-process and process file lease. The
    // conditional caller supplies its exact reservation owner; ordinary Save cannot adopt it.
    private async Task SaveOwnedAsync(AuthorizationRecord durable, Guid mutationId, CancellationToken ct)
    {
        string path = PathFor(durable.ProfileId);
        byte[] clear = [], cipher = [], envelopeBytes = [], entropy = Entropy(durable.ProfileId, Version);
        try
        {
            EnsurePrivateDirectories();
            if (File.Exists(path))
            {
                AuthorizationRecord existing = ReadProtected(path, durable.ProfileId, out _);
                if (durable.Generation <= existing.Generation || durable.VerifiedUserId != existing.VerifiedUserId
                    || existing.Issuer.Length != 0 && (durable.Provider != existing.Provider || durable.Issuer != existing.Issuer || durable.Subject != existing.Subject))
                    throw new InvalidOperationException("Authorization generation or contextual identity changed unexpectedly. Reconnect the saved identity explicitly.");
            }
            else if (File.Exists(path + ".bak"))
                throw new InvalidDataException("An obsolete authorization backup exists without its current record. Preserve it for explicit recovery; it will not be restored or reused.");

            GenerationState generation = ReadGeneration(durable.ProfileId);
            if (durable.Generation < generation.HighestAllocated || durable.Generation <= generation.LastCommitted)
                throw new InvalidOperationException("This authorization generation is stale. Sign in again; an earlier generation cannot be reused.");
            if (durable.Generation == generation.HighestAllocated && generation.HighestMutationId != Guid.Empty
                && generation.HighestMutationId != mutationId)
                throw new InvalidOperationException("This authorization generation belongs to another conditional operation. It cannot be reused by an ordinary save.");
            if (mutationId != Guid.Empty && (generation.HighestMutationId != mutationId || durable.Generation != generation.HighestAllocated))
                throw new InvalidOperationException("The conditional authorization reservation is no longer owned by this operation.");
            var pointer = PointerFor(durable);
            if (ReadPointer(durable.ProfileId) is { } previous && previous != pointer) throw IncompleteIdentity();
            ValidateAvailableClaim(pointer.IdentityKey, durable);
            ValidateAvailableUserClaims(durable);
            // Reserve first. Interruption after this point fences older material even if the new
            // credential or its commit marker cannot be published.
            if (durable.Generation > generation.HighestAllocated)
            {
                generation = generation with { HighestAllocated = durable.Generation, HighestMutationId = Guid.Empty };
                await WriteGenerationAsync(generation, ct).ConfigureAwait(false);
            }
            DeleteObsoleteCopies(path);
            string credentialRef = durable.ProfileId.ToString("N");
            DateTimeOffset savedUtc = DateTimeOffset.UtcNow;
            clear = JsonSerializer.SerializeToUtf8Bytes(new ProtectedPayload(Version, credentialRef, durable, savedUtc), JsonOptions);
            if (clear.Length > 65536) throw InvalidRecord();
            cipher = ProtectedData.Protect(clear, entropy, DataProtectionScope.CurrentUser);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new VaultEnvelope(Version, MetadataFor(durable, credentialRef, savedUtc), Digest(cipher), cipher), JsonOptions);
            envelopeBytes = new byte[Magic.Length + json.Length];
            Magic.CopyTo(envelopeBytes, 0);
            json.CopyTo(envelopeBytes, Magic.Length);
            if (envelopeBytes.Length > MaximumFileBytes) throw InvalidRecord();
            // Claims reserve identity only; a claim without a complete credential fails closed.
            await AtomicWriteAsync(PointerPath(durable.ProfileId), JsonSerializer.SerializeToUtf8Bytes(pointer, JsonOptions), ct).ConfigureAwait(false);
            await ReserveClaimAsync(pointer.IdentityKey, durable, ct).ConfigureAwait(false);
            await ReserveClaimAsync(pointer.UserKey, durable, ct).ConfigureAwait(false);
            // Summary publication is atomic with its durably written encrypted payload.
            await AtomicWriteAsync(path, envelopeBytes, ct).ConfigureAwait(false);
            await WriteGenerationAsync(generation with { LastCommitted = durable.Generation }, ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(cipher);
            CryptographicOperations.ZeroMemory(envelopeBytes);
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    public async Task<AuthorizationMutationResult> TryReplaceNextAsync(AuthorizationRecord expected, AuthorizationRecord replacement, Guid mutationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(replacement);
        if (mutationId == Guid.Empty) throw new ArgumentException("A fresh conditional mutation identity is required.");
        expected = expected.Validated(); replacement = DurableRecord(replacement);
        if (replacement.Generation != expected.Generation || !SameContext(expected, replacement)
            || !AuthorizationIdentity.HasVerifiedContext(expected.Provider, expected.Issuer, expected.Subject, expected.VerifiedUserId))
            throw new ArgumentException("Conditional authorization templates must have the same profile, contextual identity and expected generation.");
        string path = PathFor(expected.ProfileId);
        using var lease = await EnterAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        GenerationState state = ReadGeneration(expected.ProfileId);
        if (state.HighestAllocated != expected.Generation || state.LastCommitted != expected.Generation || !File.Exists(path))
            return new(AuthorizationMutationStatus.Conflict);
        AuthorizationRecord current = ReadProtected(path, expected.ProfileId, out bool legacy);
        if (legacy || current != expected) return new(AuthorizationMutationStatus.Conflict);
        ValidateClaims(MetadataFor(current, current.ProfileId.ToString("N"), default));
        if (state.HighestMutationId == mutationId) throw new ArgumentException("Use a fresh conditional mutation identity for each operation.");
        long next = NextGeneration(state.HighestAllocated);
        // Reserve ownership durably before any credential/index writes. An interrupted commit
        // remains fenced and only this opaque operation ID can conditionally invalidate it.
        await WriteGenerationAsync(state with { HighestAllocated = next, HighestMutationId = mutationId }, ct).ConfigureAwait(false);
        await SaveOwnedAsync(replacement with { Generation = next }, mutationId, ct).ConfigureAwait(false);
        return new(AuthorizationMutationStatus.Applied, next);
    }

    public async Task<bool> TryFenceOwnedMutationAsync(Guid profileId, Guid mutationId, CancellationToken ct)
    {
        PathFor(profileId);
        if (mutationId == Guid.Empty) throw new ArgumentException("A conditional mutation identity is required.");
        using var lease = await EnterAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        GenerationState state = ReadGeneration(profileId);
        if (state.HighestMutationId != mutationId) return false;
        await WriteGenerationAsync(state with { HighestAllocated = NextGeneration(state.HighestAllocated), HighestMutationId = Guid.Empty }, ct).ConfigureAwait(false);
        return true;
    }
    private static bool SameContext(AuthorizationRecord left, AuthorizationRecord right) => left.ProfileId == right.ProfileId
        && left.Provider == right.Provider && left.Issuer == right.Issuer && left.Subject == right.Subject && left.VerifiedUserId == right.VerifiedUserId;

    public async Task<AuthorizationWriteSnapshot> ReadWriteSnapshotAsync(Guid profileId, CancellationToken ct)
    {
        string path = PathFor(profileId);
        using var lease = await EnterAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        ct.ThrowIfCancellationRequested();
        GenerationState state = ReadGeneration(profileId);
        byte[]? bytes = ReadSnapshotBytes(path);
        try
        {
            if (bytes is null && File.Exists(path + ".bak"))
                throw new InvalidDataException("An obsolete authorization backup exists without its current record. Preserve it for explicit recovery; it will not be restored or reused.");
            AuthorizationWriteSnapshot snapshot = SnapshotFromBytes(profileId, state, bytes);
            ct.ThrowIfCancellationRequested();
            return snapshot;
        }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    public async Task<AuthorizationMutationResult> TryCommitSnapshotAsync(AuthorizationWriteSnapshot expected,
        AuthorizationRecord replacement, Guid mutationId, CancellationToken ct)
    {
        ValidateSnapshot(expected);
        AuthorizationRecord durable = DurableRecord(replacement);
        if (mutationId == Guid.Empty) throw new ArgumentException("A fresh conditional mutation identity is required.");
        if (durable.ProfileId != expected.ProfileId || durable.Generation != expected.NextGeneration
            || expected.Record is { } previous && (previous.VerifiedUserId != durable.VerifiedUserId
                || previous.Issuer.Length != 0 && !SameContext(previous, durable)))
            throw new ArgumentException("The snapshot replacement must retain its selected profile and verified contextual identity at the next generation.");
        using var lease = await EnterAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (!MatchesSnapshotOwned(expected, ct)) return new(AuthorizationMutationStatus.Conflict);
        if (expected.HighestMutationId == mutationId) throw new ArgumentException("Use a fresh conditional mutation identity for each operation.");
        // Validate known identity claims before reserving anything. A failed comparison or an
        // incompatible identity must not fence the current owner's otherwise valid authority.
        ValidateSaveClaims(durable);
        ct.ThrowIfCancellationRequested();
        long next = expected.NextGeneration;
        var reserved = new GenerationState(2, expected.ProfileId, next, expected.LastCommitted, mutationId);
        await WriteGenerationAsync(reserved, ct).ConfigureAwait(false);
        await SaveOwnedAsync(durable, mutationId, ct).ConfigureAwait(false);
        return new(AuthorizationMutationStatus.Applied, next);
    }

    public async Task<AuthorizationMutationResult> TryDeleteSnapshotAsync(AuthorizationWriteSnapshot expected, CancellationToken ct)
    {
        ValidateSnapshot(expected);
        using var lease = await EnterAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (!MatchesSnapshotOwned(expected, ct)) return new(AuthorizationMutationStatus.Conflict);
        long next = expected.NextGeneration;
        await DeleteOwnedAsync(expected.ProfileId, ct, next).ConfigureAwait(false);
        return new(AuthorizationMutationStatus.Applied, next);
    }

    public async Task<AuthorizationDispatchResult> TryDispatchCurrentAsync(AuthorizationWriteSnapshot expected,
        Func<NativeHandoffDispatchResult> dispatch, CancellationToken ct)
    {
        ValidateSnapshot(expected); ArgumentNullException.ThrowIfNull(dispatch);
        using var lease = await EnterAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (!MatchesSnapshotOwned(expected, ct) || expected.Record is not { } current
            || current.Provider != RobloxSessionIdentity.Provider || current.Health != AuthorizationHealth.Linked
            || !AuthorizationIdentity.HasVerifiedContext(current.Provider, current.Issuer, current.Subject, current.VerifiedUserId)
            || current.VerifiedUserId <= 0 || current.Generation != expected.HighestAllocated || current.Generation != expected.LastCommitted
            || current.ExpiresUtc <= DateTimeOffset.UtcNow)
            return new(AuthorizationDispatchStatus.NotAuthorized);
        ct.ThrowIfCancellationRequested();
        NativeHandoffDispatchResult? result;
        try { result = dispatch(); }
        catch (Exception)
        {
            // A native exception can contain a launch URI/ticket. Do not forward/log it or
            // guess that startup was prevented. The real synchronous owner has now returned.
            result = null;
        }
        // Do not check cancellation here: an actual late result must survive cancellation.
        // A valid receipt is retained unchanged, even alongside an inconsistent outcome.
        return new(AuthorizationDispatchStatus.CallbackInvoked, NativeHandoffDispatchResult.Sanitize(result));
    }

    private static void ValidateSnapshot(AuthorizationWriteSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ProfileId == Guid.Empty || snapshot.HighestAllocated < 0 || snapshot.LastCommitted < 0
            || snapshot.LastCommitted > snapshot.HighestAllocated || snapshot.HighestAllocated == 0 && snapshot.HighestMutationId != Guid.Empty
            || snapshot.Record is null && snapshot.RecordSha256 is not null
            || snapshot.Record is not null && (snapshot.Record.ProfileId != snapshot.ProfileId
                || snapshot.RecordSha256 is not { Length: 64 } || snapshot.RecordSha256.AsSpan().IndexOfAnyExcept("0123456789ABCDEF") >= 0))
            throw new ArgumentException("The authorization comparison snapshot is invalid.");
        if (snapshot.Record is { } record && record.Validated() != record)
            throw new ArgumentException("The authorization comparison snapshot is invalid.");
    }

    // All helpers below run under the actual process lease. Comparison does not repair ACLs,
    // delete obsolete copies, rewrite a credential or decrypt another profile's inventory.
    private bool MatchesSnapshotOwned(AuthorizationWriteSnapshot expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        GenerationState state = ReadGeneration(expected.ProfileId);
        if (state.HighestAllocated != expected.HighestAllocated || state.LastCommitted != expected.LastCommitted
            || state.HighestMutationId != expected.HighestMutationId) return false;
        string path = PathFor(expected.ProfileId);
        byte[]? bytes = ReadSnapshotBytes(path);
        try
        {
            if (bytes is null)
            {
                if (expected.Record is not null || File.Exists(path + ".bak")) return false;
            }
            else if (expected.Record is null || Digest(bytes) != expected.RecordSha256) return false;
            AuthorizationWriteSnapshot current = SnapshotFromBytes(expected.ProfileId, state, bytes);
            ct.ThrowIfCancellationRequested();
            return current.Record == expected.Record;
        }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    private AuthorizationWriteSnapshot SnapshotFromBytes(Guid profileId, GenerationState state, byte[]? bytes)
    {
        if (bytes is null)
        {
            ValidateAbsentSnapshotClaims(profileId, state);
            return new(profileId, null, state.HighestAllocated, state.LastCommitted, state.HighestMutationId, null);
        }
        string hash = Digest(bytes);
        AuthorizationRecord record = ReadProtectedBytes(bytes, profileId, out bool legacy);
        if (!legacy) ValidateClaims(MetadataFor(record, profileId.ToString("N"), default));
        if (legacy || record.Generation != state.HighestAllocated || record.Generation != state.LastCommitted)
            record = record with { AccessToken = "", RefreshToken = "", SessionCookie = "", Health = AuthorizationHealth.SignInRequired };
        return new(profileId, record, state.HighestAllocated, state.LastCommitted, state.HighestMutationId, hash);
    }

    private void ValidateAbsentSnapshotClaims(Guid profileId, GenerationState state)
    {
        ProfilePointer? pointer = ReadPointer(profileId);
        // A pending generation can explain a create/delete interrupted between owned writes.
        // A fully committed missing record is ambiguous and is not a fresh sign-in slot.
        // Only an untouched 0/0 slot without a pointer is fresh absent state.
        if (state.HighestAllocated == state.LastCommitted && (state.HighestAllocated > 0 || pointer is not null)) throw IncompleteIdentity();
        if (pointer is null) return;
        IdentityClaim? first = ReadClaim(pointer.IdentityKey), second = ReadClaim(pointer.UserKey);
        if (first is not null && second is not null && first != second) throw IncompleteIdentity();
        foreach (IdentityClaim? claim in new[] { first, second })
            if (claim is not null && (claim.ProfileId != profileId
                || IdentityKey(claim.Provider, claim.Issuer, claim.Subject) != pointer.IdentityKey
                || UserKey(claim.Provider, claim.Issuer, claim.VerifiedUserId) != pointer.UserKey)) throw IncompleteIdentity();
    }

    private static byte[]? ReadSnapshotBytes(string path)
    {
        try { return ReadPrivateBytes(path, MaximumFileBytes); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private void ValidateSaveClaims(AuthorizationRecord durable)
    {
        ProfilePointer pointer = PointerFor(durable);
        if (ReadPointer(durable.ProfileId) is { } previous && previous != pointer) throw IncompleteIdentity();
        ValidateAvailableClaim(pointer.IdentityKey, durable);
        ValidateAvailableUserClaims(durable);
    }

    public async Task<AuthorizationRecord?> ReadAsync(Guid profileId, CancellationToken ct)
    {
        string path = PathFor(profileId);
        using var lease = await EnterAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        EnsurePrivateDirectories();
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return null;
        AuthorizationRecord record = ReadProtected(path, profileId, out bool legacy);
        if (!legacy) ValidateClaims(MetadataFor(record, profileId.ToString("N"), default));
        GenerationState generation = ReadGeneration(profileId);
        if (legacy || record.Generation != generation.HighestAllocated || record.Generation != generation.LastCommitted)
            record = record with { AccessToken = "", RefreshToken = "", SessionCookie = "", Health = AuthorizationHealth.SignInRequired };
        ct.ThrowIfCancellationRequested();
        DeleteObsoleteCopies(path);
        return record;
    }

    public async Task<AuthorizationMetadata?> GetSummaryAsync(Guid profileId, CancellationToken ct)
    {
        PathFor(profileId);
        using var lease = await EnterAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        EnsurePrivateDirectories();
        ct.ThrowIfCancellationRequested();
        return ReadSummary(profileId);
    }

    public async Task<IReadOnlyList<AuthorizationMetadata>> ReadSummariesAsync(IReadOnlyList<Guid> profileIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profileIds);
        if (profileIds.Count > 200) throw new ArgumentOutOfRangeException(nameof(profileIds), "Request at most 200 visible profile summaries.");
        foreach (Guid id in profileIds) PathFor(id);
        using var lease = await EnterAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        EnsurePrivateDirectories();
        var summaries = new List<AuthorizationMetadata>(profileIds.Count);
        foreach (Guid id in profileIds.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            if (ReadSummary(id) is { } summary) summaries.Add(summary);
        }
        return summaries.AsReadOnly();
    }

    public async Task<AuthorizationMetadata?> FindByIdentityAsync(string provider, string issuer, string subject, CancellationToken ct)
    {
        if (!AuthorizationIdentity.HasVerifiedContext(provider, issuer, subject)) throw new ArgumentException("The verified provider identity context is invalid.");
        using var lease = await EnterAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        EnsurePrivateDirectories();
        ct.ThrowIfCancellationRequested();
        IdentityClaim? claim = ReadClaim(IdentityKey(provider, issuer, subject));
        if (claim is null) return null;
        if (claim.Provider != provider || claim.Issuer != issuer || claim.Subject != subject) throw IncompleteIdentity();
        AuthorizationMetadata? summary = ReadSummary(claim.ProfileId);
        if (summary is null || !ClaimMatches(claim, summary)) throw IncompleteIdentity();
        return summary;
    }

    public async Task<AuthorizationMetadata?> FindByRobloxUserIdAsync(long userId, CancellationToken ct)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        using var lease = await EnterAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        EnsurePrivateDirectories();
        AuthorizationMetadata? found = null;
        foreach (var context in RobloxContexts)
        {
            ct.ThrowIfCancellationRequested();
            IdentityClaim? claim = ReadClaim(UserKey(context.Provider, context.Issuer, userId));
            if (claim is null) continue;
            if (claim.Provider != context.Provider || claim.Issuer != context.Issuer || claim.VerifiedUserId != userId) throw IncompleteIdentity();
            AuthorizationMetadata? summary = ReadSummary(claim.ProfileId);
            if (summary is null || !ClaimMatches(claim, summary) || found is not null) throw IncompleteIdentity();
            found = summary;
        }
        return found;
    }

    public async Task DeleteAsync(Guid profileId, CancellationToken ct)
    {
        PathFor(profileId);
        using var lease = await EnterAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        await DeleteOwnedAsync(profileId, ct).ConfigureAwait(false);
    }

    private async Task DeleteOwnedAsync(Guid profileId, CancellationToken ct, long? fenceGeneration = null)
    {
        string path = PathFor(profileId);
        EnsurePrivateDirectories();
        ct.ThrowIfCancellationRequested();
        ProfilePointer? pointer = ReadPointer(profileId);
        if (pointer is null && File.Exists(path))
        {
            byte[] bytes = ReadPrivateBytes(path, MaximumFileBytes);
            try { if (HasMagic(bytes)) pointer = PointerFor(ParseEnvelope(bytes, profileId).Metadata); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        if (pointer is not null)
            foreach (string key in new[] { pointer.IdentityKey, pointer.UserKey })
                if (ReadClaim(key) is { } claim && claim.ProfileId != profileId) throw IncompleteIdentity();
        GenerationState generation = ReadGeneration(profileId);
        // Invalidation is durable before deletion; a canceled disconnect cannot revive old bytes.
        await WriteGenerationAsync(generation with { HighestAllocated = fenceGeneration ?? NextGeneration(generation.HighestAllocated), HighestMutationId = Guid.Empty }, ct).ConfigureAwait(false);
        DeletePrivateFile(path);
        DeleteObsoleteCopies(path);
        if (pointer is not null)
            foreach (string claim in new[] { IndexPath(pointer.IdentityKey), IndexPath(pointer.UserKey) })
            {
                DeletePrivateFile(claim);
                // Interrupted claim writes hold the plaintext subject and user ID.
                DeleteOrphanedTemporaries(claim, claim);
            }
        // Keep the bounded recovery references until both claims are gone. A crash or failed
        // claim deletion can then retry without scanning the inventory or guessing an identity.
        DeletePrivateFile(PointerPath(profileId));
        // The non-secret generation tombstone deliberately survives profile/grant deletion.
    }

    private AuthorizationMetadata? ReadSummary(Guid profileId)
    {
        string path = PathFor(profileId);
        if (!File.Exists(path)) return null;
        byte[] bytes = ReadPrivateBytes(path, MaximumFileBytes);
        try
        {
            // Raw v1 DPAPI has no readable summary. Painting cards never decrypts it or invents context.
            if (!HasMagic(bytes)) return null;
            AuthorizationMetadata summary = ParseEnvelope(bytes, profileId).Metadata;
            ValidateClaims(summary);
            GenerationState state = ReadGeneration(profileId);
            if (summary.Generation != state.HighestAllocated || summary.Generation != state.LastCommitted)
                summary = summary with { Health = AuthorizationHealth.SignInRequired };
            return summary;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static AuthorizationRecord ReadProtected(string path, Guid profileId, out bool legacy)
    {
        // Only selected credential use tightens an existing/legacy secret file. Card metadata and
        // identity-index reads below never rewrite per-file ACLs.
        RejectReparse(path);
        var selected = new FileInfo(path);
        if (!HasPrivateFileAcl(selected.GetAccessControl(AccessControlSections.Access))) selected.SetAccessControl(FileAcl());
        byte[] bytes = ReadPrivateBytes(path, MaximumFileBytes);
        try { return ReadProtectedBytes(bytes, profileId, out legacy); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static AuthorizationRecord ReadProtectedBytes(byte[] bytes, Guid profileId, out bool legacy)
    {
        byte[] cipher = [], clear = [], entropy = [];
        try
        {
            legacy = !HasMagic(bytes);
            VaultEnvelope? envelope = legacy ? null : ParseEnvelope(bytes, profileId);
            cipher = envelope?.ProtectedPayload ?? bytes;
            entropy = Entropy(profileId, legacy ? 1 : Version);
            try { clear = ProtectedData.Unprotect(cipher, entropy, DataProtectionScope.CurrentUser); }
            catch (CryptographicException) { throw new CryptographicException("The protected authorization could not be decrypted. The original is preserved; sign in or review recovery options."); }
            try
            {
                if (legacy)
                {
                    AuthorizationRecord old = (JsonSerializer.Deserialize<AuthorizationRecord>(clear, JsonOptions) ?? throw InvalidRecord()).Validated();
                    if (old.ProfileId != profileId) throw InvalidRecord();
                    return old with { AccessToken = "", RefreshToken = "", SessionCookie = "", Health = AuthorizationHealth.SignInRequired, Issuer = "", Subject = "" };
                }
                ProtectedPayload payload = JsonSerializer.Deserialize<ProtectedPayload>(clear, JsonOptions) ?? throw InvalidRecord();
                if (payload.Version != Version || payload.Authorization is null) throw InvalidRecord();
                AuthorizationRecord current = payload.Authorization.Validated();
                if (current.ProfileId != profileId || current.AccessToken.Length != 0
                    || !AuthorizationIdentity.HasVerifiedContext(current.Provider, current.Issuer, current.Subject, current.VerifiedUserId)
                    || MetadataFor(current, payload.CredentialRef, payload.SavedUtc) != envelope!.Metadata) throw InvalidRecord();
                return current;
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException) { throw InvalidRecord(); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(entropy);
            CryptographicOperations.ZeroMemory(cipher);
        }
    }

    private static VaultEnvelope ParseEnvelope(byte[] bytes, Guid expectedProfile)
    {
        VaultEnvelope envelope;
        try { envelope = JsonSerializer.Deserialize<VaultEnvelope>(bytes.AsSpan(Magic.Length), JsonOptions) ?? throw InvalidRecord(); }
        catch (JsonException) { throw InvalidRecord(); }
        if (envelope.Version != Version || envelope.Metadata is null || envelope.ProtectedPayload is not { Length: > 0 and <= 65536 }
            || envelope.PayloadDigest is not { Length: 64 } || Digest(envelope.ProtectedPayload) != envelope.PayloadDigest) throw InvalidRecord();
        AuthorizationMetadata metadata = envelope.Metadata;
        if (metadata.ProfileId != expectedProfile || metadata.CredentialRef != expectedProfile.ToString("N")
            || !AuthorizationIdentity.HasVerifiedContext(metadata.Provider, metadata.Issuer, metadata.Subject, metadata.VerifiedUserId)
            || metadata.VerifiedUserId <= 0 || metadata.Generation <= 0 || metadata.VerifiedUsername is null
            || metadata.VerifiedUsername.Length > 100 || metadata.VerifiedUsername.Any(char.IsControl)
            || !Enum.IsDefined(metadata.Health) || metadata.Provider == RobloxSessionIdentity.Provider && metadata.Health == AuthorizationHealth.RefreshPending
            || metadata.ExpiresUtc == default || metadata.SavedUtc == default) throw InvalidRecord();
        return envelope;
    }

    private void ValidateClaims(AuthorizationMetadata metadata)
    {
        ProfilePointer expected = PointerFor(metadata);
        if (ReadPointer(metadata.ProfileId) != expected) throw IncompleteIdentity();
        foreach (string key in new[] { expected.IdentityKey, expected.UserKey })
            if (ReadClaim(key) is not { } claim || !ClaimMatches(claim, metadata)) throw IncompleteIdentity();
    }
    private void ValidateAvailableClaim(string key, AuthorizationRecord record) => ValidateClaimOwner(ReadClaim(key), record);
    private static void ValidateClaimOwner(IdentityClaim? existing, AuthorizationRecord record)
    {
        if (existing is not null && (existing.ProfileId != record.ProfileId || existing.Provider != record.Provider
            || existing.Issuer != record.Issuer || existing.Subject != record.Subject || existing.VerifiedUserId != record.VerifiedUserId))
            throw new InvalidOperationException("This verified provider identity is already reserved for another saved profile. Review or disconnect that profile first.");
    }
    private void ValidateAvailableUserClaims(AuthorizationRecord record)
    {
        foreach (var context in RobloxContexts)
        {
            string key = UserKey(context.Provider, context.Issuer, record.VerifiedUserId);
            IdentityClaim? claim = ReadClaim(key);
            if (claim is null) continue;
            if (claim.Provider != context.Provider || claim.Issuer != context.Issuer || claim.VerifiedUserId != record.VerifiedUserId) throw IncompleteIdentity();
            ValidateClaimOwner(claim, record);
        }
    }
    private async Task ReserveClaimAsync(string key, AuthorizationRecord record, CancellationToken ct)
    {
        if (ReadClaim(key) is not null) return;
        var claim = new IdentityClaim(1, record.ProfileId, record.Provider, record.Issuer, record.Subject, record.VerifiedUserId);
        await AtomicWriteAsync(IndexPath(key), JsonSerializer.SerializeToUtf8Bytes(claim, JsonOptions), ct).ConfigureAwait(false);
    }
    private IdentityClaim? ReadClaim(string key)
    {
        IdentityClaim? claim = ReadSmall<IdentityClaim>(IndexPath(key));
        if (claim is not null && (claim.Version != 1 || claim.ProfileId == Guid.Empty
            || !AuthorizationIdentity.HasVerifiedContext(claim.Provider, claim.Issuer, claim.Subject, claim.VerifiedUserId) || claim.VerifiedUserId <= 0
            || key != IdentityKey(claim.Provider, claim.Issuer, claim.Subject) && key != UserKey(claim.Provider, claim.Issuer, claim.VerifiedUserId))) throw IncompleteIdentity();
        return claim;
    }
    private ProfilePointer? ReadPointer(Guid profileId)
    {
        ProfilePointer? pointer = ReadSmall<ProfilePointer>(PointerPath(profileId));
        if (pointer is not null && (pointer.Version != 1 || pointer.ProfileId != profileId
            || !ValidIndexKey(pointer.IdentityKey, "subject-") || !ValidIndexKey(pointer.UserKey, "user-"))) throw IncompleteIdentity();
        return pointer;
    }
    private GenerationState ReadGeneration(Guid profileId)
    {
        string path = GenerationPath(profileId);
        if (!File.Exists(path)) return new(2, profileId, 0, 0, Guid.Empty);
        byte[] bytes = ReadPrivateBytes(path, MaximumIndexBytes);
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var root = json.RootElement; int version = root.GetProperty("Version").GetInt32();
            string[] names = version switch
            {
                1 => ["Version", "ProfileId", "HighestAllocated", "LastCommitted"],
                2 => ["Version", "ProfileId", "HighestAllocated", "LastCommitted", "HighestMutationId"],
                _ => throw InvalidRecord()
            };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw InvalidRecord();
            if (seen.Count != names.Length) throw InvalidRecord();
            var state = new GenerationState(version, root.GetProperty("ProfileId").GetGuid(), root.GetProperty("HighestAllocated").GetInt64(),
                root.GetProperty("LastCommitted").GetInt64(), version == 2 ? root.GetProperty("HighestMutationId").GetGuid() : Guid.Empty);
            if (state.ProfileId != profileId || state.HighestAllocated < 0 || state.LastCommitted < 0
                || state.LastCommitted > state.HighestAllocated || state.HighestAllocated == 0 && state.HighestMutationId != Guid.Empty) throw InvalidRecord();
            return state;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { throw InvalidRecord(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private Task WriteGenerationAsync(GenerationState state, CancellationToken ct) => AtomicWriteAsync(GenerationPath(state.ProfileId), JsonSerializer.SerializeToUtf8Bytes(state with { Version = 2 }, JsonOptions), ct);
    private static long NextGeneration(long previous) => previous == long.MaxValue ? throw new InvalidOperationException("This authorization generation is exhausted. Create a new saved profile; generations will not reset.") : previous + 1;

    private static T? ReadSmall<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        byte[] bytes = ReadPrivateBytes(path, MaximumIndexBytes);
        try
        {
            try { return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw IncompleteIdentity(); }
            catch (JsonException) { throw IncompleteIdentity(); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static bool ClaimMatches(IdentityClaim claim, AuthorizationMetadata metadata) => claim.ProfileId == metadata.ProfileId
        && claim.Provider == metadata.Provider && claim.Issuer == metadata.Issuer && claim.Subject == metadata.Subject && claim.VerifiedUserId == metadata.VerifiedUserId;
    private static AuthorizationMetadata MetadataFor(AuthorizationRecord record, string reference, DateTimeOffset savedUtc) => new(record.ProfileId, reference,
        record.Provider, record.Issuer, record.Subject, record.VerifiedUserId, record.VerifiedUsername, record.Generation, record.Health, record.ExpiresUtc, savedUtc);
    private static ProfilePointer PointerFor(AuthorizationRecord record) => new(1, record.ProfileId,
        IdentityKey(record.Provider, record.Issuer, record.Subject), UserKey(record.Provider, record.Issuer, record.VerifiedUserId));
    private static ProfilePointer PointerFor(AuthorizationMetadata metadata) => new(1, metadata.ProfileId,
        IdentityKey(metadata.Provider, metadata.Issuer, metadata.Subject), UserKey(metadata.Provider, metadata.Issuer, metadata.VerifiedUserId));
    private static string IdentityKey(string provider, string issuer, string subject) => "subject-" + Digest(JsonSerializer.SerializeToUtf8Bytes(new[] { provider, issuer, subject }));
    private static string UserKey(string provider, string issuer, long userId) => "user-" + Digest(JsonSerializer.SerializeToUtf8Bytes(new[] { provider, issuer, userId.ToString(CultureInfo.InvariantCulture) }));
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool ValidIndexKey(string? key, string prefix) => key is not null && key.StartsWith(prefix, StringComparison.Ordinal)
        && key.Length == prefix.Length + 64 && key.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789ABCDEF") < 0;
    private string PathFor(Guid profileId)
    {
        if (profileId == Guid.Empty) throw new ArgumentException("Profile identity is required.");
        return Path.Combine(_directory, profileId.ToString("N") + ".dpapi");
    }
    private string PointerPath(Guid id) => PathFor(id) + ".identity";
    private string GenerationPath(Guid id) { PathFor(id); return Path.Combine(_generationDirectory, id.ToString("N") + ".json"); }
    private string IndexPath(string key)
    {
        if (!ValidIndexKey(key, "subject-") && !ValidIndexKey(key, "user-")) throw IncompleteIdentity();
        return Path.Combine(_indexDirectory, key + ".json");
    }
    private static bool HasMagic(byte[] bytes) => bytes.AsSpan().StartsWith(Magic);
    private static byte[] Entropy(Guid id, int version) => Encoding.UTF8.GetBytes("ZenithSessions/Personal/roblox-oauth/v" + version.ToString(CultureInfo.InvariantCulture) + "/" + id.ToString("N"));
    private static InvalidDataException InvalidRecord() => new("The protected authorization record is invalid or unsupported. Its original is preserved; sign in or review recovery options. Obsolete refresh tokens will not be restored.");
    private static InvalidDataException IncompleteIdentity() => new("The saved authorization identity index is incomplete or inconsistent. Its originals are preserved; review recovery or disconnect this authorization before relinking.");

    private void EnsurePrivateDirectories()
    {
        for (string? ancestor = _directory; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            if (Directory.Exists(ancestor) || File.Exists(ancestor)) RejectReparse(ancestor);
        RestrictDirectory(_directory); RestrictDirectory(_indexDirectory); RestrictDirectory(_generationDirectory);
    }
    private static void RestrictDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Exists) RejectReparse(path);
        FileSystemAclExtensions.Create(directory, DirectoryAcl());
        RejectReparse(path);
        // Freshly inspect three directory ACLs once per operation; avoid stale path caches and
        // avoid rewriting already-private permissions during lightweight metadata restoration.
        if (!HasPrivateDirectoryAcl(directory.GetAccessControl(AccessControlSections.Access)))
            directory.SetAccessControl(DirectoryAcl());
    }
    private static bool HasPrivateDirectoryAcl(DirectorySecurity acl)
    {
        if (!acl.AreAccessRulesProtected) return false;
        SecurityIdentifier user = CurrentUser(), system = new(WellKnownSidType.LocalSystemSid, null);
        bool hasUser = false, hasSystem = false;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            bool isUser = rule.IdentityReference.Equals(user), isSystem = rule.IdentityReference.Equals(system);
            if ((!isUser && !isSystem) || rule.AccessControlType != AccessControlType.Allow || rule.IsInherited
                || rule.FileSystemRights != FileSystemRights.FullControl || rule.PropagationFlags != PropagationFlags.None
                || rule.InheritanceFlags != (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)) return false;
            hasUser |= isUser;
            hasSystem |= isSystem;
        }
        return hasUser && hasSystem;
    }
    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new UnauthorizedAccessException("The current Windows user identity is unavailable for vault protection.");
    }
    private static DirectorySecurity DirectoryAcl()
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (SecurityIdentifier sid in new[] { CurrentUser(), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return acl;
    }
    private static FileSecurity FileAcl()
    {
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (SecurityIdentifier sid in new[] { CurrentUser(), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return acl;
    }
    private static bool HasPrivateFileAcl(FileSecurity acl)
    {
        if (!acl.AreAccessRulesProtected) return false;
        SecurityIdentifier user = CurrentUser(), system = new(WellKnownSidType.LocalSystemSid, null);
        bool hasUser = false, hasSystem = false;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            bool isUser = rule.IdentityReference.Equals(user), isSystem = rule.IdentityReference.Equals(system);
            if ((!isUser && !isSystem) || rule.AccessControlType != AccessControlType.Allow || rule.IsInherited
                || rule.FileSystemRights != FileSystemRights.FullControl || rule.InheritanceFlags != InheritanceFlags.None
                || rule.PropagationFlags != PropagationFlags.None) return false;
            hasUser |= isUser; hasSystem |= isSystem;
        }
        return hasUser && hasSystem;
    }
    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Vault records and directories cannot be filesystem links.");
    }
    private static byte[] ReadPrivateBytes(string path, int maximum)
    {
        RejectReparse(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 || file.Length > maximum) throw InvalidRecord();
        byte[] bytes = new byte[(int)file.Length];
        try { file.ReadExactly(bytes); return bytes; }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }
    private static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            ct.ThrowIfCancellationRequested();
            await using (var file = FileSystemAclExtensions.Create(new FileInfo(temporary), FileMode.CreateNew, FileSystemRights.FullControl,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough, FileAcl()))
            {
                await file.WriteAsync(bytes, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
                file.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                // ReplaceFile documents 1175 as retaining both original names. Transient
                // non-delete-shared readers can cause this or a sharing/lock violation.
                // Retry the same local ciphertext only; never delete/rename the target,
                // retry remote refresh, relax ACLs or retry ambiguous 1176/1177 outcomes.
                for(int attempt=0;;attempt++)
                {
                    ct.ThrowIfCancellationRequested();RejectReparse(path);RejectReparse(temporary);
                    try
                    {
                        new FileInfo(path).SetAccessControl(FileAcl());File.Replace(temporary,path,null);break;
                    }
                    catch(IOException ex) when(attempt<4 && ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) or unchecked((int)0x80070497))
                    {await Task.Delay(50<<attempt,ct).ConfigureAwait(false);}
                }
            }
            else File.Move(temporary, path);
        }
        finally { DeletePrivateFile(temporary); }
    }
    private static void DeleteObsoleteCopies(string path)
    {
        DeletePrivateFile(path + ".bak"); DeletePrivateFile(path + ".tmp");
        // The record and its pointer share the record's name prefix, so one scan finds both.
        DeleteOrphanedTemporaries(path, path, path + ".identity");
    }
    // A process kill or power loss inside AtomicWriteAsync leaves "<target>.<guid:N>.tmp" with a
    // full copy of the target (encrypted credential plus plaintext identity). Callers hold the
    // writer lease and every atomic write runs under it, so a match is never in flight; nothing
    // reads these names. Only exact atomic-write names of the given targets are removed.
    private static void DeleteOrphanedTemporaries(string prefix, params string[] targets)
    {
        string directory = Path.GetDirectoryName(prefix)!;
        if (!Directory.Exists(directory)) return;
        string[] names = Array.ConvertAll(targets, target => Path.GetFileName(target));
        string[] orphans = Directory.EnumerateFiles(directory, Path.GetFileName(prefix) + ".*.tmp", SearchOption.TopDirectoryOnly)
            .Where(file => names.Any(name => IsAtomicTemporaryOf(Path.GetFileName(file), name)))
            .Take(MaximumOrphanedTemporaries).ToArray();
        foreach (string orphan in orphans) DeletePrivateFile(orphan);
    }
    private static bool IsAtomicTemporaryOf(string candidate, string target) => candidate.Length == target.Length + 37
        && candidate.StartsWith(target, StringComparison.Ordinal) && candidate[target.Length] == '.'
        && candidate.EndsWith(".tmp", StringComparison.Ordinal) && candidate.AsSpan(target.Length + 1, 32).IndexOfAnyExcept("0123456789abcdef") < 0;
    private static void DeletePrivateFile(string path)
    {
        if (!File.Exists(path)) return;
        RejectReparse(path);
        File.Delete(path);
    }

    private async Task<WriterLease> EnterAsync(CancellationToken ct)
    {
        // Public callers force an asynchronous continuation off their UI context: DPAPI, ACL
        // operations, and the bounded local reads below use synchronous Windows primitives.
        ct.ThrowIfCancellationRequested();
        lock (_lifetime) { ObjectDisposedException.ThrowIf(_disposed, this); _activeCalls++; }
        bool admitted = false, entered = false;
        try
        {
            lock (RegistryGate)
            {
                if (_writer.PendingCalls >= MaximumPendingOperations) throw new AuthorizationVaultBusyException();
                _writer.PendingCalls++; admitted = true;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                // Enter may initially run on the UI thread. Force the platform portion off that
                // context even when the in-process semaphore was immediately available.
                await _writer.Semaphore.WaitAsync(deadline.Token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding); entered = true;
                EnsurePrivateDirectories();
                var processLease = await AcquireProcessLeaseAsync(deadline.Token).ConfigureAwait(false);
                return new(this, processLease);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new AuthorizationVaultBusyException(); }
        }
        catch
        {
            if (entered) _writer.Semaphore.Release();
            if (admitted) lock (RegistryGate) _writer.PendingCalls--;
            lock (_lifetime) _activeCalls--;
            throw;
        }
    }
    private async Task<FileStream> AcquireProcessLeaseAsync(CancellationToken ct)
    {
        string path = Path.Combine(_directory, WriterLockFileName);
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path)) RejectReparse(path);
            FileStream? file = null;
            try
            {
                file = FileSystemAclExtensions.Create(new FileInfo(path), FileMode.OpenOrCreate, FileSystemRights.FullControl,
                    FileShare.None, 1, FileOptions.Asynchronous, FileAcl());
                RejectReparse(path);
                if (file.Length != 0) throw new InvalidDataException("The protected store coordination file is invalid. Preserve it for recovery.");
                if (!HasPrivateFileAcl(file.GetAccessControl())) file.SetAccessControl(FileAcl());
                return file;
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                file?.Dispose();
                if (attempt >= 99) throw new AuthorizationVaultBusyException();
                await Task.Delay(20, ct).ConfigureAwait(false);
            }
            catch { file?.Dispose(); throw; }
        }
    }
    private void Leave()
    {
        _writer.Semaphore.Release();
        lock (RegistryGate) _writer.PendingCalls--;
        lock (_lifetime) _activeCalls--;
    }
    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_disposed) return;
            if (_activeCalls != 0) throw new InvalidOperationException("Cancel and await pending vault operations before disposing the vault.");
            _disposed = true;
        }
        lock (RegistryGate) { if (--_writer.Owners == 0) { Writers.Remove(_directory); _writer.Semaphore.Dispose(); } }
    }
    private sealed class SharedWriter { public readonly SemaphoreSlim Semaphore = new(1, 1); public int Owners, PendingCalls; }
    private sealed class WriterLease(ProtectedCredentialVault owner, FileStream processLease) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { processLease.Dispose(); } finally { owner.Leave(); }
        }
    }
    private sealed record VaultEnvelope(int Version, AuthorizationMetadata Metadata, string PayloadDigest, byte[] ProtectedPayload);
    private sealed record ProtectedPayload(int Version, string CredentialRef, AuthorizationRecord Authorization, DateTimeOffset SavedUtc);
    private sealed record ProfilePointer(int Version, Guid ProfileId, string IdentityKey, string UserKey);
    private sealed record IdentityClaim(int Version, Guid ProfileId, string Provider, string Issuer, string Subject, long VerifiedUserId);
    private sealed record GenerationState(int Version, Guid ProfileId, long HighestAllocated, long LastCommitted, Guid HighestMutationId);
}
