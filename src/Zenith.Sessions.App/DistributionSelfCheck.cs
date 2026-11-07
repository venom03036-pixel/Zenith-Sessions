using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Microsoft.Data.Sqlite;

namespace Zenith.Sessions.App;

// This mode never creates a window, registers shortcuts, opens a browser or sends input.
internal static class DistributionSelfCheck
{
    private static readonly Guid FixtureId=new("76232fdc-843e-41c9-86b2-fab45cba55c7");
    private static readonly Guid SessionFixtureId=new("67fbfa82-8738-4a7f-96a7-5290353e81aa");
    private sealed record Case(string Name,bool Passed,string? ErrorType=null,string? ErrorCode=null,string? Step=null,string? FailingMethod=null);
    private sealed class SyntheticHandoffStart : IPreparedNativeHandoffStart
    {
        public int Calls {get;private set;}
        public LaunchProcessReceipt Receipt {get;}=new(700003,DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks,DateTimeOffset.UtcNow);
        public NativeHandoffDispatchResult Start(string officialHandoff)
        {Calls++;return new(NativeHandoffDispatchOutcome.StartedUnverified,Receipt);}
        public void Dispose() { }
    }
    private sealed class SyntheticNativePreparation : IRobloxNativeHandoffCapture,IRobloxSessionClient,IRobloxSessionSignIn
    {
        public int Captures,Validations;
        public Task<NativeBrowserCapture> CaptureAsync(NativeHandoffContext context,RobloxSessionCredential credential,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();Captures++;
            if(credential.CopyValue()!="synthetic-only-native-session")throw new InvalidDataException("Unexpected synthetic preparation credential.");
            string nested="https://www.roblox.com/Game/PlaceLauncher.ashx?request=RequestGame&browserTrackerId=1234567890&placeId=6564161046&isPlayTogetherGame=false&joinAttemptId=11111111-2222-4333-8444-555555555555&joinAttemptOrigin=PlayButton";
            string original="roblox-player:1+launchmode:play+gameinfo:synthetic-nonredeemable-ticket+launchtime:"+DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
                +"+placelauncherurl:"+Uri.EscapeDataString(nested)+"+browsertrackerid:1234567890+robloxLocale:en_us+gameLocale:en_us+LaunchExp:InApp";
            var captured=NativeRobloxHandoff.Capture(context,"https://www.roblox.com/games/6564161046",RobloxHandoffProbePolicy.OfficialOrigin,original);
            if(captured.Candidate is null)throw new InvalidDataException("Synthetic native grammar failed.");
            return Task.FromResult(new NativeBrowserCapture(RobloxHandoffProbeOutcome.CapturedAndPrevented,captured.Candidate,
                new("synthetic-post-browser-session"),captured.Status));
        }
        public Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();Validations++;
            if(credential.CopyValue()!="synthetic-post-browser-session")throw new InvalidDataException("Unexpected synthetic verification credential.");
            return Task.FromResult(new RobloxSessionValidation(new(123,"Synthetic_Native",RobloxSessionIdentity.Issuer,"123")));
        }
        public Task<RobloxSessionCredential> SignInAsync(Guid profileId,CancellationToken ct)
            =>throw new InvalidOperationException("The isolated native preparation must not request interactive sign-in.");
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class SyntheticQueuePreparation(bool trial=false) : IPreparingLaunchProvider
    {
        public ProviderCapability AccountLaunch{get;}=new("Synthetic queue check",trial?CapabilityState.Unverified:CapabilityState.Supported,"Isolated fixture only");
        public int Calls,Dispatches,Disposed;
        public Task<LaunchPreparationResult> PrepareAsync(LaunchPreparationContext context,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();Calls++;return Task.FromResult(new LaunchPreparationResult(LaunchPreparationFailure.None,new Lease(this,context)));}
        public Task<ProviderLaunchResult> LaunchAsync(LaunchIntent intent,CancellationToken ct)=>throw new InvalidOperationException("Synthetic legacy dispatch must not be used.");
        public Task<bool> ConfirmArrivalAsync(LaunchIntent intent,ProviderLaunchResult observed,CancellationToken ct)=>Task.FromResult(false);
        private sealed class Lease(SyntheticQueuePreparation owner,LaunchPreparationContext context):IPreparedAccountLaunch
        {
            public LaunchPreparationContext Context=>context;
            public LaunchIntent DispatchIntent{get;}=context.OriginalIntent with{AuthorizationGeneration=context.OriginalIntent.AuthorizationGeneration+2};
            public bool IsExpired=>false;
            public async Task<ProviderLaunchResult> DispatchAsync(LaunchDispatchBoundary boundary,CancellationToken ct)
            {
                if(!boundary.IsCurrent||!boundary.ReadAdmission().Allowed||!await boundary.ValidateAsync(DispatchIntent,ct))return new(ProviderLaunchStatus.RejectedBeforeStart);
                owner.Dispatches++;return new(ProviderLaunchStatus.ClientStartedUnverified,ProcessId:700123,ProcessStartUtcTicks:DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks);
            }
            public ValueTask DisposeAsync(){owner.Disposed++;return ValueTask.CompletedTask;}
        }
    }
    private sealed class CounterClock(DateTimeOffset at):TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp()=>_ticks;
        public override DateTimeOffset GetUtcNow()=>at.AddTicks(_ticks);
        public void Advance()=>_ticks+=TimeSpan.TicksPerSecond;
    }
    private sealed class SyntheticAccountSetup : IRobloxSessionSignIn, IRobloxSessionClient
    {
        public Task<RobloxSessionCredential> SignInAsync(Guid profileId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (profileId == Guid.Empty) throw new InvalidDataException("Synthetic account setup requires its own profile identity.");
            return Task.FromResult(new RobloxSessionCredential("synthetic-only-account-setup"));
        }
        public Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (credential.CopyValue() != "synthetic-only-account-setup") throw new InvalidDataException("Unexpected synthetic account setup material.");
            return Task.FromResult(new RobloxSessionValidation(new(789, "Synthetic_Setup", RobloxSessionIdentity.Issuer, "789")));
        }
    }
    public static async Task<int> RunAsync(string root)
    {
        const string ownership="ZenithSessionsDistributionCheck/v1";
        string marker=Path.Combine(root,"distribution-check.owner"),report=Path.Combine(root,"distribution-check.json");
        var cases=new List<Case>(); string stage="Isolated storage ownership"; bool owned=false; string? sqliteVersion=null; var elapsed=Stopwatch.StartNew();
        string? cleanupPhase=null,cleanupErrorType=null,step=null;int? cleanupPending=null,cleanupRemaining=null;
        GpuTelemetrySnapshot? gpuEvidence=null;ClientExecutableTrustResult? publisherEvidence=null;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15)); var ct=deadline.Token;
        try
        {
            if(Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && (!File.Exists(marker) || File.ReadAllText(marker)!=ownership))
                throw new InvalidOperationException("The self-check folder is not an owned fixture.");
            Directory.CreateDirectory(root);
            if(!File.Exists(marker)) File.WriteAllText(marker,ownership);
            owned=true; cases.Add(new(stage,true));

            stage="Packaged brand image";
            // Decode on the entry STA thread before yielding to platform I/O.
            CheckBrandAsset(); cases.Add(new(stage,true));

            stage="Packaged production window composition";
            await PackagedLayoutCheck.VerifyAsync(Path.Combine(root,"layout-check"),ct);
            cases.Add(new(stage,true));

            using var repository=new AccountRepository(root); using var vault=new ProtectedCredentialVault(root);
            using var cleanup=new CredentialCleanupService(repository,vault); using var settings=new SettingsStore(root);
            stage="Packaged native SQLite and profile round trip";
            await repository.InitializeAsync(ct);
            var existing=await repository.ReadAsync(new(),ct);
            var pendingCleanup=await repository.ReadCredentialCleanupAsync(ct);
            if(existing.Total>2 || pendingCleanup.Total>2 || existing.Items.Any(p=>p.Id!=FixtureId && p.Id!=SessionFixtureId)
                || pendingCleanup.ProfileIds.Any(id=>id!=FixtureId && id!=SessionFixtureId))
                throw new InvalidOperationException("The self-check folder contains other profiles.");
            foreach(var prior in existing.Items) await repository.DeleteAsync(prior.Id,prior.Revision,ct);
            cleanupPhase="Prior fixture";var priorCleanup=await cleanup.RunAsync(ct);cleanupRemaining=priorCleanup.Remaining;cleanupErrorType=priorCleanup.ErrorType;
            if(priorCleanup.Remaining!=0) throw new IOException("Prior fixture cleanup failed.");
            var saved=await repository.SaveAsync(AccountProfile.Create("Distribution fixture") with { Id=FixtureId,PreferredPlaceId=6564161046,Notes="Synthetic distribution note",IsFavorite=true,AdditionalGroups="Synthetic raid\nSynthetic group" },ct);
            if((await repository.FindAsync(saved.Id,ct))!=saved) throw new InvalidDataException("The native profile round trip failed.");
            int grouped=await ((IAccountGroupEditor)repository).ApplyGroupAsync([saved],"Synthetic shared",ct);
            saved=await repository.FindAsync(saved.Id,ct)??throw new InvalidDataException("The grouped profile disappeared.");
            if(grouped!=1 || !AccountGroups.Contains(saved,"Synthetic shared") || saved.Group!="General"
                || (await repository.ReadAsync(new(Group:"Synthetic raid"),ct)).Total!=1
                || (await repository.ReadAsync(new(Group:"raid"),ct)).Total!=0
                || !(await repository.ReadGroupsAsync(ct)).Contains("Synthetic shared"))
                throw new InvalidDataException("The native multiple-membership profile check failed.");
            await using(var probe=new SqliteConnection("Data Source=:memory:"))
            { await probe.OpenAsync(ct);using var version=probe.CreateCommand();version.CommandText="SELECT sqlite_version()";sqliteVersion=(string?)await version.ExecuteScalarAsync(ct); }
            cases.Add(new(stage,true));

            stage="Library-wide group review and commit";
            var groupLifecycle=(IAccountGroupLifecycle)repository;
            var renameReview=await groupLifecycle.PreviewGroupChangeAsync(new(AccountGroupChangeKind.Rename,"Synthetic shared","Synthetic renamed"),ct);
            if(renameReview.AffectedCount!=1 || renameReview.PrimaryCount!=0 || renameReview.DestinationExists
                || await groupLifecycle.ApplyGroupChangeAsync(renameReview,ct)!=1)
                throw new InvalidDataException("The library-wide group rename failed.");
            bool staleGroupRejected=false;
            try{await groupLifecycle.ApplyGroupChangeAsync(renameReview,ct);}catch(RevisionConflictException){staleGroupRejected=true;}
            var removalReview=await groupLifecycle.PreviewGroupChangeAsync(new(AccountGroupChangeKind.Remove,"Synthetic renamed","General"),ct);
            if(!staleGroupRejected || removalReview.AffectedCount!=1 || await groupLifecycle.ApplyGroupChangeAsync(removalReview,ct)!=1)
                throw new InvalidDataException("The library-wide group removal or stale-review boundary failed.");
            saved=await repository.FindAsync(saved.Id,ct)??throw new InvalidDataException("The saved profile disappeared after group editing.");
            if(saved.Group!="General" || saved.Notes!="Synthetic distribution note" || !saved.IsFavorite
                || AccountGroups.Contains(saved,"Synthetic renamed") || !AccountGroups.Contains(saved,"Synthetic raid")
                || (await repository.ReadCredentialCleanupAsync(ct)).Total!=0 || (await repository.ReadAsync(new(),ct)).Total!=1)
                throw new InvalidDataException("A group change altered unrelated profile or cleanup state.");
            cases.Add(new(stage,true));

            stage="Independent saved group catalog";
            var catalog=(IAccountGroupCatalog)repository;
            var catalogBefore=await catalog.ReadGroupCatalogAsync(new(),ct);
            const string catalogFixture="Synthetic empty catalog fixture";
            if(await catalog.FindGroupAsync(catalogFixture,ct) is not null)
            {
                var oldEmpty=await groupLifecycle.PreviewGroupChangeAsync(new(AccountGroupChangeKind.Remove,catalogFixture,"General"),ct);
                if(oldEmpty.AffectedCount!=0)throw new InvalidDataException("The empty catalog fixture unexpectedly has members.");
                await groupLifecycle.ApplyGroupChangeAsync(oldEmpty,ct);
                catalogBefore=await catalog.ReadGroupCatalogAsync(new(),ct);
            }
            var emptyGroup=await catalog.CreateGroupAsync(catalogFixture,"#40b8a0",catalogBefore.CatalogRevision,ct);
            var catalogPage=await catalog.ReadGroupCatalogAsync(new(),ct);
            if(emptyGroup.ColorHex!="#40B8A0" || await repository.FindAsync(saved.Id,ct)!=saved
                || (await repository.ReadAsync(new(Group:catalogFixture),ct)).Total!=0)
                throw new InvalidDataException("Creating an empty saved group altered an account.");
            emptyGroup=await catalog.SetGroupColorAsync(emptyGroup,"#FFAA00",catalogPage.CatalogRevision,ct);
            catalogPage=await catalog.ReadGroupCatalogAsync(new(),ct);
            await catalog.MoveGroupAsync(emptyGroup,AccountGroupMoveDirection.Up,catalogPage.CatalogRevision,ct);
            var emptyReview=await groupLifecycle.PreviewGroupChangeAsync(new(AccountGroupChangeKind.Remove,catalogFixture,"General"),ct);
            if(!emptyReview.SourceExists || emptyReview.AffectedCount!=0 || await groupLifecycle.ApplyGroupChangeAsync(emptyReview,ct)!=0
                || await catalog.FindGroupAsync(emptyGroup.Id,ct) is not null || await repository.FindAsync(saved.Id,ct)!=saved)
                throw new InvalidDataException("The independent empty-group lifecycle failed.");
            cases.Add(new(stage,true));

            stage="Atomic settings round trip";
            await settings.LoadAsync(ct); await settings.SaveAsync(new(DenseAccounts:true,WindowLayout:new(new(GapDip:12,Columns:3),"synthetic-display")),ct);
            var savedSettings=(await settings.LoadAsync(ct)).Settings;
            if(!savedSettings.DenseAccounts || savedSettings.WindowLayout?.Tile?.GapDip!=12 || savedSettings.WindowLayout.Tile.Columns!=3
                || savedSettings.WindowLayout.PreferredDisplayId!="synthetic-display") throw new InvalidDataException("Settings round trip failed.");
            await settings.SaveAsync(new(),ct); cases.Add(new(stage,true));

            stage="Synthetic final handoff and current-grant boundary";
            // Synthetic ticket and starter only. This exercises packaged contracts/DPAPI, not a
            // browser-generated request, native process, real resource admission or Roblox account.
            var emptyNative=await vault.ReadWriteSnapshotAsync(saved.Id,ct);
            var nativeRecord=new AuthorizationRecord(saved.Id,RobloxSessionIdentity.Provider,123,emptyNative.NextGeneration,"","",DateTimeOffset.MaxValue,
                "Synthetic_Native",AuthorizationHealth.Linked,RobloxSessionIdentity.Issuer,"123","synthetic-only-native-session");
            if((await vault.TryCommitSnapshotAsync(emptyNative,nativeRecord,Guid.NewGuid(),ct)).Status!=AuthorizationMutationStatus.Applied)
                throw new InvalidDataException("Synthetic handoff preparation conflicted.");
            await using(var nativeFixture=new SyntheticNativePreparation())
            await using(var nativeService=new RobloxSessionLinkService(repository,vault,nativeFixture,nativeFixture))
            {
                var intent=new LaunchIntent(Guid.NewGuid(),saved.Id,saved.Revision,123,6564161046,0,DateTimeOffset.UtcNow,nativeRecord.Generation,HasExplicitDestination:true);
                var preparation=await nativeService.PrepareNativeHandoffAsync(intent,nativeFixture,ct);
                if(preparation.Status!=NativeHandoffPreparationStatus.Ready||preparation.Preparation is null||nativeFixture.Captures!=1||nativeFixture.Validations!=1)
                    throw new InvalidDataException("Synthetic owned native preparation failed.");
                if(preparation.BrowserFacts is not { Outcome:RobloxHandoffProbeOutcome.CapturedAndPrevented,
                    ShapeStatus:NativeHandoffCaptureStatus.AcceptedSourceGrammar })
                    throw new InvalidDataException("The fixed browser preparation facts were lost.");
                using var ready=preparation.Preparation;
                using var prepared=new SyntheticHandoffStart();
                await using(var coordinator=new NativeHandoffCoordinator(vault))
                {
                    var handoffAttempt=await coordinator.DispatchAsync(ready.Candidate,ready.FinalGrant,ready.Intent,prepared,new LaunchAuthorizationValidator(repository,vault).ValidateAsync,
                        ()=>true,()=>new(true,"Synthetic fixture reservation only"),ct);
                    if(handoffAttempt.Status!=NativeHandoffAttemptStatus.StartedUnverified||prepared.Calls!=1||handoffAttempt.ProcessReceipt!=prepared.Receipt||!ready.Candidate.IsConsumed)
                        throw new InvalidDataException("Synthetic final handoff boundary failed.");
                }
                if((await vault.TryDeleteSnapshotAsync(ready.FinalGrant,ct)).Status!=AuthorizationMutationStatus.Applied)
                    throw new InvalidDataException("Synthetic handoff cleanup conflicted.");
            }
            cases.Add(new(stage,true));

            stage="Windows protected authorization round trip";
            step="Allocate first generation";
            long firstGeneration=await vault.AllocateNextGenerationAsync(saved.Id,0,ct);
            var authorization=new AuthorizationRecord(saved.Id,"roblox-oauth",123,firstGeneration,"synthetic-only-access","synthetic-only-refresh",DateTimeOffset.UtcNow.AddMinutes(5),"fixture",
                Issuer:RobloxOAuthIdentity.Issuer,Subject:"123");
            step="Commit first protected record";await vault.SaveAsync(authorization,ct);
            step="Allocate second generation";long secondGeneration=await vault.AllocateNextGenerationAsync(saved.Id,firstGeneration,ct);
            step="Commit second protected record";await vault.SaveAsync(authorization with { Generation=secondGeneration },ct);
            step="Read final protected record";
            if((await vault.ReadAsync(saved.Id,ct))?.Generation!=secondGeneration) throw new InvalidDataException("Protected record round trip failed.");
            cases.Add(new(stage,true));step=null;

            stage="Protected private-server store round trip";
            await PackagedPrivateServerCheck.VerifyAsync(root,ct);
            cases.Add(new(stage,true));

            stage="Saved session and browser runtime boundaries";
            var sessionProfile=await repository.SaveAsync(AccountProfile.Create("synthetic session") with { Id=SessionFixtureId },ct);
            long sessionGeneration=await vault.AllocateNextGenerationAsync(sessionProfile.Id,0,ct);
            var sessionRecord=new AuthorizationRecord(sessionProfile.Id,RobloxSessionIdentity.Provider,456,sessionGeneration,"","",DateTimeOffset.MaxValue,"synthetic_session",
                Issuer:RobloxSessionIdentity.Issuer,Subject:"456",SessionCookie:"synthetic-only-session");
            await vault.SaveAsync(sessionRecord,ct);
            if((await vault.ReadAsync(sessionProfile.Id,ct))?.SessionCookie!="synthetic-only-session"
                ||(await vault.GetSummaryAsync(sessionProfile.Id,ct))?.Provider!=RobloxSessionIdentity.Provider)
                throw new InvalidDataException("The protected saved session did not round trip.");
            // Load SDK assemblies without creating an environment/browser, using a user's browser
            // profile, contacting Roblox, or claiming the installed Evergreen Runtime is available.
            if(typeof(Microsoft.Web.WebView2.Wpf.WebView2).Assembly.GetName().Name!="Microsoft.Web.WebView2.Wpf"
                ||typeof(Microsoft.Web.WebView2.Core.CoreWebView2Environment).Assembly.GetName().Name!="Microsoft.Web.WebView2.Core")
                throw new InvalidDataException("Packaged sign-in SDK assemblies are unavailable.");
            string loader=Path.Combine(AppContext.BaseDirectory,"WebView2Loader.dll");
            if(!File.Exists(loader)) loader=Path.Combine(AppContext.BaseDirectory,"runtimes","win-x64","native","WebView2Loader.dll");
            if(!System.Runtime.InteropServices.NativeLibrary.TryLoad(loader,out nint loaderHandle)) throw new InvalidDataException("The x64 browser SDK loader is unavailable.");
            System.Runtime.InteropServices.NativeLibrary.Free(loaderHandle);
            cases.Add(new(stage,true));

            stage="Conditional protected-session ownership";
            var pendingSession=sessionRecord with{Health=AuthorizationHealth.SignInRequired};
            var pendingReceipt=await vault.TryReplaceNextAsync(sessionRecord,pendingSession,Guid.NewGuid(),ct);
            if(pendingReceipt.Status!=AuthorizationMutationStatus.Applied||pendingReceipt.CommittedGeneration!=sessionGeneration+1)
                throw new InvalidDataException("The protected-session reservation failed.");
            pendingSession=pendingSession with{Generation=pendingReceipt.CommittedGeneration.Value};
            var staleReceipt=await vault.TryReplaceNextAsync(sessionRecord,sessionRecord,Guid.NewGuid(),ct);
            if(staleReceipt.Status!=AuthorizationMutationStatus.Conflict)throw new InvalidDataException("An obsolete protected session was accepted.");
            var mutationId=Guid.NewGuid();
            var linkedReceipt=await vault.TryReplaceNextAsync(pendingSession,pendingSession with{Health=AuthorizationHealth.Linked},mutationId,ct);
            if(linkedReceipt.Status!=AuthorizationMutationStatus.Applied||linkedReceipt.CommittedGeneration!=sessionGeneration+2)
                throw new InvalidDataException("The protected-session replacement failed.");
            string protectedPath=Path.Combine(root,"accounts.vault",sessionProfile.Id.ToString("N")+".dpapi");
            byte[] beforeFence=await File.ReadAllBytesAsync(protectedPath,ct);
            if(await vault.TryFenceOwnedMutationAsync(sessionProfile.Id,Guid.NewGuid(),ct)
                ||!await vault.TryFenceOwnedMutationAsync(sessionProfile.Id,mutationId,ct))
                throw new InvalidDataException("The protected-session owner fence changed unrelated or encrypted data.");
            byte[] afterFence=await File.ReadAllBytesAsync(protectedPath,ct);
            if(!beforeFence.AsSpan().SequenceEqual(afterFence))throw new InvalidDataException("The protected-session owner fence changed encrypted data.");
            var fencedSession=await vault.ReadAsync(sessionProfile.Id,ct);
            if(fencedSession?.Health!=AuthorizationHealth.SignInRequired||fencedSession.SessionCookie.Length!=0
                ||(await vault.GetSummaryAsync(sessionProfile.Id,ct))?.Health!=AuthorizationHealth.SignInRequired)
                throw new InvalidDataException("An invalidated protected session remained reusable.");
            long laterGeneration=await vault.AllocateNextGenerationAsync(sessionProfile.Id,linkedReceipt.CommittedGeneration.Value,ct);
            var laterSession=sessionRecord with{Generation=laterGeneration,SessionCookie="synthetic-only-later-session"};
            await vault.SaveAsync(laterSession,ct);
            if(await vault.TryFenceOwnedMutationAsync(sessionProfile.Id,mutationId,ct)
                ||await vault.ReadAsync(sessionProfile.Id,ct)!=laterSession)
                throw new InvalidDataException("An old operation invalidated a newer protected session.");
            cases.Add(new(stage,true));

            stage="Snapshot sign-in create and disconnect";
            var beforeSnapshot=await vault.ReadWriteSnapshotAsync(sessionProfile.Id,ct);
            var snapshotReplacement=sessionRecord with{Generation=beforeSnapshot.NextGeneration,SessionCookie="synthetic-only-snapshot-session"};
            var snapshotCommit=await vault.TryCommitSnapshotAsync(beforeSnapshot,snapshotReplacement,Guid.NewGuid(),ct);
            if(snapshotCommit.Status!=AuthorizationMutationStatus.Applied||snapshotCommit.CommittedGeneration!=snapshotReplacement.Generation
                ||(await vault.TryDeleteSnapshotAsync(beforeSnapshot,ct)).Status!=AuthorizationMutationStatus.Conflict)
                throw new InvalidDataException("An obsolete saved-session snapshot changed a newer grant.");
            var committedSnapshot=await vault.ReadWriteSnapshotAsync(sessionProfile.Id,ct);
            var removedSnapshot=await vault.TryDeleteSnapshotAsync(committedSnapshot,ct);
            var emptySnapshot=await vault.ReadWriteSnapshotAsync(sessionProfile.Id,ct);
            if(removedSnapshot.Status!=AuthorizationMutationStatus.Applied||removedSnapshot.CommittedGeneration!=committedSnapshot.NextGeneration
                ||emptySnapshot.Record is not null||emptySnapshot.HighestAllocated!=committedSnapshot.NextGeneration
                ||emptySnapshot.LastCommitted!=snapshotReplacement.Generation)
                throw new InvalidDataException("The saved-session conditional removal did not retain its single durable fence.");
            var recreatedSession=sessionRecord with{Generation=emptySnapshot.NextGeneration};
            if((await vault.TryCommitSnapshotAsync(emptySnapshot,recreatedSession,Guid.NewGuid(),ct)).Status!=AuthorizationMutationStatus.Applied
                ||(await vault.TryCommitSnapshotAsync(emptySnapshot,recreatedSession,Guid.NewGuid(),ct)).Status!=AuthorizationMutationStatus.Conflict
                ||JsonSerializer.Serialize(committedSnapshot).Contains("synthetic-only",StringComparison.Ordinal))
                throw new InvalidDataException("The saved-session snapshot allowed an obsolete create or exposed protected material.");
            cases.Add(new(stage,true));

            stage="Direct verified account creation";
            var setup = new SyntheticAccountSetup();
            using (var setupProjection = new AccountIdentityProjection(root, repository, vault))
            await using (var setupService = new RobloxSessionLinkService(repository, vault, setup, setup, setupProjection))
            {
                var beforeAdd = await repository.ReadAsync(new(PageSize: 1), ct);
                var added = await setupService.AddAsync(ct);
                var addedProfile = await repository.FindAsync(added.ProfileId, ct);
                var addedSummary = await vault.GetSummaryAsync(added.ProfileId, ct);
                if (added.Identity.UserId != 789 || addedProfile is null || addedProfile.Label != "Synthetic_Setup"
                    || addedProfile.Username != "Synthetic_Setup" || addedProfile.RobloxUserId != 789
                    || addedProfile.Group != "General" || addedProfile.PreferredPlaceId is not null
                    || addedSummary is not { VerifiedUserId: 789, Health: AuthorizationHealth.Linked })
                    throw new InvalidDataException("The verified account creation did not preserve its identity and default preferences.");
                bool duplicateRejected = false;
                try { await setupService.AddAsync(ct); }
                catch (AccountLinkException) { duplicateRejected = true; }
                if (!duplicateRejected || (await repository.ReadAsync(new(PageSize: 1), ct)).Total != beforeAdd.Total + 1)
                    throw new InvalidDataException("The duplicate saved account created another profile or authorization.");
                // Only this exact newly created synthetic profile is removed. An interrupted
                // check retains its files for review; the normal user workspace is excluded.
                await setupService.DisconnectAsync(added.ProfileId, ct);
                await repository.DeleteAsync(added.ProfileId, addedProfile.Revision, ct);
                var setupCleanup = await repository.ReadCredentialCleanupAsync(ct);
                if (setupCleanup.Total != 1 || setupCleanup.ProfileIds.Count != 1 || setupCleanup.ProfileIds[0] != added.ProfileId)
                    throw new InvalidDataException("The synthetic account cleanup did not retain its exact owned profile.");
                if ((await cleanup.RunAsync(ct)).Remaining != 0 || await vault.ReadAsync(added.ProfileId, ct) is not null
                    || await repository.FindAsync(added.ProfileId, ct) is not null)
                    throw new InvalidDataException("The synthetic account cleanup did not finish.");
            }
            cases.Add(new(stage,true));

            stage="Generation-owned identity recovery cleanup";
            using(var deletingProjection=new AccountIdentityProjection(root,repository,vault))
            using(var signingProjection=new AccountIdentityProjection(root,repository,vault))
            {
                var oldIdentitySnapshot=await vault.ReadWriteSnapshotAsync(sessionProfile.Id,ct);
                var deletionReceipt=await vault.TryDeleteSnapshotAsync(oldIdentitySnapshot,ct);
                if(deletionReceipt.Status!=AuthorizationMutationStatus.Applied||deletionReceipt.CommittedGeneration!=oldIdentitySnapshot.NextGeneration)
                    throw new InvalidDataException("The identity recovery fixture could not establish its deletion fence.");
                var missingIdentity=await vault.ReadWriteSnapshotAsync(sessionProfile.Id,ct);
                var freshIdentity=sessionRecord with{Generation=missingIdentity.NextGeneration};
                var expectedMetadata=new AuthorizationMetadata(freshIdentity.ProfileId,freshIdentity.ProfileId.ToString("N"),freshIdentity.Provider,
                    freshIdentity.Issuer,freshIdentity.Subject,freshIdentity.VerifiedUserId,freshIdentity.VerifiedUsername,freshIdentity.Generation,
                    freshIdentity.Health,freshIdentity.ExpiresUtc,DateTimeOffset.UtcNow);
                await signingProjection.PrepareAsync(expectedMetadata,ct);
                await deletingProjection.DiscardAsync(sessionProfile.Id,deletionReceipt.CommittedGeneration.Value,ct);
                if((await vault.TryCommitSnapshotAsync(missingIdentity,freshIdentity,Guid.NewGuid(),ct)).Status!=AuthorizationMutationStatus.Applied)
                    throw new InvalidDataException("The newer identity recovery fixture could not commit its selected grant.");
                await signingProjection.CompleteAsync(sessionProfile.Id,freshIdentity.Generation,ct);
                var projectedSession=await repository.FindAsync(sessionProfile.Id,ct);
                if(projectedSession?.Username!=freshIdentity.VerifiedUsername||projectedSession.RobloxUserId!=freshIdentity.VerifiedUserId
                    ||projectedSession.Label!=sessionProfile.Label||projectedSession.Revision!=sessionProfile.Revision+1
                    ||(await signingProjection.ReplayAsync(ct)).Pending!=0)
                    throw new InvalidDataException("An older cleanup removed or corrupted a newer identity recovery intent.");
                // Projection intentionally advances the profile revision. Carry that verified
                // fixture result into its later guarded deletion rather than using the old row.
                sessionProfile=projectedSession;
            }
            cases.Add(new(stage,true));

            stage="Capture-only handoff policy boundaries";
            var handoffContext=new RobloxHandoffProbeContext(FixtureId,456,1,42);
            var observedHandoff=RobloxHandoffProbePolicy.InspectExternal(handoffContext,RobloxHandoffProbePolicy.CreateGamePageUri(handoffContext),
                "https://www.roblox.com","roblox-player:1+launchmode:play+gameinfo:synthetic-package-ticket");
            if(observedHandoff.Outcome!=RobloxHandoffProbeOutcome.CapturedAndPrevented||observedHandoff.CanLaunch
                ||observedHandoff.Shape?.FieldCount!=2||!observedHandoff.NativeLaunchPrevented
                ||RobloxHandoffProbePolicy.ClassifyNavigation(handoffContext,"roblox-player:1+launchmode:play",false)!=RobloxHandoffProbeNavigation.Block
                ||RobloxHandoffProbePolicy.InspectExternal(handoffContext,"https://www.roblox.com/games/43","https://www.roblox.com","roblox-player:1+launchmode:play").Outcome!=RobloxHandoffProbeOutcome.ContextRejected)
                throw new InvalidDataException("The capture-only handoff policy did not retain its non-launch boundary.");
            var disposableHandoffCredential=new RobloxSessionCredential("synthetic-package-owned-cookie");
            using(var capture=new RobloxHandoffProbeCapture(observedHandoff,disposableHandoffCredential))
                if(JsonSerializer.Serialize(capture).Contains("synthetic-package",StringComparison.Ordinal))throw new InvalidDataException("The handoff report included protected material.");
            bool cleared=false;try{_ =disposableHandoffCredential.CopyValue();}catch(ObjectDisposedException){cleared=true;}
            if(!cleared)throw new InvalidDataException("The handoff credential owner did not dispose its returned value.");
            cases.Add(new(stage,true));

            stage="Native Windows resource sampling";
            using(var monitor=new WindowsDesktopMonitor())
            {
                if(!(await monitor.SampleAsync(ct)).Resources.IsValid) throw new InvalidDataException("Native memory/commit sampling failed.");
                await Task.Delay(1050,ct); // Rate counters need two collections on the single sampler.
                gpuEvidence=(await monitor.SampleAsync(ct)).Resources.Gpu;
                if(gpuEvidence is null || gpuEvidence.Adapters.Count>WindowsGpuTelemetryReader.MaximumAdapters
                    || gpuEvidence.Adapters.Any(adapter=>adapter.Engines.Count>WindowsGpuTelemetryReader.MaximumEnginesPerAdapter
                        || adapter.BusiestEnginePercent is { } percent && (!double.IsFinite(percent)||percent is <0 or >100)))
                    throw new InvalidDataException("GPU evidence violated the optional bounded counter contract.");
                // Unknown/unsupported is an acceptable explicit result, not proof that GPU measurement works.
            }
            using(var own=Process.GetCurrentProcess()) if(ProcessMemoryReader.Read(own.Id,own.StartTime.ToUniversalTime().Ticks) is null) throw new InvalidDataException("Native process memory counters were unavailable.");
            cases.Add(new(stage,true));

            stage="Durable guarded launch queue";
            using(var store=new LaunchQueueStore(root))
            {
                await store.SaveAsync(LaunchQueueSnapshot.Empty,ct);
                await using var queue=new LaunchCoordinator(store,new UnavailableLaunchProvider(),(_,_)=>Task.FromResult(false),_=>new(false,"Self-check: no clients may start."));
                await queue.InitializeAsync(ct);var blocked=await queue.EnqueueAsync(saved,ct);
                if(blocked.State!=LaunchState.Blocked || blocked.Problem!=LaunchProblem.ProviderUnavailable || (await store.LoadAsync(ct)).Operations.Count!=1)
                    throw new InvalidDataException("The guarded native queue round trip failed.");
                await queue.CancelAsync(blocked.Intent.OperationId,ct);
            }
            cases.Add(new(stage,true));

            stage="Durable unverified process receipt";
            using(var receiptStore=new LaunchQueueStore(root))
            {
                var at=DateTimeOffset.UtcNow;
                var intent=new LaunchIntent(FixtureId,saved.Id,saved.Revision,123,6564161046,0,at,firstGeneration);
                var receipt=new LaunchProcessReceipt(777,at.AddMinutes(-1).UtcTicks,at);
                var operation=new LaunchOperation(intent,LaunchState.ClientStartedUnverified,LaunchProblem.ClientStartedUnverified,at,receipt);
                await receiptStore.SaveAsync(new(true,[operation]),ct);
                var restored=(await receiptStore.LoadAsync(ct)).Operations.Single();
                if(restored!=operation||LaunchDescriptions.Terminal(restored.State)||!LaunchDescriptions.NeedsReservation(restored.State))
                    throw new InvalidDataException("The unverified process receipt lost its review/reservation boundary.");
                await receiptStore.SaveAsync(LaunchQueueSnapshot.Empty,ct);
            }
            cases.Add(new(stage,true));

            stage="Synthetic preparation queue and recovery";
            using(var preparationStore=new LaunchQueueStore(Path.Combine(root,"preparation-queue-fixture")))
            {
                await preparationStore.SaveAsync(LaunchQueueSnapshot.Empty,ct);
                var provider=new SyntheticQueuePreparation();
                await using(var queue=new LaunchCoordinator(preparationStore,provider,(_,_)=>Task.FromResult(true),_=>new(true,"Synthetic admission"),
                    startupDeadline:TimeSpan.FromSeconds(2),stabilization:TimeSpan.Zero,stagger:TimeSpan.Zero))
                {
                    await queue.InitializeAsync(ct);await queue.EnqueueAsync(saved,ct,new(123,7));await queue.SetPausedAsync(false,ct);
                    while(provider.Disposed==0){await Task.Delay(5,ct);}
                    var snapshot=await preparationStore.LoadAsync(ct);var row=snapshot.Operations.Single();
                    if(provider.Calls!=1||provider.Dispatches!=1||row.State!=LaunchState.ClientStartedUnverified||row.Intent.AuthorizationGeneration!=9||row.ProcessReceipt is null)
                        throw new InvalidDataException("The synthetic prepared dispatch lost its owned intent or receipt.");
                }
                var at=DateTimeOffset.UtcNow;
                var interrupted=new LaunchOperation(new(Guid.NewGuid(),saved.Id,saved.Revision,123,6564161046,0,at,7),LaunchState.Preparing,LaunchProblem.None,at);
                await preparationStore.SaveAsync(new(false,[interrupted]),ct);
                var recoveryProvider=new SyntheticQueuePreparation();
                await using(var queue=new LaunchCoordinator(preparationStore,recoveryProvider,(_,_)=>Task.FromResult(true),_=>new(true,"Synthetic admission")))
                {
                    await queue.InitializeAsync(ct);var recovered=await preparationStore.LoadAsync(ct);var row=recovered.Operations.Single();
                    if(!recovered.Paused||row.State!=LaunchState.Blocked||row.Problem!=LaunchProblem.PreparationInterrupted||LaunchDescriptions.NeedsReservation(row.State)||recoveryProvider.Calls!=0)
                        throw new InvalidDataException("Interrupted preparation was replayed or retained a client reservation.");
                }
                await preparationStore.SaveAsync(LaunchQueueSnapshot.Empty,ct);
            }
            cases.Add(new(stage,true));

            stage="Synthetic trial consent and reservations";
            using(var trialStore=new LaunchQueueStore(Path.Combine(root,"trial-queue-fixture")))
            {
                // This self-check's marker-owned synthetic queue is intentionally reused across
                // runs. Reset its fixture intent, never append another account trial to history.
                await trialStore.SaveAsync(LaunchQueueSnapshot.Empty,ct);
                var provider=new SyntheticQueuePreparation(true);
                await using var queue=new LaunchCoordinator(trialStore,provider,(_,_)=>Task.FromResult(true),_=>new(true,"Synthetic capacity"),
                    stabilization:TimeSpan.Zero,stagger:TimeSpan.Zero);
                await queue.InitializeAsync(ct);
                var request=await queue.EnqueueTrialAsync(saved,ct,new(123,7),new(6564161046));
                if(!await queue.ResumeTrialAsync(request.Intent.OperationId,ct))throw new InvalidDataException("Synthetic trial consent was lost.");
                while(provider.Disposed==0)await Task.Delay(5,ct);
                var snapshot=await trialStore.LoadAsync(ct);var row=snapshot.Operations.Single();var at=DateTimeOffset.UtcNow;
                if(row.State!=LaunchState.ClientStartedUnverified||row.ProcessReceipt is not { } receipt||provider.AccountLaunch.State!=CapabilityState.Unverified)
                    throw new InvalidDataException("An explicit trial incorrectly upgraded capability or lost its receipt.");
                var desktop=new DesktopSnapshot(new(at,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,64*CapacityPolicy.GiB,10*CapacityPolicy.GiB,10),
                    [new(receipt.ProcessId,receipt.ProcessStartUtcTicks,"synthetic",0,"synthetic",false,true,(long)CapacityPolicy.GiB,(long)CapacityPolicy.GiB,0,at,
                        PrivateWorkingSetBytes:(long)CapacityPolicy.GiB)],ProcessObservedUtc:at,ProcessRevision:1,ProcessInventoryComplete:true);
                var projected=LaunchReservationProjection.Evaluate(snapshot,desktop,new(),at);
                if(projected.OccupiedForAdmission!=1||projected.CreditedReservations!=1||!projected.SafelyObservedOperationIds.Contains(request.Intent.OperationId))
                    throw new InvalidDataException("Synthetic process consumption was not projected conservatively.");
                var evidence=NativeTrialEvidence.Project(snapshot,request.Intent.OperationId,desktop,[],at);
                if(evidence.Presence!=NativeTrialProcessPresence.Observed||evidence.ExpectedUserId!=123||evidence.RequestedPlaceId!=6564161046
                    ||evidence.ReportedUser.UserId is not null||evidence.Outcome!=NativeTrialEvidenceOutcome.Unknown
                    ||evidence.ArrivalVerified||evidence.CanAuthorizeAccountAction||evidence.CanRouteInput||!queue.ReadPacing().Ready)
                    throw new InvalidDataException("Synthetic process evidence incorrectly established an account or arrival.");
                queue.SignalPanic();if(queue.IsTrialOperation(request.Intent.OperationId))throw new InvalidDataException("Panic retained trial permission.");
                await queue.AcknowledgeClientClosedAsync(request.Intent.OperationId,ct);
                if((await trialStore.LoadAsync(ct)).Operations.Single().Problem!=LaunchProblem.ClientClosureAcknowledged)
                    throw new InvalidDataException("Explicit closure acknowledgment was not persisted.");
            }
            cases.Add(new(stage,true));

            stage="Pinned resource forecasts and candidate admission";
            using(var budgetStore=new LaunchQueueStore(Path.Combine(root,"resource-budget-check")))
            {
                var at=DateTimeOffset.UtcNow;
                var first=new LaunchOperation(new(Guid.NewGuid(),Guid.NewGuid(),1,123,42,0,at,7),LaunchState.Uncertain,LaunchProblem.Interrupted,at,
                    ResourceBudget:new(4*CapacityPolicy.GiB,5*CapacityPolicy.GiB));
                var second=first with{Intent=first.Intent with{OperationId=Guid.NewGuid(),ProfileId=Guid.NewGuid()},ResourceBudget=new(2*CapacityPolicy.GiB,3*CapacityPolicy.GiB)};
                await budgetStore.SaveAsync(new(true,[first,second]),ct);
                var restored=await budgetStore.LoadAsync(ct);
                var system=new ResourceSnapshot(at,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,64*CapacityPolicy.GiB,10*CapacityPolicy.GiB,10);
                var desktop=new DesktopSnapshot(system,[],ProcessObservedUtc:at,ProcessRevision:1,ProcessInventoryComplete:true);
                var changed=new CapacitySettings(NewClientPhysicalGiB:.1,NewClientCommitGiB:.1);
                var budgetProjection=LaunchReservationProjection.Evaluate(restored,desktop,changed,at);
                if(!budgetProjection.ForecastsKnown||budgetProjection.Reservations.Count!=2||budgetProjection.Reservations[0].PhysicalBytes!=4*CapacityPolicy.GiB
                    ||budgetProjection.Reservations[0].CommitBytes!=5*CapacityPolicy.GiB||budgetProjection.Reservations[1].PhysicalBytes!=2*CapacityPolicy.GiB)
                    throw new InvalidDataException("A settings change rewrote a request's resource forecast.");
                var tight=system with{AvailablePhysical=8*CapacityPolicy.GiB,CommitUsed=55*CapacityPolicy.GiB};
                if(CapacityPolicy.Evaluate(tight,changed,[],0,at,first.ResourceBudget).MemoryOnlySlots!=0)
                    throw new InvalidDataException("An expensive candidate borrowed the cheaper current default.");
                var legacy=LaunchReservationProjection.Evaluate(new(true,[first with{ResourceBudget=null}]),desktop,changed,at);
                if(legacy.ForecastsKnown||legacy.Reservations.Count!=1)
                    throw new InvalidDataException("A legacy request invented its missing original forecast.");
                var huge=new CapacitySettings(NewClientPhysicalGiB:1024,NewClientCommitGiB:1024,UserMaximum:1);
                if(new ResourcePressureTracker().Observe(system,CapacityPolicy.EvaluateSystemPressure(system,huge,at),at).Held)
                    throw new InvalidDataException("A default estimate incorrectly latched system pressure.");
            }
            cases.Add(new(stage,true));

            stage="Recovered queue generation and no replay";
            var recoveryRoot=Path.Combine(root,"queue-generation-fixture");
            using(var recoveryStore=new LaunchQueueStore(recoveryRoot))
            {
                var recoveryAt=DateTimeOffset.UtcNow;
                var recoveryOperation=new LaunchOperation(new(Guid.NewGuid(),saved.Id,saved.Revision,123,42,0,recoveryAt.AddMinutes(-2),7),
                    LaunchState.ClientStartedUnverified,LaunchProblem.ClientStartedUnverified,recoveryAt,new(700008,recoveryAt.AddMinutes(-1).UtcTicks,recoveryAt),
                    new(4*CapacityPolicy.GiB,5*CapacityPolicy.GiB));
                var recoveryBefore=new LaunchQueueSnapshot(false,[recoveryOperation]);
                await recoveryStore.SaveAsync(recoveryBefore,ct);await recoveryStore.SaveAsync(recoveryBefore,ct);
                string recoveryPrimary=Path.Combine(recoveryRoot,"launch-queue.json");
                byte[] recoveryBackup=await File.ReadAllBytesAsync(recoveryPrimary+".bak",ct);
                // This is the exact primary just written inside the already-owned self-check
                // folder, not a user file. Simulate interrupted replacement without live clients.
                File.Delete(recoveryPrimary);
                var recoveryLoaded=await recoveryStore.LoadAsync(ct);
                if(!recoveryLoaded.Paused||recoveryLoaded.Operations.Count!=1||recoveryLoaded.Operations[0]!=recoveryOperation||recoveryStore.RecoveryNotice is null||File.Exists(recoveryPrimary))
                    throw new InvalidDataException("A missing primary hid or resumed its verified backup.");
                await using var restoredQueue=new LaunchCoordinator(recoveryStore,new UnavailableLaunchProvider(),
                    (_,_)=>throw new InvalidDataException("Recovered intent must not request authorization."),_=>new(false,"Synthetic review only"));
                await restoredQueue.InitializeAsync(ct);var recoveryReviewed=await recoveryStore.LoadAsync(ct);
                byte[] recoveryRetainedBackup=await File.ReadAllBytesAsync(recoveryPrimary+".bak",ct);
                // A receipted, budgeted started client stays that reviewable started client after
                // restart (CAP-14-03), so only an exact fresh observation can reconcile it.
                if(!recoveryReviewed.Paused||recoveryReviewed.Operations.Single()!=recoveryOperation
                    ||!LaunchDescriptions.HasUnresolvedReservation(recoveryReviewed.Operations[0])
                    ||!recoveryBackup.AsSpan().SequenceEqual(recoveryRetainedBackup))
                    throw new InvalidDataException("Recovered receipt or forecast lost its review boundary.");
            }
            cases.Add(new(stage,true));

            stage="Bounded typed diagnostics journal";
            await using(var diagnostics=new ManagerDiagnostics(root))
            {
                for(int index=0;index<520;index++) diagnostics.Record(ManagerEventKind.OperationFailed,typeof(IOException));
                var snapshot=diagnostics.GetSnapshot();
                if(snapshot.Events.Count!=512 || snapshot.TotalEvents!=520 || snapshot.ErrorCount!=520 || snapshot.Events[0].Sequence!=9 || snapshot.Events[^1].Sequence!=520)
                    throw new InvalidDataException("The packaged diagnostic journal exceeded its bound or lost sequence coherence.");
                await diagnostics.DisposeAsync();
                if(diagnostics.GetSnapshot().Failure is not null) throw new IOException("Packaged diagnostic persistence failed.");
            }
            cases.Add(new(stage,true));

            stage="Bounded diagnostic session projection";
            var observedAt=DateTimeOffset.UtcNow;
            var observedProcess=new SessionObservation(123,observedAt.AddSeconds(-5).UtcTicks,"Synthetic fixture",0,"",false,true,0,0,null,observedAt);
            var processKey=new SessionProcessKey(null,observedProcess.ProcessId,observedProcess.ProcessStartUtcTicks);
            var stream=new ClientLogStreamId(Guid.NewGuid(),1);
            var fact=new ClientLogObservation(stream,0,10,observedAt,observedAt,ClientLogFact.UserIdentityObserved,UserId:123);
            var batch=new ClientLogBatch(stream,ClientLogReadStatus.Ready,ClientLogDiscontinuity.Initial,10,10,false,0,[fact]);
            var projection=new SessionLogProjection();
            projection.Update([observedProcess],new(1,observedAt,[new(processKey,SessionLogCorrelationStatus.ResourceHolderObserved,
                stream,batch,observedAt,observedAt.AddSeconds(10))],1,false),observedAt);
            var diagnostic=projection.Snapshot(observedAt)[processKey];
            if(diagnostic.ReportedUserId!="123" || diagnostic.CanAuthorizeAccountAction
                || !diagnostic.Correlation.Contains("unverified",StringComparison.Ordinal)
                || projection.Snapshot(observedAt.AddSeconds(10))[processKey].ReportedUserId!="Unknown")
                throw new InvalidDataException("Packaged diagnostic evidence did not retain its authorization/freshness boundary.");
            cases.Add(new(stage,true));

            stage="Recent client history metadata round trip";
            using(var historyStore=new RecentClientHistoryStore(root))
            {
                var priorHistory=await historyStore.LoadAsync(ct);
                if(priorHistory.Entries.Any(entry=>entry.Process.ProcessId!=10001)) throw new InvalidDataException("The self-check history contains other process records.");
                var emptyHistory=new RecentClientHistorySnapshot(1,checked(priorHistory.Revision+1),Array.Empty<RecentClientHistoryEntry>());
                await historyStore.SaveAsync(emptyHistory,ct);
                var history=new RecentClientHistoryReducer();history.Restore(emptyHistory);
                var time=DateTimeOffset.UtcNow;
                var process=new SessionObservation(10001,time.AddMinutes(-1).UtcTicks,"Synthetic",0,"Synthetic",false,false,1,1,null,time);
                var resources=new ResourceSnapshot(time,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,64*CapacityPolicy.GiB,20*CapacityPolicy.GiB,10);
                history.Observe(new(resources,new[]{process},ProcessObservedUtc:time,ProcessRevision:1,ProcessInventoryComplete:true),Array.Empty<SessionEvidenceSnapshot>(),time);
                history.RecordCloseRequest(new(null,process.ProcessId,process.ProcessStartUtcTicks),time.AddMilliseconds(100));
                history.Observe(new(resources,Array.Empty<SessionObservation>(),ProcessObservedUtc:time.AddSeconds(1),ProcessRevision:2,ProcessInventoryComplete:true),Array.Empty<SessionEvidenceSnapshot>(),time.AddSeconds(1));
                await historyStore.SaveAsync(history.PersistedSnapshot(),ct);
                var ended=(await historyStore.LoadAsync(ct)).Entries.Single();
                if(ended.Process.ProcessId!=10001 || ended.NoLongerObservedUtc!=time.AddSeconds(1)
                    || ended.CloseRequestedUtc!=time.AddMilliseconds(100) || ended.CanAuthorizeAccountAction || ended.Cause!="Unknown")
                    throw new InvalidDataException("Packaged history lost its process-instance, request-only or no-authority boundary.");
            }
            cases.Add(new(stage,true));

            stage="Public game metadata and capacity boundaries";
            using(var transport=new PublicGameFixtureHandler())
            using(var publicHttp=new HttpClient(transport) {Timeout=TimeSpan.FromSeconds(2)})
            {
                var directory=new RobloxPublicDirectory(publicHttp);
                var metadata=await directory.LookupGameAsync(6564161046,ct);
                if(metadata.State!=PublicDirectoryState.Success || metadata.Value is not { } game
                    || game.RequestedPlaceId!=6564161046 || game.RootPlaceId!=100 || game.Playing!=3000000000L || transport.Requests!=2)
                    throw new InvalidDataException("Packaged public metadata changed the requested subplace or lost the wide player count.");
                var now=DateTimeOffset.UtcNow;const string job="11111111-2222-4333-8444-000000000001";
                var destination=new RobloxRequestedDestination(6564161046,job,RobloxServerKind.Public);
                var observation=new RobloxPublicServer(6564161046,job,18,20,null,null,now);
                if(RequestedServerCapacity.Assess(destination,observation,3,now).State!=RequestedServerCapacityState.InsufficientSlots
                    || RequestedServerCapacity.Assess(destination,observation,3,now.AddMinutes(6)).State!=RequestedServerCapacityState.Stale
                    || RequestedServerCapacity.Assess(destination,observation with {PlaceId=100},3,now).State!=RequestedServerCapacityState.Unknown)
                    throw new InvalidDataException("Packaged capacity advice lost its recipient, place or freshness boundary.");
            }
            cases.Add(new(stage,true));

            stage="Public game search and destination isolation";
            await PackagedGameSearchCheck.VerifyAsync(ct);
            cases.Add(new(stage,true));

            stage="Atomic destination favorites round trip";
            using(var favorites=new FavoriteDestinationsStore(root))
            {
                var priorFavorites=await favorites.LoadAsync(ct);
                if(priorFavorites.Destinations.Any(item=>item.Id!=FixtureId)) throw new InvalidDataException("The self-check favorites contain other records.");
                var existingFavorite=priorFavorites.Destinations.SingleOrDefault();
                var favorite=await favorites.SaveAsync(existingFavorite is null
                    ?new FavoriteDestination(FixtureId,"Synthetic distribution destination",6564161046)
                    :existingFavorite with {Name="Synthetic distribution destination"},ct);
                var restored=(await favorites.LoadAsync(ct)).Destinations.Single();
                if(restored!=favorite || restored.RequestedDestination.ServerKind!=RobloxServerKind.Unknown)
                    throw new InvalidDataException("Favorites metadata did not preserve its requested-only semantics.");
                await favorites.DeleteAsync(favorite.Id,favorite.Revision,favorite.ChangeId,ct);
                if((await favorites.LoadAsync(ct)).Destinations.Count!=0) throw new InvalidDataException("Favorite deletion failed.");
            }
            cases.Add(new(stage,true));

            stage="Recipient bundle metadata round trip";
            await PackagedRecipientBundleCheck.VerifyAsync(root,FixtureId,SessionFixtureId,ct);
            cases.Add(new(stage,true));

            stage="Physical client layout planner";
            var display=new ClientDisplay("synthetic-display","Synthetic display",new(-3840,-100,3840,2160),200,true);
            var layout=ClientWindowTilePlanner.Plan(display,16,new());
            if(!layout.Fits || layout.Cells.Count!=16 || layout.Cells.Any(cell=>!display.WorkArea.Contains(cell) || cell.Width<640 || cell.Height<480))
                throw new InvalidDataException("The physical layout violated its declared workarea or logical minimums.");
            cases.Add(new(stage,true));

            stage="Physical content-size planner and invalid-client guard";
            var resizeDisplay=new ClientDisplay("synthetic-resize-display","Synthetic resize display",new(-1280,0,1280,720),150,false);
            var geometry=new ClientWindowGeometry(new(-1200,100,656,519),new(-1192,131,640,480),resizeDisplay);
            var resizePlan=ClientWindowResizePlanner.Plan(geometry,new(320,240));
            if(!resizePlan.Fits||resizePlan.OuterBounds!=new PixelRectangle(-1200,100,336,279)
                ||ClientWindowResizePlanner.Plan(geometry,new(8192,8192)).Fits)
                throw new InvalidDataException("The physical content-size planner guessed scale or exceeded its workarea.");
            using(var resizeMonitor=new WindowsDesktopMonitor())
            {
                var invalid=new SessionObservation(0,0,"Synthetic invalid target",0,"",false,true,0,0,null,DateTimeOffset.UtcNow);
                var declined=await resizeMonitor.ResizeAsync(new[]{invalid},new(320,240),ct);
                if(declined.AllApplied||declined.Results.Count!=1||declined.Results[0].Status!=ClientWindowPlacementStatus.InvalidClient)
                    throw new InvalidDataException("A missing client acquired resize permission.");
            }
            cases.Add(new(stage,true));

            stage="Portable manager performance round trip";
            var localMain=Guid.NewGuid();
            var portablePerformance=new PortablePerformanceSettings(ManagerPerformanceProfile.MainAccountPriority,2500,1500,6000,40000);
            var sharedPerformance=PerformanceSettingsTransfer.EncodeShareCode(portablePerformance,ct);
            var performanceReview=PerformanceSettingsTransfer.DecodeShareCode(sharedPerformance,ct).ToReviewSettings(new(MainAccountProfileId:localMain));
            if(performanceReview.MainAccountProfileId!=localMain || performanceReview.CustomProcessSampleMs!=2500
                || performanceReview.CustomStabilizationMs!=40000 || performanceReview.Resolve().MainAccountPriorityBoost!=100
                || System.Text.Encoding.UTF8.GetString(PerformanceSettingsTransfer.SerializeJson(portablePerformance,ct)).Contains(localMain.ToString(),StringComparison.Ordinal))
                throw new InvalidDataException("Portable manager settings changed local account selection or lost timing values.");
            cases.Add(new(stage,true));

            stage="Read-only installation inventory";
            // Only our own packaged executable is copied as an inert metadata fixture. It is never run.
            // Its Roblox-shaped filename must remain unverified, even though version metadata is readable.
            string inventoryRoot=Path.Combine(root,"installation-metadata-fixture"),versionsRoot=Path.Combine(inventoryRoot,"Roblox","Versions");
            string inventoryVersion=Path.Combine(versionsRoot,"version-"+Guid.NewGuid().ToString("N"));
            string inventoryFile=Path.Combine(inventoryVersion,"RobloxPlayerBeta.exe");
            Directory.CreateDirectory(inventoryVersion);
            try
            {
                File.Copy(Path.Combine(AppContext.BaseDirectory,"Zenith.Sessions.App.exe"),inventoryFile);
                var discovery=new WindowsClientInstallationDiscovery(new WindowsClientInstallationInventorySource(inventoryRoot,includeCurrentUserRegistry:false));
                var inventory=await discovery.DiscoverAsync(ct);
                var candidate=inventory.Candidates.SingleOrDefault(item=>string.Equals(item.ExecutablePath,inventoryFile,StringComparison.OrdinalIgnoreCase));
                if(candidate is null || candidate.FileState!=ClientInstallationFileState.PresentUnverified || candidate.FileVersion is null
                    || candidate.Authenticity!=CapabilityState.Unverified || candidate.NamedAccountLaunch!=CapabilityState.Unavailable)
                    throw new InvalidDataException("The packaged metadata inventory failed or upgraded a filename hint to launch authority.");
                cases.Add(new(stage,true));
                stage="Selected-file publisher boundary";
                await using var verifier=new WindowsClientExecutableTrust();
                publisherEvidence=await verifier.VerifyAsync(candidate,ct);
                if(publisherEvidence.NativeLaunchAuthorized || publisherEvidence.Status==ClientExecutableTrustStatus.VerifiedRobloxPublisher
                    || publisherEvidence.Reason==ClientExecutableTrustReason.Verified)
                    throw new InvalidDataException("An inert renamed Zenith fixture acquired Roblox publisher authority.");
            }
            finally {if(File.Exists(inventoryFile)) File.Delete(inventoryFile);if(Directory.Exists(inventoryVersion)) Directory.Delete(inventoryVersion);}
            cases.Add(new(stage,true));

            stage="Process I/O counters and telemetry boundaries";
            using(var ownProcess=Process.GetCurrentProcess())
                if(ProcessIoReader.Read(ownProcess.Id,ownProcess.StartTime.ToUniversalTime().Ticks,ct) is null)
                    throw new InvalidDataException("Owned-process I/O counters were unavailable.");
            // Synthetic arithmetic/history is separate from the owned query. No Roblox client,
            // account or server identity is assigned, and no input/window is touched.
            var ioClock=new CounterClock(DateTimeOffset.UtcNow);var rateTracker=new ProcessIoRateTracker(ioClock);
            var ioKey=new SessionProcessKey(null,77,ioClock.GetUtcNow().AddMinutes(-1).UtcTicks);
            var warming=rateTracker.Observe(ioKey,new(1,2,3,4,5,6),ct);
            if(warming.ReadBytesPerSecond is not null) throw new InvalidDataException("A first counter reading invented a rate.");
            ioClock.Advance();var measuredIo=rateTracker.Observe(ioKey,new(4097,2050,1027,14,15,16),ct);
            if(measuredIo.ReadBytesPerSecond!=4096 || measuredIo.WriteBytesPerSecond!=2048 || measuredIo.OtherBytesPerSecond!=1024)
                throw new InvalidDataException("Process I/O deltas were inconsistent.");
            var telemetry=new TelemetryHistory();var telemetryAt=ioClock.GetUtcNow();
            var telemetryFrame=new DesktopSnapshot(new(telemetryAt,16*CapacityPolicy.GiB,10*CapacityPolicy.GiB,32*CapacityPolicy.GiB,8*CapacityPolicy.GiB,20),
                [new(ioKey.ProcessId,ioKey.ProcessStartUtcTicks,"Synthetic",0,"",false,false,1024,2048,10,telemetryAt,IoRates:measuredIo)],
                ProcessObservedUtc:telemetryAt,ProcessRevision:1,ProcessInventoryComplete:true);
            if(!telemetry.Observe(telemetryFrame,telemetryAt)) throw new InvalidDataException("A measured history point was lost.");
            var telemetryClient=telemetry.GetClientSnapshot(ioKey);
            if(telemetryClient?.Points.Count!=1 || telemetryClient.Points[0].IoRates?.ReadBytesPerSecond!=4096
                || telemetry.Observe(telemetryFrame,telemetryAt.AddSeconds(1)) || !ReferenceEquals(telemetryClient,telemetry.GetClientSnapshot(ioKey)))
                throw new InvalidDataException("Cached telemetry renewed or altered a measurement.");
            cases.Add(new(stage,true));

            stage="Preview runtime and invalid target boundaries";
            // Load the exact packaged projections, but never create a capture item/device or inspect
            // a real window. A zero HWND is rejected before any desktop/native target operation.
            var sdk=AssemblyName.GetAssemblyName(Path.Combine(AppContext.BaseDirectory,"Microsoft.Windows.SDK.NET.dll"));
            var winrt=AssemblyName.GetAssemblyName(Path.Combine(AppContext.BaseDirectory,"WinRT.Runtime.dll"));
            if(sdk.Name!="Microsoft.Windows.SDK.NET" || winrt.Name!="WinRT.Runtime") throw new InvalidDataException("Preview projection files are missing.");
            var invalidPreviewTarget=new ClientPreviewTarget(new(null,1,DateTimeOffset.UtcNow.UtcTicks),0);
            await using(var previews=new WindowsClientPreviewProvider())
            {
                var preview=await previews.CaptureAsync([invalidPreviewTarget],1,ct);
                if(preview.Count!=1 || preview[0].Target!=invalidPreviewTarget || preview[0].Status!=ClientPreviewStatus.InvalidClient || preview[0].Frame is not null)
                    throw new InvalidDataException("An invalid preview target was not rejected.");
            }
            cases.Add(new(stage,true));

            stage="Controlled targeted input ownership";
            await PackagedTargetedInputCheck.VerifyAsync(ct);
            cases.Add(new(stage,true));

            stage="Read-only Personal macro catalog pipe";
            await PackagedMacroHostLinkCheck.VerifyAsync(ct);
            cases.Add(new(stage,true));

            stage="Durable shared macro assignments";
            await PackagedMacroAssignmentsCheck.VerifyAsync(root,ct);
            cases.Add(new(stage,true));

            stage="Public headshot and ordered gallery boundaries";
            await PackagedPublicMediaCheck.VerifyAsync(ct);
            cases.Add(new(stage,true));

            stage="All-observed tiled Windows capture scheduling";
            await PackagedWindowsOverviewCheck.VerifyAsync(ct);
            cases.Add(new(stage,true));

            stage="Durable credential cleanup";
            await repository.DeleteAsync(saved.Id,saved.Revision,ct);
            await repository.DeleteAsync(sessionProfile.Id,sessionProfile.Revision,ct);
            cleanupPhase="Final fixture";cleanupPending=(await repository.ReadCredentialCleanupAsync(ct)).Total;
            var finalCleanup=await cleanup.RunAsync(ct);cleanupRemaining=finalCleanup.Remaining;cleanupErrorType=finalCleanup.ErrorType;
            if(cleanupPending!=2 || finalCleanup.Remaining!=0 || await vault.ReadAsync(saved.Id,ct) is not null
                || await vault.ReadAsync(sessionProfile.Id,ct) is not null)
                throw new InvalidDataException("Credential cleanup failed.");
            cases.Add(new(stage,true));
        }
        catch(Exception ex)
        {
            var failure=ex.GetBaseException();var method=failure.TargetSite;
            // Static phase/code/method only: never persist exception messages, arguments,
            // raw paths, authorization material or a raw exception/stack dump.
            cases.Add(new(stage,false,ex.GetType().Name,$"0x{failure.HResult:X8}",step,method is null?null:$"{method.DeclaringType?.FullName}.{method.Name}"));
        }
        if(!owned) { Console.WriteLine("Distribution self-check refused an unowned data folder."); return 2; }
        // Not the stage deadline: the report must still be written after a stage times out. Six local reads are bounded.
        var identity=await IdentifyBuildAsync(AppContext.BaseDirectory,AppDomain.CurrentDomain.GetAssemblies(),CancellationToken.None);
        var result=new { FormatVersion=1,DevelopmentSelfCheck=true,ReleaseReady=false,CheckedUtc=DateTimeOffset.UtcNow,
            AssemblySha256=identity.AssemblySha256,FirstPartySha256=identity.FirstPartySha256,FirstPartyIdentitySha256=identity.FirstPartyIdentitySha256,
            Runtime=Environment.Version.ToString(),SQLiteVersion=sqliteVersion,ElapsedMs=elapsed.ElapsedMilliseconds,Cases=cases,
            GpuObservation=gpuEvidence,
            PublisherBoundary=new { Status=publisherEvidence?.Status.ToString(),Reason=publisherEvidence?.Reason.ToString(),NativeLaunchAuthorized=false },
            CleanupDiagnostic=new { Phase=cleanupPhase,Pending=cleanupPending,Remaining=cleanupRemaining,ErrorType=cleanupErrorType } };
        string temporary=report+".tmp";
        try { await File.WriteAllTextAsync(temporary,JsonSerializer.Serialize(result,new JsonSerializerOptions { WriteIndented=true })); File.Move(temporary,report,true); }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
        Console.WriteLine(Summary(cases.Count(c=>c.Passed),PlannedStageCount,cases.FirstOrDefault(c=>!c.Passed)?.Name));
        return cases.All(c=>c.Passed)?0:1;
    }

    // Every stage RunAsync codes. Keep in step with $expectedCases in scripts/Check-ZenithSessionsManager.ps1.
    internal const int PlannedStageCount=43;
    // The run stops at the first failing stage, so the console line counts against every planned stage and names the
    // failed one: "3/42 (failed at ...)" instead of "3/4". Stage names are static literals (no paths or secrets).
    // The success line is unchanged so stored "42/42" evidence still matches.
    internal static string Summary(int passed,int planned,string? failedStage)
        =>failedStage is null
            ?$"Distribution self-check: {passed}/{planned}; release readiness remains false."
            :$"Distribution self-check: {passed}/{planned} (failed at {failedStage}); release readiness remains false.";

    // Builds are deterministic, so a Core- or Windows-only change leaves Zenith.Sessions.App.dll byte-identical and an
    // App-only hash cannot tell an old report from the fixed build. The report identifies every first-party assembly the
    // packaged check exercises. Keep this list in step with $firstParty in scripts/Check-ZenithSessionsManager.ps1.
    internal static readonly string[] FirstPartyAssemblies=["Zenith.Sessions.App.dll","Zenith.Sessions.Core.dll","Zenith.Sessions.Windows.dll",
        "Zenith.Automation.Input.dll","Zenith.MacroHost.Contracts.dll","Zenith.MacroHost.Windows.dll"];
    // AssemblySha256 keeps its historical meaning (the App dll alone) so older reports and ledger entries stay comparable.
    internal sealed record BuildIdentity(string AssemblySha256,IReadOnlyDictionary<string,string> FirstPartySha256,string FirstPartyIdentitySha256);

    internal static async Task<BuildIdentity> IdentifyBuildAsync(string baseDirectory,IEnumerable<Assembly> loaded,CancellationToken ct)
    {
        // Hash the copies that actually ran. A first-party assembly loaded from any other file would make the hashes describe other code.
        string folder=Path.GetFullPath(baseDirectory);
        foreach(var assembly in loaded)
        {
            if(assembly.IsDynamic) continue;
            string file=assembly.GetName().Name+".dll";
            if(Array.IndexOf(FirstPartyAssemblies,file)<0) continue;
            if(string.IsNullOrEmpty(assembly.Location) || !string.Equals(Path.GetFullPath(assembly.Location),Path.Combine(folder,file),StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The first-party assembly {file} was not loaded from the checked folder.");
        }
        var hashes=await HashFirstPartyAssembliesAsync(folder,ct);
        return new(hashes[FirstPartyAssemblies[0]],hashes,CombinedIdentity(hashes));
    }

    internal static async Task<IReadOnlyDictionary<string,string>> HashFirstPartyAssembliesAsync(string baseDirectory,CancellationToken ct)
    {
        var hashes=new Dictionary<string,string>(FirstPartyAssemblies.Length,StringComparer.Ordinal);
        foreach(string name in FirstPartyAssemblies)
        {
            // Messages name the file only, never the folder path.
            var file=new FileInfo(Path.Combine(baseDirectory,name));
            if(!file.Exists || (file.Attributes & FileAttributes.ReparsePoint)!=0) throw new InvalidDataException($"The first-party assembly {name} is missing or redirected.");
            await using var stream=new FileStream(file.FullName,FileMode.Open,FileAccess.Read,FileShare.Read,4096,FileOptions.Asynchronous|FileOptions.SequentialScan);
            hashes.Add(name,Convert.ToHexString(await SHA256.HashDataAsync(stream,ct)));
        }
        return hashes;
    }

    // SHA-256 of one UTF-8 "name:SHA256\n" line per first-party assembly, in the fixed order above. The manager script
    // recomputes the same value from the staged files, so one hash names the whole first-party set.
    internal static string CombinedIdentity(IReadOnlyDictionary<string,string> hashes)
    {
        if(hashes.Count!=FirstPartyAssemblies.Length) throw new InvalidDataException("The first-party hash set is incomplete.");
        var lines=new StringBuilder();
        foreach(string name in FirstPartyAssemblies)
        {
            if(!hashes.TryGetValue(name,out string? hash)) throw new InvalidDataException($"The first-party hash set has no entry for {name}.");
            lines.Append(name).Append(':').Append(hash).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lines.ToString())));
    }
    // In-process synthetic HTTP only: the packaged check has no network or account access.
    private sealed class PublicGameFixtureHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();Requests++;
            if(request.Method!=HttpMethod.Get || request.Headers.Authorization is not null || request.Headers.Contains("Cookie"))
                throw new InvalidOperationException("The synthetic public request carried unexpected authentication.");
            string json=request.RequestUri?.AbsoluteUri switch
            {
                "https://apis.roblox.com/universes/v1/places/6564161046/universe"=>"""{"universeId":200}""",
                "https://games.roblox.com/v1/games?universeIds=200"=>"""{"data":[{"id":200,"rootPlaceId":100,"name":"Synthetic experience","creator":{"id":300,"name":"Synthetic creator","type":"Group"},"playing":3000000000,"maxPlayers":20}]}""",
                _=>throw new InvalidOperationException("The synthetic check requested an unexpected endpoint.")
            };
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) {Content=new StringContent(json,System.Text.Encoding.UTF8,"application/json")});
        }
    }
    private static void CheckBrandAsset()
    {
        var assembly=Assembly.GetExecutingAssembly();
        using var resources=assembly.GetManifestResourceStream(assembly.GetName().Name+".g.resources")??throw new InvalidDataException("Application resources are missing.");
        using var reader=new ResourceReader(resources); var entries=reader.GetEnumerator();
        while(entries.MoveNext())
        {
            if(entries.Key is not string key || key!="assets/zenith-mark.png") continue;
            using var image=(Stream)entries.Value!;
            var decoder=BitmapDecoder.Create(image,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
            if(decoder.Frames.Count!=1 || decoder.Frames[0].PixelWidth is <16 or >4096 || decoder.Frames[0].PixelHeight is <16 or >4096)
                throw new InvalidDataException("The brand image is invalid.");
            return;
        }
        throw new InvalidDataException("The brand image is missing.");
    }
}
