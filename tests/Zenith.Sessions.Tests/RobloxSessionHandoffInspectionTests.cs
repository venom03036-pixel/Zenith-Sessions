using System.Globalization;
using System.IO;
using System.Text.Json;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

// Every credential, result and browser boundary is synthetic. Real DPAPI is exercised only in
// this newly owned temporary fixture; no browser, HTTP endpoint or native client is invoked.
public sealed class RobloxSessionHandoffInspectionTests : IDisposable
{
    private readonly string _name = "zenith-handoff-inspection-" + Guid.NewGuid().ToString("N");
    private string Root => Path.Combine(Path.GetTempPath(), _name);
    private const string Original = "synthetic-selected-original-session", Current = "synthetic-owned-browser-current-session";
    private const string Rotated = "synthetic-validated-replacement-session", RawFailure = "synthetic-raw-cookie-and-ticket-must-not-escape";
    private static LinkedIdentity Identity(long user = 456, string name = "verified_player") => new(user, name, RobloxSessionIdentity.Issuer, user.ToString(CultureInfo.InvariantCulture));
    private static AuthorizationRecord Saved(Guid id) => new(id, RobloxSessionIdentity.Provider, 456, 1, "", "", DateTimeOffset.MaxValue,
        "old_player", AuthorizationHealth.Linked, RobloxSessionIdentity.Issuer, "456", SessionCookie: Original);
    private static RobloxHandoffProbeResult Captured(RobloxHandoffProbeContext context, bool? gesture = true)
    {
        var parsed = RobloxHandoffProbePolicy.InspectExternal(context, RobloxHandoffProbePolicy.CreateGamePageUri(context),
            RobloxHandoffProbePolicy.OfficialOrigin, "roblox-player:1+launchmode:play+gameinfo:synthetic-ticket");
        Assert.Equal(RobloxHandoffProbeOutcome.CapturedAndPrevented, parsed.Outcome);
        return new(parsed.Outcome, parsed.Shape, gesture);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task SelectedGrantIsDurablyFencedAndCleanupJoinedBeforeCurrentCookieIsVerified(bool? gesture)
    {
        await using var fixture = await Fixture.Open(Root); var entered = Signal(); var release = Signal(); bool cleaned = false;
        await using var probe = new Probe
        {
            Run = async (context, input, _) =>
            {
                Assert.Equal(fixture.Profile.Id, context.ProfileId); Assert.Equal(456, context.ExpectedUserId);
                Assert.Equal(42, context.PlaceId); Assert.Equal(2, context.AuthorizationGeneration);
                Assert.Equal(Original, input.CopyValue()); Assert.Null(input.ExpiresUtc);
                var pending = (await fixture.Inner.ReadAsync(context.ProfileId, default))!;
                Assert.Equal(AuthorizationHealth.SignInRequired, pending.Health); Assert.Equal(2, pending.Generation);
                entered.TrySetResult(); await release.Task;
                cleaned = true;
                return new(Captured(context, gesture), new(Current));
            }
        };
        fixture.Client.Reply = (_, _, _) =>
        {
            Assert.True(cleaned); return Task.FromResult(new RobloxSessionValidation(Identity()));
        };
        var work = fixture.Service.InspectHandoffAsync(fixture.Profile.Id, 42, probe, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.False(work.IsCompleted);
            Assert.Equal(0, fixture.Client.Calls); Assert.Empty(fixture.Vault.Writes.Where(record => record.Health == AuthorizationHealth.Linked));
            await Assert.ThrowsAsync<AccountLinkException>(() => fixture.Service.CheckSavedAsync(fixture.Profile.Id, default));
            var busy = await fixture.Service.InspectHandoffAsync(fixture.Profile.Id, 42, probe, default);
            Assert.Equal(RobloxHandoffProbeOutcome.Busy, busy.Outcome); Assert.Equal(1, probe.Calls);
        }
        finally { release.TrySetResult(); await work.WaitAsync(TimeSpan.FromSeconds(3)); }
        var result = await work;
        Assert.Same(probe.Returned!.Result, result); Assert.Equal(gesture, result.UserInitiated);
        Assert.False(result.CanLaunch); Assert.True(result.NativeLaunchPrevented);
        var saved = (await fixture.Inner.ReadAsync(fixture.Profile.Id, default))!;
        Assert.Equal(3, saved.Generation); Assert.Equal(AuthorizationHealth.Linked, saved.Health); Assert.Equal(Current, saved.SessionCookie);
        Assert.Equal(new[] { Current }, fixture.Client.Seen);
        var profile = (await fixture.Repository.FindAsync(fixture.Profile.Id, default))!;
        Assert.Equal("Manual alias", profile.Label); Assert.Equal("Keep local notes", profile.Notes); Assert.Equal(456, profile.RobloxUserId);
        Assert.Equal("verified_player", profile.Username); Assert.Equal(0, fixture.SignIn.Calls); Assert.Equal(0, fixture.Vault.InventoryReads);
        Assert.All(fixture.Vault.ReadIds, id => Assert.Equal(fixture.Profile.Id, id));
        Disposed(probe.Input); Disposed(probe.Issued); Assert.Null(probe.Returned.UpdatedCredential);
        string safe = JsonSerializer.Serialize(result) + result + JsonSerializer.Serialize(probe.Returned);
        foreach (string secret in new[] { Original, Current, "synthetic-ticket" }) Assert.DoesNotContain(secret, safe);
    }

    [Theory]
    [InlineData(RobloxHandoffProbeOutcome.ContextRejected)]
    [InlineData(RobloxHandoffProbeOutcome.OriginRejected)]
    [InlineData(RobloxHandoffProbeOutcome.ExternalSchemeBlocked)]
    [InlineData(RobloxHandoffProbeOutcome.MalformedHandoff)]
    [InlineData(RobloxHandoffProbeOutcome.TimedOut)]
    [InlineData(RobloxHandoffProbeOutcome.Cancelled)]
    [InlineData(RobloxHandoffProbeOutcome.BrowserUnavailable)]
    [InlineData(RobloxHandoffProbeOutcome.Failed)]
    [InlineData(RobloxHandoffProbeOutcome.SignInRequired)]
    [InlineData(RobloxHandoffProbeOutcome.IdentityMismatch)]
    [InlineData(RobloxHandoffProbeOutcome.AuthorizationChanged)]
    [InlineData(RobloxHandoffProbeOutcome.Busy)]
    [InlineData(RobloxHandoffProbeOutcome.CleanupFailed)]
    public async Task EveryNonCaptureOutcomeLeavesSignInRequiredAndNeverReplaysTheOldCookie(RobloxHandoffProbeOutcome outcome)
    {
        await using var fixture = await Fixture.Open(Root);
        await using var probe = new Probe { Run = (_, _, _) => Task.FromResult(new RobloxHandoffProbeCapture(new(outcome), new(Current))) };
        var result = await fixture.Service.InspectHandoffAsync(fixture.Profile.Id, 42, probe, default);
        Assert.Equal(outcome, result.Outcome); Assert.Equal(0, fixture.Client.Calls);
        var saved = (await fixture.Inner.ReadAsync(fixture.Profile.Id, default))!;
        Assert.Equal(AuthorizationHealth.SignInRequired, saved.Health); Assert.Equal(2, saved.Generation); Assert.Equal(Original, saved.SessionCookie);
        Assert.Single(fixture.Vault.Writes); Disposed(probe.Input); Disposed(probe.Issued);
        var retry = await fixture.Service.InspectHandoffAsync(fixture.Profile.Id, 42, probe, default);
        Assert.Equal(RobloxHandoffProbeOutcome.SignInRequired, retry.Outcome); Assert.Equal(1, probe.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureWithoutAnUnexpiredCurrentOwnedCookieCannotRestoreAuthority(bool expired)
    {
        await using var fixture = await Fixture.Open(Root);
        await using var probe = new Probe { Run = (context, _, _) => Task.FromResult(new RobloxHandoffProbeCapture(Captured(context),
            expired ? new(Current, DateTimeOffset.UtcNow.AddMinutes(-1)) : null)) };
        var result = await fixture.Service.InspectHandoffAsync(fixture.Profile.Id, 42, probe, default);
        Assert.Equal(RobloxHandoffProbeOutcome.SignInRequired, result.Outcome); Assert.Equal(0, fixture.Client.Calls);
        Assert.Equal(AuthorizationHealth.SignInRequired, (await fixture.Inner.ReadAsync(fixture.Profile.Id, default))!.Health);
        Disposed(probe.Input); if (expired) Disposed(probe.Issued);
    }

    [Fact]
    public async Task CurrentCookieRotationIsReverifiedAndPersistedWithTheSameExpectedIdentity()
    {
        await using var fixture = await Fixture.Open(Root); RobloxSessionCredential? rotated = null;
        await using var probe = new Probe();
        fixture.Client.Reply = (call, _, _) => Task.FromResult(call == 1
            ? new RobloxSessionValidation(Identity(), rotated = new(Rotated))
            : new RobloxSessionValidation(Identity(name: "renamed_player")));
        var result = await fixture.Service.InspectHandoffAsync(fixture.Profile.Id, 42, probe, default);
        Assert.Equal(RobloxHandoffProbeOutcome.CapturedAndPrevented, result.Outcome);
        Assert.Equal(new[] { Current, Rotated }, fixture.Client.Seen);
        var saved = (await fixture.Inner.ReadAsync(fixture.Profile.Id, default))!;
        Assert.Equal(Rotated, saved.SessionCookie); Assert.Equal("renamed_player", saved.VerifiedUsername); Assert.Equal(456, saved.VerifiedUserId);
        Disposed(rotated); Disposed(probe.Input); Disposed(probe.Issued);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongCurrentOrRotatedUserCannotReplaceThePinnedAccount(bool afterRotation)
    {
        await using var fixture = await Fixture.Open(Root); await using var probe = new Probe();
        fixture.Client.Reply = (call, _, _) => Task.FromResult(afterRotation && call == 1
            ? new RobloxSessionValidation(Identity(), new(Rotated)) : new RobloxSessionValidation(Identity(999)));
        var result = await fixture.Service.InspectHandoffAsync(fixture.Profile.Id, 42, probe, default);
        Assert.Equal(RobloxHandoffProbeOutcome.IdentityMismatch, result.Outcome);
        var pending = (await fixture.Inner.ReadAsync(fixture.Profile.Id, default))!;
        Assert.Equal(456, pending.VerifiedUserId); Assert.Equal(Original, pending.SessionCookie); Assert.Equal(AuthorizationHealth.SignInRequired, pending.Health);
        Assert.DoesNotContain(fixture.Vault.Writes, record => record.Health == AuthorizationHealth.Linked);
        Disposed(probe.Issued);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unlinked")]
    [InlineData("expired")]
    [InlineData("oauth")]
    [InlineData("stale")]
    public async Task IneligibleSelectedAuthorizationDoesNotOpenTheProbeOrContactTheClient(string kind)
    {
        await using var fixture = await Fixture.Open(Root, record: id => kind switch
        {
            "missing" => null,
            "unlinked" => Saved(id) with { Health = AuthorizationHealth.SignInRequired },
            "expired" => Saved(id) with { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1) },
            "oauth" => new(id, RobloxOAuthIdentity.Provider, 456, 1, "", "synthetic-legacy-refresh", DateTimeOffset.MaxValue,
                Issuer: RobloxOAuthIdentity.Issuer, Subject: "opaque-subject"),
            _ => Saved(id)
        });
        if (kind == "stale") await fixture.Inner.AllocateNextGenerationAsync(fixture.Profile.Id, 1, default);
        await using var probe = new Probe();
        var result = await fixture.Service.InspectHandoffAsync(fixture.Profile.Id, 42, probe, default);
        Assert.Equal(RobloxHandoffProbeOutcome.SignInRequired, result.Outcome);
        Assert.Equal(0, probe.Calls); Assert.Equal(0, fixture.Client.Calls); Assert.Equal(0, fixture.SignIn.Calls);
        if (kind == "expired") Assert.Equal(AuthorizationHealth.SignInRequired, (await fixture.Inner.ReadAsync(fixture.Profile.Id, default))!.Health);
        if (kind == "oauth") Assert.Equal("synthetic-legacy-refresh", (await fixture.Inner.ReadAsync(fixture.Profile.Id, default))!.RefreshToken);
    }

    [Fact]
    public async Task InvalidContextAndMissingProfileDoNotReadAnyProtectedInventory()
    {
        await using var fixture = await Fixture.Open(Root); await using var probe = new Probe();
        Assert.Equal(RobloxHandoffProbeOutcome.ContextRejected, (await fixture.Service.InspectHandoffAsync(Guid.Empty, 42, probe, default)).Outcome);
        Assert.Equal(RobloxHandoffProbeOutcome.ContextRejected, (await fixture.Service.InspectHandoffAsync(fixture.Profile.Id, 0, probe, default)).Outcome);
        Assert.Equal(RobloxHandoffProbeOutcome.ContextRejected, (await fixture.Service.InspectHandoffAsync(Guid.NewGuid(), 42, probe, default)).Outcome);
        Assert.Empty(fixture.Vault.ReadIds); Assert.Empty(fixture.Vault.Writes); Assert.Equal(0, probe.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndDisconnectJoinTheUncooperativeProbeAndDisposeLateSuccessOrFailure(bool failLate)
    {
        await using var fixture = await Fixture.Open(Root); var entered = Signal(); var release = Signal();
        await using var probe = new Probe { Run = async (context, _, _) =>
        {
            entered.TrySetResult(); await release.Task;
            if (failLate) throw new InvalidOperationException(RawFailure);
            return new(Captured(context), new(Current));
        } };
        var inspection = fixture.Service.InspectHandoffAsync(fixture.Profile.Id, 42, probe, default);
        Task? disconnect = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); fixture.Service.Cancel();
            disconnect = fixture.Service.DisconnectAsync(fixture.Profile.Id, default);
            Assert.False(inspection.IsCompleted); Assert.False(disconnect.IsCompleted);
            await Assert.ThrowsAsync<AccountLinkException>(() => fixture.Service.LinkAsync(fixture.Profile.Id, default));
        }
        finally
        {
            release.TrySetResult();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspection.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.DoesNotContain(RawFailure, error.ToString());
            if (disconnect is not null) await disconnect.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Null(await fixture.Inner.ReadAsync(fixture.Profile.Id, default)); Assert.Equal(0, fixture.Client.Calls);
        Assert.DoesNotContain(fixture.Vault.Writes, record => record.Health == AuthorizationHealth.Linked);
        Disposed(probe.Input); if (!failLate) Disposed(probe.Issued);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IssuedSaveAfterCancelIsAwaitedAndItsNewAuthorityFencedBeforeCompletion(bool disconnect)
    {
        await using var fixture = await Fixture.Open(Root); var entered = Signal(); var release = Signal();
        await using var probe = new Probe();
        fixture.Vault.BeforeSave = async record => { if (record.Health == AuthorizationHealth.Linked) { entered.TrySetResult(); await release.Task; } };
        fixture.Vault.IgnoreLinkedSaveCancellation = true;
        var inspection = fixture.Service.InspectHandoffAsync(fixture.Profile.Id, 42, probe, default); Task? removing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (disconnect) removing = fixture.Service.DisconnectAsync(fixture.Profile.Id, default); else fixture.Service.Cancel();
            Assert.False(inspection.IsCompleted); if (removing is not null) Assert.False(removing.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspection.WaitAsync(TimeSpan.FromSeconds(3)));
            if (removing is not null) await removing.WaitAsync(TimeSpan.FromSeconds(3));
        }
        if (disconnect) Assert.Null(await fixture.Inner.ReadAsync(fixture.Profile.Id, default));
        else
        {
            var fenced = (await fixture.Inner.ReadAsync(fixture.Profile.Id, default))!;
            Assert.Equal(AuthorizationHealth.SignInRequired, fenced.Health); Assert.Equal(3, fenced.Generation);
            Assert.Empty(fenced.SessionCookie); // Fenced reads expose no reusable material; ciphertext is retained.
            Assert.True(File.Exists(Path.Combine(Root,"accounts.vault",fixture.Profile.Id.ToString("N")+".dpapi")));
            Assert.Equal(5,await fixture.Inner.AllocateNextGenerationAsync(fixture.Profile.Id,3,default));
        }
        Disposed(probe.Input); Disposed(probe.Issued);
    }

    [Fact]
    public async Task ServiceDisposalWaitsForActualProbeCleanupAndRejectsFurtherWork()
    {
        await using var fixture = await Fixture.Open(Root); var entered = Signal(); var release = Signal();
        await using var probe = new Probe { Run = async (context, _, _) => { entered.TrySetResult(); await release.Task; return new(Captured(context), new(Current)); } };
        var inspection = fixture.Service.InspectHandoffAsync(fixture.Profile.Id, 42, probe, default); Task? disposal = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); disposal = fixture.Service.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Service.InspectHandoffAsync(fixture.Profile.Id, 42, probe, default));
        }
        finally
        {
            release.TrySetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspection.WaitAsync(TimeSpan.FromSeconds(3)));
            if (disposal is not null) await disposal.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Equal(AuthorizationHealth.SignInRequired, (await fixture.Inner.ReadAsync(fixture.Profile.Id, default))!.Health);
        Disposed(probe.Input); Disposed(probe.Issued);
    }

    [Theory]
    [InlineData("before-probe")]
    [InlineData("inside-probe")]
    [InlineData("inside-validation")]
    [InlineData("before-commit")]
    public async Task InterveningDurableGenerationCannotBeSkippedToRestoreTheOldInspection(string phase)
    {
        await using var fixture = await Fixture.Open(Root);
        fixture.Vault.BeforeAllocate = async (call, id, minimum) =>
        {
            if (phase == "before-probe" && call == 1 || phase == "before-commit" && call == 2)
                await fixture.Inner.AllocateNextGenerationAsync(id, minimum, default);
        };
        await using var probe = new Probe { Run = async (context, _, _) =>
        {
            if (phase == "inside-probe") await fixture.Inner.AllocateNextGenerationAsync(context.ProfileId, context.AuthorizationGeneration, default);
            return new(Captured(context), new(Current));
        } };
        fixture.Client.Reply = async (_, _, _) =>
        {
            if (phase == "inside-validation") await fixture.Inner.AllocateNextGenerationAsync(fixture.Profile.Id, 2, default);
            return new(Identity());
        };
        var result = await fixture.Service.InspectHandoffAsync(fixture.Profile.Id, 42, probe, default);
        Assert.Equal(RobloxHandoffProbeOutcome.AuthorizationChanged, result.Outcome);
        Assert.Equal(AuthorizationHealth.SignInRequired, (await fixture.Inner.ReadAsync(fixture.Profile.Id, default))!.Health);
        Assert.DoesNotContain(fixture.Vault.Writes, record => record.Health == AuthorizationHealth.Linked);
        Assert.Equal(phase == "before-probe" ? 0 : 1, probe.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbeExceptionsAndPostcommitProjectionFailuresExposeNoSecretAndNoFalseSuccess(bool projectionFailure)
    {
        await using var fixture = await Fixture.Open(Root); await using var probe = new Probe();
        if (projectionFailure) fixture.Vault.FailLinkedSummary = true;
        else probe.Run = (_, _, _) => throw new InvalidOperationException(RawFailure, new Exception(Original));
        var result = await fixture.Service.InspectHandoffAsync(fixture.Profile.Id, 42, probe, default);
        Assert.Equal(RobloxHandoffProbeOutcome.Failed, result.Outcome);
        string safe = JsonSerializer.Serialize(result) + result;
        Assert.DoesNotContain(RawFailure, safe); Assert.DoesNotContain(Original, safe); Assert.False(result.CanLaunch);
        var current = (await fixture.Inner.ReadAsync(fixture.Profile.Id, default))!;
        Assert.Equal(AuthorizationHealth.SignInRequired, current.Health);
        Assert.Equal(projectionFailure ? "" : Original, current.SessionCookie);
        Assert.Equal(projectionFailure ? 3 : 2, current.Generation); Disposed(probe.Input);
        if (projectionFailure) Disposed(probe.Issued);
    }

    [Fact]
    public async Task FailedSafetyInvalidationIsReportedAndTheProtectedOriginalIsPreserved()
    {
        await using var fixture = await Fixture.Open(Root); await using var probe = new Probe();
        fixture.Vault.FailLinkedSummary = true;
        fixture.Vault.BeforeAllocate = (call, _, _) => call == 3 ? throw new IOException(RawFailure) : Task.CompletedTask;
        var result = await fixture.Service.InspectHandoffAsync(fixture.Profile.Id, 42, probe, default);
        Assert.Equal(RobloxHandoffProbeOutcome.CleanupFailed, result.Outcome); Assert.False(result.CanLaunch);
        Assert.DoesNotContain(RawFailure, JsonSerializer.Serialize(result));
        var retained = (await fixture.Inner.ReadAsync(fixture.Profile.Id, default))!;
        Assert.Equal(Current, retained.SessionCookie); Assert.Equal(3, retained.Generation);
        Disposed(probe.Input); Disposed(probe.Issued);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AForeignNewerSameIdentityGrantIsPreservedRatherThanInvalidatedByOurFailure(bool afterOurCommit)
    {
        await using var fixture = await Fixture.Open(Root); const string foreign = "synthetic-foreign-newer-session";
        async Task Replace(long minimum)
        {
            long generation = await fixture.Inner.AllocateNextGenerationAsync(fixture.Profile.Id, minimum, default);
            await fixture.Inner.SaveAsync(Saved(fixture.Profile.Id) with { Generation = generation, SessionCookie = foreign, VerifiedUsername = "foreign_player" }, default);
        }
        await using var probe = new Probe { Run = async (context, _, _) =>
        {
            if (!afterOurCommit) await Replace(context.AuthorizationGeneration);
            return new(Captured(context), new(Current));
        } };
        if (afterOurCommit)
        {
            fixture.Vault.FailLinkedSummary = true;
            fixture.Vault.AfterSave = record => record.Health == AuthorizationHealth.Linked ? Replace(record.Generation) : Task.CompletedTask;
        }
        var result = await fixture.Service.InspectHandoffAsync(fixture.Profile.Id, 42, probe, default);
        Assert.NotEqual(RobloxHandoffProbeOutcome.CapturedAndPrevented, result.Outcome);
        var retained = (await fixture.Inner.ReadAsync(fixture.Profile.Id, default))!;
        Assert.Equal(AuthorizationHealth.Linked, retained.Health); Assert.Equal(foreign, retained.SessionCookie);
        Assert.Equal(afterOurCommit ? 4 : 3, retained.Generation);
        Assert.Single(fixture.Vault.Writes.Where(record => record.Health == AuthorizationHealth.SignInRequired));
        Disposed(probe.Input); Disposed(probe.Issued);
    }

    [Fact]
    public async Task ForeignReplacementAfterFinalPendingReadCannotBeOverwrittenByOurCommit()
    {
        await using var fixture=await Fixture.Open(Root);await using var probe=new Probe();
        const string foreign="synthetic-intervening-final-boundary-session";
        fixture.Vault.BeforeSave=async record=>
        {
            if(record.Health!=AuthorizationHealth.Linked)return;
            long generation=await fixture.Inner.AllocateNextGenerationAsync(record.ProfileId,2,default);
            await fixture.Inner.SaveAsync(Saved(record.ProfileId) with{Generation=generation,SessionCookie=foreign},default);
        };
        var result=await fixture.Service.InspectHandoffAsync(fixture.Profile.Id,42,probe,default);
        Assert.Equal(RobloxHandoffProbeOutcome.AuthorizationChanged,result.Outcome);
        var retained=Assert.IsType<AuthorizationRecord>(await fixture.Inner.ReadAsync(fixture.Profile.Id,default));
        Assert.Equal(3,retained.Generation);Assert.Equal(AuthorizationHealth.Linked,retained.Health);Assert.Equal(foreign,retained.SessionCookie);
        Assert.Single(fixture.Vault.Writes);Assert.Equal(AuthorizationHealth.SignInRequired,fixture.Vault.Writes[0].Health);
        Assert.False(result.CanLaunch);Disposed(probe.Input);Disposed(probe.Issued);
    }

    [Fact]
    public async Task LostCommitReceiptStillFencesItsExactOwnerAndPreservesEncryptedBytes()
    {
        await using var fixture=await Fixture.Open(Root);await using var probe=new Probe();byte[]? committedBytes=null;
        string path=Path.Combine(Root,"accounts.vault",fixture.Profile.Id.ToString("N")+".dpapi");
        fixture.Vault.AfterSave=async record=>
        {
            if(record.Health!=AuthorizationHealth.Linked)return;
            committedBytes=await File.ReadAllBytesAsync(path);throw new IOException(RawFailure);
        };
        var result=await fixture.Service.InspectHandoffAsync(fixture.Profile.Id,42,probe,default);
        Assert.Equal(RobloxHandoffProbeOutcome.Failed,result.Outcome);Assert.False(result.CanLaunch);
        var denied=Assert.IsType<AuthorizationRecord>(await fixture.Inner.ReadAsync(fixture.Profile.Id,default));
        Assert.Equal(AuthorizationHealth.SignInRequired,denied.Health);Assert.Empty(denied.SessionCookie);Assert.Equal(3,denied.Generation);
        Assert.NotNull(committedBytes);Assert.Equal(committedBytes,await File.ReadAllBytesAsync(path));
        Assert.Equal(5,await fixture.Inner.AllocateNextGenerationAsync(fixture.Profile.Id,3,default));
        Assert.DoesNotContain(RawFailure,JsonSerializer.Serialize(result));Disposed(probe.Input);Disposed(probe.Issued);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Disposed(RobloxSessionCredential? credential)
    { Assert.NotNull(credential); Assert.Throws<ObjectDisposedException>(() => credential!.CopyValue()); }

    private sealed class Fixture : IAsyncDisposable
    {
        public required AccountRepository Repository; public required AccountProfile Profile; public required ProtectedCredentialVault Inner;
        public required TrackingVault Vault; public required Client Client; public required NoSignIn SignIn;
        public required AccountIdentityProjection Projection; public required RobloxSessionLinkService Service;
        public static async Task<Fixture> Open(string root, Func<Guid, AuthorizationRecord?>? record = null)
        {
            var repository = new AccountRepository(root); await repository.InitializeAsync(default);
            var profile = await repository.SaveAsync(AccountProfile.Create("Manual alias") with { Notes = "Keep local notes" }, default);
            var inner = new ProtectedCredentialVault(root); var selected = record is null ? Saved(profile.Id) : record(profile.Id);
            if (selected is not null) await inner.SaveAsync(selected, default);
            var vault = new TrackingVault(inner); var client = new Client(); var signIn = new NoSignIn();
            var projection = new AccountIdentityProjection(root, repository, vault);
            return new() { Repository = repository, Profile = profile, Inner = inner, Vault = vault, Client = client, SignIn = signIn,
                Projection = projection, Service = new(repository, vault, client, signIn, projection) };
        }
        public async ValueTask DisposeAsync() { await Service.DisposeAsync(); Projection.Dispose(); Inner.Dispose(); Repository.Dispose(); }
    }
    private sealed class NoSignIn : IRobloxSessionSignIn
    {
        public int Calls;
        public Task<RobloxSessionCredential> SignInAsync(Guid id, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("No sign-in UI is expected during an existing-session handoff inspection."); }
    }
    private sealed class Client : IRobloxSessionClient
    {
        public int Calls; public readonly List<string> Seen = [];
        public Func<int, RobloxSessionCredential, CancellationToken, Task<RobloxSessionValidation>>? Reply;
        public Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Seen.Add(credential.CopyValue()); Calls++; return Reply?.Invoke(Calls, credential, ct) ?? Task.FromResult(new RobloxSessionValidation(Identity())); }
    }
    private sealed class Probe : IRobloxHandoffProbe
    {
        public int Calls; public RobloxSessionCredential? Input, Issued; public RobloxHandoffProbeCapture? Returned;
        public Func<RobloxHandoffProbeContext, RobloxSessionCredential, CancellationToken, Task<RobloxHandoffProbeCapture>>? Run;
        public async Task<RobloxHandoffProbeCapture> InspectAsync(RobloxHandoffProbeContext context, RobloxSessionCredential credential, CancellationToken ct)
        {
            Calls++; Input = credential;
            Returned = Run is null ? new(Captured(context), new(Current)) : await Run(context, credential, ct);
            Issued = Returned.UpdatedCredential; return Returned;
        }
        public ValueTask DisposeAsync() { Returned?.Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class TrackingVault(ProtectedCredentialVault inner) : IAccountCredentialVault, IAuthorizationGenerationStore,
        IAuthorizationMetadataReader, IRobloxAuthorizationIdentityReader, ISnapshotAuthorizationVault
    {
        public readonly List<Guid> ReadIds = []; public readonly List<AuthorizationRecord> Writes = [];
        public int InventoryReads, Allocations; public bool FailLinkedSummary, IgnoreLinkedSaveCancellation;
        public Func<AuthorizationRecord, Task>? BeforeSave, AfterSave; public Func<int, Guid, long, Task>? BeforeAllocate;
        public async Task<AuthorizationRecord?> ReadAsync(Guid id, CancellationToken ct) { ReadIds.Add(id); return await inner.ReadAsync(id, ct); }
        public Task<AuthorizationWriteSnapshot> ReadWriteSnapshotAsync(Guid id,CancellationToken ct)
        {ReadIds.Add(id);return inner.ReadWriteSnapshotAsync(id,ct);}
        public Task<AuthorizationMutationResult> TryCommitSnapshotAsync(AuthorizationWriteSnapshot expected,AuthorizationRecord replacement,Guid mutationId,CancellationToken ct)
            =>inner.TryCommitSnapshotAsync(expected,replacement,mutationId,ct);
        public Task<AuthorizationMutationResult> TryDeleteSnapshotAsync(AuthorizationWriteSnapshot expected,CancellationToken ct)
            =>inner.TryDeleteSnapshotAsync(expected,ct);
        public async Task SaveAsync(AuthorizationRecord record, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); if (BeforeSave is not null) await BeforeSave(record);
            await inner.SaveAsync(record, IgnoreLinkedSaveCancellation && record.Health == AuthorizationHealth.Linked ? default : ct); Writes.Add(record);
            if (AfterSave is not null) await AfterSave(record);
        }
        public Task DeleteAsync(Guid id, CancellationToken ct) => inner.DeleteAsync(id, ct);
        public async Task<long> AllocateNextGenerationAsync(Guid id, long minimum, CancellationToken ct)
        { Allocations++; if (BeforeAllocate is not null) await BeforeAllocate(Allocations, id, minimum); return await inner.AllocateNextGenerationAsync(id, minimum, ct); }
        public async Task<AuthorizationMutationResult> TryReplaceNextAsync(AuthorizationRecord expected, AuthorizationRecord replacement, Guid mutationId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Allocations++;if(BeforeAllocate is not null)await BeforeAllocate(Allocations,expected.ProfileId,expected.Generation);
            var committed=replacement with{Generation=checked(expected.Generation+1)};
            if(BeforeSave is not null)await BeforeSave(committed);
            var result=await inner.TryReplaceNextAsync(expected,replacement,mutationId,
                IgnoreLinkedSaveCancellation&&replacement.Health==AuthorizationHealth.Linked?default:ct);
            if(result.Status==AuthorizationMutationStatus.Applied)
            {
                Assert.Equal(committed.Generation,result.CommittedGeneration);Writes.Add(committed);
                if(AfterSave is not null)await AfterSave(committed);
            }
            return result;
        }
        public async Task<bool> TryFenceOwnedMutationAsync(Guid id,Guid mutationId,CancellationToken ct)
        {
            // Model an I/O failure at the same safety boundary as the previous regression,
            // without splitting the real store's compare/reservation/commit transaction.
            Allocations++;if(BeforeAllocate is not null)await BeforeAllocate(Allocations,id,0);
            return await inner.TryFenceOwnedMutationAsync(id,mutationId,ct);
        }
        public async Task<AuthorizationMetadata?> GetSummaryAsync(Guid id, CancellationToken ct)
        {
            var result = await inner.GetSummaryAsync(id, ct);
            if (FailLinkedSummary && result?.Health == AuthorizationHealth.Linked) throw new IOException(RawFailure);
            return result;
        }
        public Task<IReadOnlyList<AuthorizationMetadata>> ReadSummariesAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
        { InventoryReads++; throw new InvalidOperationException("No credential inventory reads are permitted in this inspection."); }
        public Task<AuthorizationMetadata?> FindByIdentityAsync(string provider, string issuer, string subject, CancellationToken ct) => inner.FindByIdentityAsync(provider, issuer, subject, ct);
        public Task<AuthorizationMetadata?> FindByRobloxUserIdAsync(long user, CancellationToken ct) => inner.FindByRobloxUserIdAsync(user, ct);
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(Root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || Path.GetFileName(full) != _name)
            throw new InvalidOperationException("Unexpected synthetic handoff fixture cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
