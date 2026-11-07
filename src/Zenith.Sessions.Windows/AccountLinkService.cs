using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// One owned authorization operation, plus at most one disconnect waiting to take ownership.
/// Authentication is contextual API identity, never proof of native-client sign-in or arrival.
/// </summary>
public sealed class AccountLinkService(IAccountRepository profiles, IAccountCredentialVault vault, RobloxOAuthClient oauth, IOAuthBrowserFlow browser,
    AccountIdentityProjection? projection = null)
    : IAccountLinkService, IDisposable
{
    private readonly object _state = new();
    private readonly CancellationTokenSource _shutdown = new();
    private ActiveOperation? _active;
    private bool _disconnectPending, _disposed;

    public async Task<LinkedIdentity> LinkAsync(Guid profileId, string clientId, int callbackPort, CancellationToken ct)
    {
        var operation = Begin(profileId, ct);
        try
        {
            await RequireProfileAsync(profileId, operation.Token).ConfigureAwait(false);
            var previous = (await vault.ReadAsync(profileId, operation.Token).ConfigureAwait(false))?.Validated();
            EnsureCurrent(operation);
            var attempt = new OAuthAttempt(clientId, callbackPort);
            Uri callback = await browser.AuthorizeAsync(attempt, operation.Token).ConfigureAwait(false);
            EnsureCurrent(operation);
            var tokens = await oauth.ExchangeAsync(attempt, attempt.ConsumeCallback(callback), operation.Token).ConfigureAwait(false);
            EnsureCurrent(operation);
            var identity = await oauth.IdentityAsync(tokens.AccessToken, operation.Token).ConfigureAwait(false);
            EnsureCurrent(operation);
            if (previous is not null && !SameBinding(previous, identity))
                throw new AccountLinkException("This sign-in belongs to a different contextual account identity. Disconnect the existing link explicitly or create another profile.");
            await RequireUniqueIdentityAsync(profileId, identity, operation.Token).ConfigureAwait(false);
            await RequireProfileAsync(profileId, operation.Token).ConfigureAwait(false);
            EnsureCurrent(operation);
            long generation = await AllocateGenerationAsync(profileId, previous?.Generation ?? 0, operation.Token).ConfigureAwait(false);
            EnsureCurrent(operation);
            // Access tokens are operational only. The protected store retains the rotating refresh grant.
            var record = new AuthorizationRecord(profileId, RobloxOAuthIdentity.Provider, identity.UserId, generation,
                "", tokens.RefreshToken, tokens.ExpiresUtc, identity.Username, AuthorizationHealth.Linked, identity.Issuer, identity.Subject);
            await PrepareProjectionAsync(record, operation).ConfigureAwait(false);
            await vault.SaveAsync(record, operation.Token).ConfigureAwait(false);
            EnsureCurrent(operation);
            await CompleteProjectionAsync(record, operation).ConfigureAwait(false);
            return identity;
        }
        finally { Complete(operation); }
    }

    public async Task<LinkedIdentity> RefreshAsync(Guid profileId, string clientId, CancellationToken ct)
    {
        var operation = Begin(profileId, ct);
        try
        {
            await RequireProfileAsync(profileId, operation.Token).ConfigureAwait(false);
            var record = (await vault.ReadAsync(profileId, operation.Token).ConfigureAwait(false))?.Validated()
                ?? throw new AccountLinkException("This profile is not linked. Sign in first.");
            EnsureCurrent(operation);
            if (record.Health != AuthorizationHealth.Linked || !RobloxOAuthIdentity.HasVerifiedContext(record.Issuer, record.Subject))
                throw new AccountLinkException("This authorization needs sign-in or a previous refresh was uncertain. The old refresh token will not be reused.");
            OAuthAttempt.ValidateClient(clientId);
            // Durable intent precedes a one-use refresh. Uncertainty can never silently replay its token.
            long pendingGeneration = await AllocateGenerationAsync(profileId, record.Generation, operation.Token).ConfigureAwait(false);
            var pending = record with { Generation = pendingGeneration, Health = AuthorizationHealth.RefreshPending, AccessToken = "" };
            EnsureCurrent(operation);
            await vault.SaveAsync(pending, operation.Token).ConfigureAwait(false);
            EnsureCurrent(operation);
            var tokens = await oauth.RefreshAsync(clientId, record.RefreshToken, operation.Token).ConfigureAwait(false);
            EnsureCurrent(operation);
            var identity = await oauth.IdentityAsync(tokens.AccessToken, operation.Token).ConfigureAwait(false);
            EnsureCurrent(operation);
            if (!SameBinding(record, identity))
                throw new AccountLinkException("The refreshed contextual account identity changed. Sign in again; no replacement authorization was committed.");
            await RequireUniqueIdentityAsync(profileId, identity, operation.Token).ConfigureAwait(false);
            await RequireProfileAsync(profileId, operation.Token).ConfigureAwait(false);
            EnsureCurrent(operation);
            long committedGeneration = await AllocateGenerationAsync(profileId, pending.Generation, operation.Token).ConfigureAwait(false);
            EnsureCurrent(operation);
            var committed = pending with
            {
                Generation = committedGeneration, AccessToken = "", RefreshToken = tokens.RefreshToken,
                ExpiresUtc = tokens.ExpiresUtc, VerifiedUsername = identity.Username, Health = AuthorizationHealth.Linked,
                Issuer = identity.Issuer, Subject = identity.Subject
            };
            await PrepareProjectionAsync(committed, operation).ConfigureAwait(false);
            await vault.SaveAsync(committed, operation.Token).ConfigureAwait(false);
            EnsureCurrent(operation);
            await CompleteProjectionAsync(committed, operation).ConfigureAwait(false);
            return identity;
        }
        finally { Complete(operation); }
    }

    public async Task<bool> DisconnectAsync(Guid profileId, string clientId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ActiveOperation? active;
        CancellationTokenSource stop;
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_disconnectPending) throw new AccountLinkException("Another authorization disconnect is already in progress.");
            _disconnectPending = true;
            stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
            active = _active;
            // Fence before waiting for the serialized owner, browser, network, or storage.
            if (active?.ProfileId == profileId) active.Invalidated = true;
        }
        using var disconnectStop = stop;
        try
        {
            if (active?.ProfileId == profileId) active.Cancel();
            if (active is not null) await active.Completion.Task.WaitAsync(stop.Token).ConfigureAwait(false);
            stop.Token.ThrowIfCancellationRequested();
            var record = (await vault.ReadAsync(profileId, stop.Token).ConfigureAwait(false))?.Validated();
            long projectionFence = await AllocateGenerationAsync(profileId, record?.Generation ?? 0, stop.Token).ConfigureAwait(false);
            await vault.DeleteAsync(profileId, stop.Token).ConfigureAwait(false);
            if (projection is not null) await projection.DiscardAsync(profileId, projectionFence, stop.Token).ConfigureAwait(false);
            // Context-less legacy authorization is unusable and must not trigger token reuse.
            if (record is null) return true;
            if (record.RefreshToken.Length == 0) return false;
            try { return await oauth.RevokeAsync(clientId, record.RefreshToken, stop.Token).ConfigureAwait(false); }
            catch (AccountLinkException) { return false; }
            catch (HttpRequestException) { return false; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        }
        finally
        {
            lock (_state) _disconnectPending = false;
        }
    }

    private ActiveOperation Begin(Guid profileId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null || _disconnectPending)
                throw new AccountLinkException("Another authorization operation is in progress. Finish or cancel it before trying again.");
            return _active = new(profileId, CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token));
        }
    }
    private void EnsureCurrent(ActiveOperation operation)
    {
        operation.Token.ThrowIfCancellationRequested();
        lock (_state)
            if (_disposed || operation.Invalidated || !ReferenceEquals(_active, operation))
                throw new OperationCanceledException("This authorization operation was cancelled or superseded.", operation.Token);
    }
    private void Complete(ActiveOperation operation)
    {
        lock (_state)
            if (ReferenceEquals(_active, operation)) _active = null;
        operation.Dispose();
        operation.Completion.TrySetResult();
    }
    private static bool SameBinding(AuthorizationRecord record, LinkedIdentity identity) => record.Provider == RobloxOAuthIdentity.Provider
        && record.VerifiedUserId == identity.UserId && RobloxOAuthIdentity.HasVerifiedContext(identity.Issuer, identity.Subject)
        && (record.Issuer.Length == 0 && record.Subject.Length == 0
            || record.Issuer == identity.Issuer && record.Subject == identity.Subject);

    private async Task RequireUniqueIdentityAsync(Guid profileId, LinkedIdentity identity, CancellationToken ct)
    {
        if (vault is not IAuthorizationMetadataReader metadata)
            throw new AccountLinkException("The authorization store cannot safely check duplicate contextual identities. No new authorization was committed.");
        var found = await metadata.FindByIdentityAsync(RobloxOAuthIdentity.Provider, identity.Issuer, identity.Subject, ct).ConfigureAwait(false);
        if (found is not null && found.ProfileId != profileId)
            throw new AccountLinkException("This verified Roblox identity is already linked to another saved profile. Disconnect its existing link before linking another profile.");
    }
    private async Task<long> AllocateGenerationAsync(Guid profileId, long minimumExclusive, CancellationToken ct)
    {
        if (vault is not IAuthorizationGenerationStore generations)
            throw new AccountLinkException("The authorization store cannot durably fence replaced grants. No authorization operation can be committed.");
        long generation = await generations.AllocateNextGenerationAsync(profileId, minimumExclusive, ct).ConfigureAwait(false);
        if (generation <= minimumExclusive || generation <= 0)
            throw new AccountLinkException("The authorization store returned an invalid generation. Sign in again after repairing its protected state.");
        return generation;
    }
    private async Task RequireProfileAsync(Guid id, CancellationToken ct)
    {
        if (await profiles.FindAsync(id, ct).ConfigureAwait(false) is null)
            throw new AccountLinkException("This saved profile no longer exists. No authorization was attached.");
    }
    private async Task PrepareProjectionAsync(AuthorizationRecord record, ActiveOperation operation)
    {
        if (projection is null) return;
        // This bounded journal contains contextual identity metadata only, never the refresh grant.
        var expected = new AuthorizationMetadata(record.ProfileId, record.ProfileId.ToString("N"), record.Provider,
            record.Issuer, record.Subject, record.VerifiedUserId, record.VerifiedUsername, record.Generation,
            record.Health, record.ExpiresUtc, DateTimeOffset.UtcNow);
        await projection.PrepareAsync(expected, operation.Token).ConfigureAwait(false);
        EnsureCurrent(operation);
    }
    private async Task CompleteProjectionAsync(AuthorizationRecord record, ActiveOperation operation)
    {
        if (projection is null) return;
        await projection.CompleteAsync(record.ProfileId, record.Generation, operation.Token).ConfigureAwait(false);
        EnsureCurrent(operation);
    }
    public void Dispose()
    {
        lock (_state)
        {
            if (_disposed) return;
            _disposed = true;
            if (_active is not null) _active.Invalidated = true;
        }
        try { _shutdown.Cancel(); }
        finally { _shutdown.Dispose(); }
    }
    private sealed class ActiveOperation(Guid profileId, CancellationTokenSource stop) : IDisposable
    {
        private int _finished;
        public Guid ProfileId { get; } = profileId;
        public CancellationToken Token { get; } = stop.Token;
        public bool Invalidated { get; set; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Cancel()
        {
            try { stop.Cancel(); }
            catch (ObjectDisposedException) when (Volatile.Read(ref _finished) != 0) { /* The already-fenced operation completed before cancellation reached it. */ }
        }
        public void Dispose() { Volatile.Write(ref _finished, 1); stop.Dispose(); }
    }
}
