using System.Text.Json;
using System.Text.Json.Serialization;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed record IdentityProjectionProblem(Guid ProfileId, string ErrorType);
public sealed record IdentityProjectionReplayResult(int Projected, int Pending, IReadOnlyList<IdentityProjectionProblem> Problems);
public sealed class AccountIdentityProjectionException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// Recoverably copies a saved grant's last-known username/UserId into searchable profile metadata.
/// These profile fields remain display/search hints, never authorization or native session identity.
/// The journal contains no credential payload. Only the requested/pending profiles' summaries are read.
/// All instances for a data root coordinate journal mutation through a process-shared file lease.
/// Await callers before disposal. No background worker is created.
/// Stop older application writers before adopting this protocol; they do not participate in this lease.
/// </summary>
public sealed class AccountIdentityProjection : IDisposable
{
    public const int MaximumPending = 64;
    public const int MaximumPendingOperations = 4;
    public const string WriterLockFileName = ".writer.lock";
    private const int MaximumBytes = 8192;
    private static readonly object RegistryGate = new();
    private static readonly Dictionary<string, SharedWriter> Writers = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _directory;
    private readonly IAccountRepository _profiles;
    private readonly IAuthorizationMetadataReader _metadata;
    private readonly TimeSpan _deadline;
    private readonly SharedWriter _writer;
    private readonly object _lifetime = new();
    private int _activeCalls;
    private bool _disposed;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed record Pending(int Version, AuthorizationMetadata Expected, long ProfileRevision);

    public AccountIdentityProjection(string root, IAccountRepository profiles, IAuthorizationMetadataReader metadata, TimeSpan? deadline = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _directory = Path.GetFullPath(Path.Combine(root, "account-identity-pending"));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _deadline = deadline ?? TimeSpan.FromSeconds(15);
        if (_deadline <= TimeSpan.Zero || _deadline > TimeSpan.FromSeconds(60)) throw new ArgumentOutOfRangeException(nameof(deadline));
        lock (RegistryGate)
        {
            if (!Writers.TryGetValue(_directory, out SharedWriter? writer)) Writers.Add(_directory, writer = new());
            _writer = writer; _writer.Owners++;
        }
    }

    /// <summary>Call before the vault commits this generation. No usable authorization is advertised here.</summary>
    public async Task PrepareAsync(AuthorizationMetadata expected, CancellationToken ct)
    {
        Validate(expected);
        using var lease = await EnterAsync(ct).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_deadline);
        var profile = await _profiles.FindAsync(expected.ProfileId, deadline.Token).ConfigureAwait(false)
            ?? throw Failure("The saved profile was removed. No identity metadata was prepared.");
        string path = PathFor(expected.ProfileId);
        var files = PendingFiles();
        if (!File.Exists(path) && files.Length >= MaximumPending)
            throw Failure("The identity recovery journal is full. Reconcile its pending records before signing in again.");
        if (File.Exists(path))
        {
            var previous = await ReadAsync(path, deadline.Token).ConfigureAwait(false);
            if (expected.Generation < previous.Expected.Generation || !SameAccount(expected, previous.Expected)
                || expected.Generation == previous.Expected.Generation && !SameIdentity(expected, previous.Expected))
                throw Failure("A newer or different identity projection is already pending. Reconcile it before retrying.");
        }
        await WriteAsync(path, new(1, expected, profile.Revision), deadline.Token).ConfigureAwait(false);
    }

    /// <summary>Call only after the matching encrypted generation is durable. Failures retain the journal.</summary>
    public async Task CompleteAsync(Guid profileId, long expectedGeneration, CancellationToken ct)
    {
        if (profileId == Guid.Empty || expectedGeneration <= 0) throw new ArgumentException("A profile and authorization generation are required.");
        using var lease = await EnterAsync(ct).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_deadline);
        var pending = await ReadAsync(PathFor(profileId), deadline.Token).ConfigureAwait(false);
        if (pending.Expected.ProfileId != profileId || pending.Expected.Generation != expectedGeneration)
            throw Failure("The identity projection was superseded. Its recovery journal was preserved.");
        await ProjectAsync(pending, deadline.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Compatibility cleanup without a caller-owned deletion receipt. Requires an absent vault
    /// snapshot and uses only LastCommitted, preserving any newer pending intent.
    /// </summary>
    public Task DiscardAsync(Guid profileId, CancellationToken ct) => DiscardOwnedAsync(profileId, null, ct);

    /// <summary>Use the applied conditional-delete fence; a newer journal is always preserved.</summary>
    public Task DiscardAsync(Guid profileId, long maximumGeneration, CancellationToken ct)
    {
        if (maximumGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(maximumGeneration));
        return DiscardOwnedAsync(profileId, maximumGeneration, ct);
    }

    private async Task DiscardOwnedAsync(Guid profileId, long? maximumGeneration, CancellationToken ct)
    {
        if (profileId == Guid.Empty) throw new ArgumentException("A saved profile is required.");
        using var lease = await EnterAsync(ct).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_deadline);
        if (maximumGeneration is null)
        {
            if (_metadata is not ISnapshotAuthorizationVault vault)
                throw Failure("Identity cleanup requires a conditional deletion fence or a protected-store snapshot. Its journal was preserved.");
            var snapshot = await vault.ReadWriteSnapshotAsync(profileId, deadline.Token).ConfigureAwait(false);
            if (snapshot.ProfileId != profileId || snapshot.Record is not null)
                throw Failure("The authorization still has a saved record. Fence and disconnect it before discarding identity recovery work.");
            maximumGeneration = snapshot.LastCommitted;
            if (maximumGeneration <= 0) return;
        }
        else if (await _metadata.GetSummaryAsync(profileId, deadline.Token).ConfigureAwait(false) is not null)
            throw Failure("The authorization still has a saved record. Fence and disconnect it before discarding identity recovery work.");
        string path = PathFor(profileId);
        if (!File.Exists(path)) return;
        var pending = await ReadAsync(path, deadline.Token).ConfigureAwait(false);
        if (pending.Expected.ProfileId != profileId) throw Failure("The identity journal belongs to a different profile. It was preserved for explicit recovery.");
        if (pending.Expected.Generation > maximumGeneration.Value) return;
        deadline.Token.ThrowIfCancellationRequested();
        File.Delete(path);
    }

    /// <summary>Bounded startup reconciliation. It never enumerates/decrypts the credential inventory.</summary>
    public async Task<IdentityProjectionReplayResult> ReplayAsync(CancellationToken ct)
    {
        using var lease = await EnterAsync(ct).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_deadline);
        var files = PendingFiles();
        int projected = 0;
        var problems = new List<IdentityProjectionProblem>();
        foreach (string path in files)
        {
            deadline.Token.ThrowIfCancellationRequested();
            Guid id = Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var parsed) ? parsed : Guid.Empty;
            try
            {
                if (id == Guid.Empty) throw Failure("An identity journal filename is invalid. Preserve it for explicit recovery.");
                var pending = await ReadAsync(path, deadline.Token).ConfigureAwait(false);
                if (pending.Expected.ProfileId != id) throw Failure("The identity journal does not match its profile.");
                await ProjectAsync(pending, deadline.Token).ConfigureAwait(false);
                projected++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or RevisionConflictException)
            { problems.Add(new(id, ex.GetType().Name)); }
        }
        return new(projected, files.Length - projected, problems.AsReadOnly());
    }

    private async Task ProjectAsync(Pending pending, CancellationToken ct)
    {
        Validate(pending.Expected);
        var summary = await _metadata.GetSummaryAsync(pending.Expected.ProfileId, ct).ConfigureAwait(false);
        if (summary is null) throw Failure("The saved authorization is unavailable. The identity journal was preserved; sign in again or retry recovery.");
        Validate(summary);
        if (!SameIdentity(summary, pending.Expected))
            throw Failure("The saved authorization no longer matches the pending identity. Its journal was preserved for review.");
        var profile = await _profiles.FindAsync(summary.ProfileId, ct).ConfigureAwait(false)
            ?? throw Failure("The saved profile no longer exists. Its identity journal was preserved for cleanup review.");
        if (profile.Revision < pending.ProfileRevision) throw Failure("The profile revision moved backwards. Preserve the identity journal for recovery.");
        // Read the latest row, change only last-known identity metadata, and let the repository's
        // optimistic revision comparison reject a concurrent edit instead of overwriting it.
        if (profile.Username != summary.VerifiedUsername || profile.RobloxUserId != summary.VerifiedUserId)
            await _profiles.SaveAsync(profile with { Username = summary.VerifiedUsername, RobloxUserId = summary.VerifiedUserId }, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        File.Delete(PathFor(summary.ProfileId));
    }

    private static bool SameAccount(AuthorizationMetadata left, AuthorizationMetadata right) => left.ProfileId == right.ProfileId
        && left.CredentialRef == right.CredentialRef && left.Provider == right.Provider && left.Issuer == right.Issuer
        && left.Subject == right.Subject && left.VerifiedUserId == right.VerifiedUserId;
    private static bool SameIdentity(AuthorizationMetadata left, AuthorizationMetadata right) => SameAccount(left, right)
        && left.Generation == right.Generation && left.VerifiedUsername == right.VerifiedUsername;

    private static void Validate(AuthorizationMetadata value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.ProfileId == Guid.Empty || value.CredentialRef != value.ProfileId.ToString("N")
            || !AuthorizationIdentity.HasVerifiedContext(value.Provider, value.Issuer, value.Subject, value.VerifiedUserId)
            || value.VerifiedUserId <= 0 || value.Generation <= 0 || value.Health != AuthorizationHealth.Linked
            || value.VerifiedUsername is not { Length: > 0 and <= 20 }
            || !PublicRobloxUsername.HasValidCharacters(value.VerifiedUsername))
            throw Failure("The saved identity metadata is invalid or needs sign-in. No profile identity was changed.");
    }

    private string[] PendingFiles()
    {
        if (!Directory.Exists(_directory)) return [];
        // Include incomplete/unknown files in the cap; repeated interrupted writes cannot make an
        // unbounded startup scan. Such files are preserved, never guessed into a committed identity.
        var files = Directory.EnumerateFiles(_directory)
            .Where(path => !string.Equals(Path.GetFileName(path), WriterLockFileName, StringComparison.OrdinalIgnoreCase))
            .Take(MaximumPending + 1).ToArray();
        if (files.Length > MaximumPending) throw Failure("The identity recovery journal exceeds its bounded record limit. Preserve it for explicit recovery.");
        return files;
    }
    private string PathFor(Guid id) => Path.Combine(_directory, id.ToString("N") + ".json");
    private static async Task<Pending> ReadAsync(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (file.Length is <= 0 or > MaximumBytes) throw Failure("The identity recovery record has an invalid size. It was preserved.");
        var pending = await JsonSerializer.DeserializeAsync<Pending>(file, Json, ct).ConfigureAwait(false);
        if (pending is not { Version: 1, Expected: not null } || pending.ProfileRevision < 0)
            throw Failure("The identity recovery record has an unsupported format. It was preserved.");
        Validate(pending.Expected);
        return pending;
    }
    private async Task WriteAsync(string path, Pending pending, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(pending, Json);
        if (bytes.Length > MaximumBytes) throw Failure("The identity recovery record exceeds its size limit.");
        Directory.CreateDirectory(_directory);
        string temporary = path + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await file.WriteAsync(bytes, ct).ConfigureAwait(false); await file.FlushAsync(ct).ConfigureAwait(false); file.Flush(true); }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task<WriterLease> EnterAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lifetime) { ObjectDisposedException.ThrowIf(_disposed, this); _activeCalls++; }
        bool admitted = false, entered = false;
        try
        {
            lock (RegistryGate)
            {
                if (_writer.PendingCalls >= MaximumPendingOperations) throw Failure("Identity recovery is busy. Wait for its owned operation, then retry.");
                _writer.PendingCalls++; admitted = true;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await _writer.Semaphore.WaitAsync(deadline.Token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding); entered = true;
                EnsureDirectory();
                var processLease = await AcquireProcessLeaseAsync(deadline.Token).ConfigureAwait(false);
                return new(this, processLease);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw Failure("Identity recovery is busy. Wait for its owned operation, then retry."); }
        }
        catch
        {
            if (entered) _writer.Semaphore.Release();
            if (admitted) lock (RegistryGate) _writer.PendingCalls--;
            lock (_lifetime) _activeCalls--;
            throw;
        }
    }

    private void EnsureDirectory()
    {
        for (string? ancestor = _directory; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            if (Directory.Exists(ancestor) || File.Exists(ancestor)) RejectReparse(ancestor);
        Directory.CreateDirectory(_directory); RejectReparse(_directory);
    }

    private async Task<FileStream> AcquireProcessLeaseAsync(CancellationToken ct)
    {
        string path = Path.Combine(_directory, WriterLockFileName);
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { RejectReparse(path); }
            catch (FileNotFoundException) { }
            FileStream? file = null;
            try
            {
                file = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
                RejectReparse(path);
                if (file.Length != 0) throw Failure("The identity recovery coordination file is invalid. Its original was preserved.");
                return file;
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                file?.Dispose();
                if (attempt >= 99) throw Failure("Identity recovery is busy. Wait for its owned operation, then retry.");
                await Task.Delay(20, ct).ConfigureAwait(false);
            }
            catch { file?.Dispose(); throw; }
        }
    }

    private static void RejectReparse(string path)
    { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw Failure("Identity recovery does not follow filesystem links. Its originals were preserved."); }

    private void Leave()
    {
        _writer.Semaphore.Release();
        lock (RegistryGate) _writer.PendingCalls--;
        lock (_lifetime) _activeCalls--;
    }
    private static AccountIdentityProjectionException Failure(string message) => new(message);
    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_disposed) return;
            if (_activeCalls != 0) throw new InvalidOperationException("Cancel and await identity projection operations before disposal.");
            _disposed = true;
        }
        lock (RegistryGate) { if (--_writer.Owners == 0) { Writers.Remove(_directory); _writer.Semaphore.Dispose(); } }
    }
    private sealed class SharedWriter { public readonly SemaphoreSlim Semaphore = new(1, 1); public int Owners, PendingCalls; }
    private sealed class WriterLease(AccountIdentityProjection owner, FileStream processLease) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { processLease.Dispose(); } finally { owner.Leave(); }
        }
    }
}
