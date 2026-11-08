using System.IO;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Owned metadata fixtures only. Recovery must not invent an empty queue or replay clients.
public sealed class LaunchQueueRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-queue-recovery-" + Guid.NewGuid().ToString("N"));
    private string Primary => Path.Combine(_root, "launch-queue.json");
    private static LaunchOperation Row()
    {
        var at = DateTimeOffset.UtcNow;
        return new(new(Guid.NewGuid(), Guid.NewGuid(), 1, 123, 6564161046, 0, at.AddMinutes(-2), 7),
            LaunchState.ClientStartedUnverified, LaunchProblem.ClientStartedUnverified, at,
            new(900001, at.AddMinutes(-1).UtcTicks, at), new(4 * CapacityPolicy.GiB, 5 * CapacityPolicy.GiB));
    }
    private async Task<byte[]> Write(string suffix, LaunchQueueSnapshot snapshot, int version = 7)
    {
        Directory.CreateDirectory(_root); var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Version = version, Queue = snapshot });
        await File.WriteAllBytesAsync(Primary + suffix, bytes); return bytes;
    }

    [Fact]
    public async Task MissingPrimaryRecoversTheVerifiedBackupPausedWithoutPromotingOrChangingFiles()
    {
        var operation = Row(); var original = await Write(".bak", new(false, [operation]));
        using var store = new LaunchQueueStore(_root); var loaded = await store.LoadAsync(default);
        Assert.True(loaded.Paused); Assert.Equal(operation, Assert.Single(loaded.Operations));
        Assert.False(File.Exists(Primary)); Assert.Equal(original, await File.ReadAllBytesAsync(Primary + ".bak"));
        Assert.False(File.Exists(Primary + ".tmp")); Assert.False(File.Exists(Primary + ".damaged"));
    }

    [Fact]
    public async Task CoordinatorRestartsRecoveredOperationsForReviewAndNeverCallsTheProvider()
    {
        var operation = Row(); var original = await Write(".bak", new(false, [operation]));
        using var store = new LaunchQueueStore(_root); var provider = new ForbiddenProvider();
        await using var queue = new LaunchCoordinator(store, provider, (_, _) => throw new InvalidOperationException("No authorization expected."), _ => new(true, "Fixture"));
        await queue.InitializeAsync(default); var recovered = await store.LoadAsync(default);
        Assert.True(recovered.Paused); var row = Assert.Single(recovered.Operations);
        // CAP-14-03: a receipted, budgeted started client stays itself (still reserving and
        // reviewable) so a fresh exact PID/creation observation can reconcile it after restart.
        Assert.Equal(LaunchState.ClientStartedUnverified, row.State); Assert.Equal(LaunchProblem.ClientStartedUnverified, row.Problem);
        Assert.True(LaunchDescriptions.HasUnresolvedReservation(row)); Assert.Equal(operation.UpdatedUtc, row.UpdatedUtc);
        Assert.Equal(operation.Intent, row.Intent); Assert.Equal(operation.ProcessReceipt, row.ProcessReceipt); Assert.Equal(operation.ResourceBudget, row.ResourceBudget);
        Assert.Equal(original, await File.ReadAllBytesAsync(Primary + ".bak")); Assert.Equal(0, provider.Calls);
        await queue.SetPausedAsync(false,default); await queue.SetPausedAsync(true,default); Assert.Equal(0, provider.Calls);
    }

    // The backup is one generation behind: the lost primary may already have moved a queued or
    // capacity-blocked row into a native start, so the backup's row is a possible client.
    [Theory]
    [InlineData(1, LaunchState.Queued, LaunchProblem.None)] [InlineData(2, LaunchState.Queued, LaunchProblem.None)]
    [InlineData(3, LaunchState.Queued, LaunchProblem.None)] [InlineData(4, LaunchState.Queued, LaunchProblem.None)]
    [InlineData(5, LaunchState.Queued, LaunchProblem.None)] [InlineData(6, LaunchState.Queued, LaunchProblem.None)]
    [InlineData(7, LaunchState.Queued, LaunchProblem.None)]
    [InlineData(4, LaunchState.Blocked, LaunchProblem.InsufficientCapacity)] [InlineData(7, LaunchState.Blocked, LaunchProblem.InsufficientCapacity)]
    [InlineData(7, LaunchState.Blocked, LaunchProblem.ProviderUnavailable)]
    public async Task EverySupportedOrphanBackupIsPreservedAndLoadedForReview(int version, LaunchState state, LaunchProblem problem)
    {
        var operation = Row() with { State = state, Problem = problem, ProcessReceipt = null, ResourceBudget = null };
        var bytes = await Write(".bak", new(false, [operation]), version);
        using var store = new LaunchQueueStore(_root); var loaded = await store.LoadAsync(default);
        Assert.True(loaded.Paused); Assert.Single(loaded.Operations);
        Assert.Equal(operation.Intent, loaded.Operations[0].Intent); Assert.Null(loaded.Operations[0].ResourceBudget);
        Assert.Equal(LaunchState.ReviewRequired, loaded.Operations[0].State); Assert.Equal(LaunchProblem.Interrupted, loaded.Operations[0].Problem);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Primary + ".bak")); Assert.False(File.Exists(Primary));
    }

    [Fact]
    public async Task ABackupQueuedRequestStaysAReservingPossibleClientAfterCoordinatorRestart()
    {
        var operation = Row() with { State = LaunchState.Queued, Problem = LaunchProblem.None, ProcessReceipt = null };
        await Write(".bak", new(false, [operation]));
        using var store = new LaunchQueueStore(_root); var provider = new ForbiddenProvider();
        await using var queue = new LaunchCoordinator(store, provider, (_, _) => throw new InvalidOperationException("No authorization expected."), _ => new(true, "Fixture"));
        await queue.InitializeAsync(default); var row = Assert.Single((await store.LoadAsync(default)).Operations);
        Assert.Equal(LaunchState.ReviewRequired, row.State); Assert.Equal(LaunchProblem.Interrupted, row.Problem);
        Assert.True(LaunchDescriptions.HasUnresolvedReservation(row)); Assert.Equal(operation.ResourceBudget, row.ResourceBudget);
        await Assert.ThrowsAsync<LaunchQueueException>(() => queue.CancelAsync(operation.Intent.OperationId, default));
        Assert.Equal(0, provider.Calls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("{\"Version\":7,\"Queue\":null}")]
    public async Task MissingPrimaryWithDamagedBackupFailsWithoutPresentingAnEmptyQueue(string content)
    {
        Directory.CreateDirectory(_root); await File.WriteAllTextAsync(Primary + ".bak", content);
        using var store = new LaunchQueueStore(_root);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new(true,[Row()]),default));
        Assert.Equal(content, await File.ReadAllTextAsync(Primary + ".bak")); Assert.False(File.Exists(Primary));
    }

    [Theory]
    [InlineData(9)] [InlineData(999)]
    public async Task MissingPrimaryDoesNotDowngradeOrIgnoreAnUnsupportedFutureBackup(int version)
    {
        var bytes = await Write(".bak", new(false, [Row()]), version);
        using var store = new LaunchQueueStore(_root);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(default));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Primary + ".bak")); Assert.False(File.Exists(Primary));
    }

    [Theory]
    [InlineData(".tmp")]
    [InlineData(".damaged")]
    public async Task OnlyUncommittedOrDamagedEvidenceRequiresReviewInsteadOfCreatingAnEmptyQueue(string suffix)
    {
        var bytes = await Write(suffix, new(false, [Row()])); using var store = new LaunchQueueStore(_root);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(default));
        if(suffix==".damaged")await Assert.ThrowsAsync<InvalidDataException>(()=>store.SaveAsync(new(true,[Row()]),default));
        else await Assert.ThrowsAsync<IOException>(()=>store.SaveAsync(new(true,[Row()]),default));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Primary + suffix)); Assert.False(File.Exists(Primary));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AValidPrimaryStaysAuthoritativeOverOlderBackupAndStrayTemporaryData(bool paused)
    {
        var old = Row(); var current = Row(); await Write(".bak", new(false, [old])); await Write(".tmp", new(false, [old]));
        var bytes = await Write("", new(paused, [current])); using var store = new LaunchQueueStore(_root);
        var loaded = await store.LoadAsync(default); Assert.Equal(paused, loaded.Paused); Assert.Equal(current, Assert.Single(loaded.Operations));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Primary)); Assert.True(File.Exists(Primary + ".tmp"));
    }

    [Fact]
    public async Task TrulyFreshDirectoryStillReturnsAnEmptyPausedQueueWithoutWriting()
    {
        using var store = new LaunchQueueStore(_root); Assert.Equal(LaunchQueueSnapshot.Empty, await store.LoadAsync(default));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task CancellationCannotRecoverOrWriteAQueue()
    {
        var bytes = await Write(".bak", new(false, [Row()])); using var store = new LaunchQueueStore(_root);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(stop.Token));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Primary + ".bak")); Assert.False(File.Exists(Primary));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ADirectoryAtAQueueFilePathIsAnAccessFailureNotAFirstLaunch(bool backup)
    {
        Directory.CreateDirectory(Primary + (backup ? ".bak" : "")); using var store = new LaunchQueueStore(_root);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.LoadAsync(default));
        Assert.True(Directory.Exists(Primary + (backup ? ".bak" : "")));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SaveNeverTruncatesOrDeletesAPreexistingInterruptedWrite(bool futurePrimary)
    {
        var main = await Write("", new(true, [Row()]), futurePrimary ? 99 : 7);
        var interrupted = await Write(".tmp", new(false, [Row()]));
        using var store = new LaunchQueueStore(_root);
        if(futurePrimary) await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new(true, [Row()]), default));
        else await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(new(true, [Row()]), default));
        Assert.Equal(main, await File.ReadAllBytesAsync(Primary)); Assert.Equal(interrupted, await File.ReadAllBytesAsync(Primary + ".tmp"));
        Assert.False(File.Exists(Primary + ".damaged"));
    }

    [Fact]
    public async Task ASecondDifferentDamagedOriginalCannotReplaceTheFirstPreservedEvidence()
    {
        await Write(".bak", new(true, [Row()])); Directory.CreateDirectory(_root);
        byte[] first = "first damaged queue"u8.ToArray(), second = "second damaged queue"u8.ToArray();
        await File.WriteAllBytesAsync(Primary + ".damaged", first); await File.WriteAllBytesAsync(Primary, second);
        byte[] backup = await File.ReadAllBytesAsync(Primary + ".bak"); using var store = new LaunchQueueStore(_root);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new(true, [Row()]), default));
        Assert.Equal(first, await File.ReadAllBytesAsync(Primary + ".damaged")); Assert.Equal(second, await File.ReadAllBytesAsync(Primary));
        Assert.Equal(backup, await File.ReadAllBytesAsync(Primary + ".bak")); Assert.False(File.Exists(Primary + ".tmp"));
    }

    [Fact]
    public async Task IdenticalPreservedDamageAllowsDeliberateRepairWithoutReplacingTheVerifiedBackup()
    {
        var previous = Row(); var backup = await Write(".bak", new(true, [previous]));
        byte[] damaged = "same interrupted queue"u8.ToArray(); await File.WriteAllBytesAsync(Primary, damaged); await File.WriteAllBytesAsync(Primary + ".damaged", damaged);
        using var store = new LaunchQueueStore(_root); var repaired = Row(); await store.SaveAsync(new(true, [repaired]), default);
        Assert.Equal(repaired, Assert.Single((await store.LoadAsync(default)).Operations));
        Assert.Equal(damaged, await File.ReadAllBytesAsync(Primary + ".damaged")); Assert.Equal(backup, await File.ReadAllBytesAsync(Primary + ".bak"));
    }

    [Fact]
    public async Task OversizedDamagedPrimaryRemainsInPlaceRatherThanBeingCopiedBeyondTheDecodedBound()
    {
        await Write(".bak", new(true, [Row()])); byte[] oversized = new byte[1048577]; Array.Fill(oversized, (byte)'x');
        await File.WriteAllBytesAsync(Primary, oversized); using var store = new LaunchQueueStore(_root);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new(true, [Row()]), default));
        Assert.Equal(oversized, await File.ReadAllBytesAsync(Primary)); Assert.False(File.Exists(Primary + ".damaged")); Assert.False(File.Exists(Primary + ".tmp"));
    }

    [Fact]
    public async Task CancelledSaveLeavesBothExistingPrimaryAndInterruptedEvidenceUntouched()
    {
        var primary = await Write("", new(true, [Row()])); var interrupted = await Write(".tmp", new(false, [Row()]));
        using var store = new LaunchQueueStore(_root); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new(true, [Row()]), stop.Token));
        Assert.Equal(primary, await File.ReadAllBytesAsync(Primary)); Assert.Equal(interrupted, await File.ReadAllBytesAsync(Primary + ".tmp"));
    }

    [Theory]
    [InlineData("{\"Version\":99,\"Queue\":", false)]
    [InlineData("{\"Version\":7,\"Version\":7,\"Queue\":", false)]
    [InlineData("{\"Version\":99.0,\"Queue\":", false)]
    [InlineData("{\"Version\":999999999999999999999999999999,\"Queue\":", false)]
    [InlineData("{\"Unfamiliar\":", true)]
    public async Task ObservedFutureOrAmbiguousVersionsCannotBecomeOrdinaryCorruptionFallback(string prefix,bool deep)
    {
        byte[] backup = await Write(".bak", new(true, [Row()]));
        string content = deep ? prefix + new string('[', 70) + "0" + new string(']', 70) + ",\"Version\":99,\"Queue\":{}}" : prefix;
        await File.WriteAllTextAsync(Primary, content); using var store = new LaunchQueueStore(_root);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new(true, [Row()]), default));
        Assert.Equal(content, await File.ReadAllTextAsync(Primary)); Assert.Equal(backup, await File.ReadAllBytesAsync(Primary + ".bak"));
        Assert.False(File.Exists(Primary + ".damaged")); Assert.False(File.Exists(Primary + ".tmp"));
    }

    [Fact]
    public async Task DeepMalformedKnownVersionStillUsesTheVerifiedBackupInsteadOfClaimingFutureAuthority()
    {
        var old = Row(); byte[] backup = await Write(".bak", new(false, [old]));
        string malformed = "{\"Version\":7,\"Unfamiliar\":" + new string('[', 70) + "0" + new string(']', 70) + ",\"Queue\":{}}";
        await File.WriteAllTextAsync(Primary, malformed); using var store = new LaunchQueueStore(_root); var recovered = await store.LoadAsync(default);
        Assert.True(recovered.Paused); Assert.Equal(old, Assert.Single(recovered.Operations));
        Assert.Equal(malformed, await File.ReadAllTextAsync(Primary)); Assert.Equal(backup, await File.ReadAllBytesAsync(Primary + ".bak"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Utf8BomQueueCompatibilityPreservesOriginalBytesAndRecoveryRules(bool backup)
    {
        var operation = Row(); byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { Version = 7, Queue = new LaunchQueueSnapshot(false, [operation]) });
        byte[] bytes = [0xef, 0xbb, 0xbf, ..json]; Directory.CreateDirectory(_root);
        string path = Primary + (backup ? ".bak" : ""); await File.WriteAllBytesAsync(path, bytes);
        using var store = new LaunchQueueStore(_root); var loaded = await store.LoadAsync(default);
        Assert.Equal(backup, loaded.Paused); Assert.Equal(operation, Assert.Single(loaded.Operations));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path)); Assert.Equal(!backup, File.Exists(Primary));
    }

    [Fact]
    public async Task AnOversizedUnreadBackupCannotBeOverwrittenByRotatingAValidPrimary()
    {
        var primary = await Write("", new(true, [Row()])); byte[] oversized = new byte[1048577]; Array.Fill(oversized, (byte)'x');
        await File.WriteAllBytesAsync(Primary + ".bak", oversized); using var store = new LaunchQueueStore(_root);
        Assert.Single((await store.LoadAsync(default)).Operations); // Committed primary still owns reads.
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new(true, [Row()]), default));
        Assert.Equal(primary, await File.ReadAllBytesAsync(Primary)); Assert.Equal(oversized, await File.ReadAllBytesAsync(Primary + ".bak"));
        Assert.False(File.Exists(Primary + ".tmp")); Assert.False(File.Exists(Primary + ".damaged"));
    }

    [Theory]
    [InlineData("not a declared format")]
    [InlineData("{}")]
    [InlineData("{\"Version\":null,\"Queue\":null}")]
    public async Task AnUnknownBackupCannotBeOverwrittenEvenBehindAValidPrimary(string content)
    {
        var primary = await Write("", new(true, [Row()])); await File.WriteAllTextAsync(Primary+".bak",content);
        using var store=new LaunchQueueStore(_root);Assert.Single((await store.LoadAsync(default)).Operations);
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.SaveAsync(new(true,[Row()]),default));
        Assert.Equal(primary,await File.ReadAllBytesAsync(Primary));Assert.Equal(content,await File.ReadAllTextAsync(Primary+".bak"));
        Assert.False(File.Exists(Primary+".tmp"));Assert.False(File.Exists(Primary+".damaged"));
    }

    [Fact]
    public async Task ExplicitlyKnownCurrentMalformedBackupCanBeReplacedByAValidCommittedPrimary()
    {
        var current=Row();var primary=await Write("",new(true,[current]));
        await File.WriteAllTextAsync(Primary+".bak","{\"Version\":7,\"Queue\":null}");
        using var store=new LaunchQueueStore(_root);var updated=Row();await store.SaveAsync(new(true,[updated]),default);
        Assert.Equal(updated,Assert.Single((await store.LoadAsync(default)).Operations));
        Assert.Equal(primary,await File.ReadAllBytesAsync(Primary+".bak"));Assert.False(File.Exists(Primary+".damaged"));
    }

    [Fact]
    public async Task TemporaryCleanupFailureReleasesTheWriterAndPreservesTheInterruptedEvidence()
    {
        var primary=await Write("",new(true,[Row()]));using var store=new LaunchQueueStore(_root);
        var collection=new CleanupFailureRows(Primary+".tmp",Row());
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>store.SaveAsync(new(true,collection),default));
            Assert.True(collection.Triggered);Assert.True(File.GetAttributes(Primary+".tmp").HasFlag(FileAttributes.ReadOnly));
            Assert.Equal(primary,await File.ReadAllBytesAsync(Primary));
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            // The retained temporary blocks this write promptly; a cleanup exception must not
            // strand the semaphore and turn a later explicit retry into cancellation/deadlock.
            await Assert.ThrowsAnyAsync<IOException>(()=>store.SaveAsync(new(true,[Row()]),stop.Token));
            Assert.False(stop.IsCancellationRequested);Assert.Equal(primary,await File.ReadAllBytesAsync(Primary));
        }
        finally{if(File.Exists(Primary+".tmp"))File.SetAttributes(Primary+".tmp",FileAttributes.Normal);}
    }
    [Fact]
    public async Task CallerCancellationDuringARealQueueStoreWriteKeepsTheQueueHealthyAndDurablyCancelsTheIntent()
    {
        // "Stop bundle work", Panic/Lock or a stale trial review can cancel the caller's token
        // while the real store owns the just-added row's write. That is a user stop, not a
        // storage fault: the queue stays usable and the new intent is durably cancelled.
        using var store=new LaunchQueueStore(_root);using var stop=new CancellationTokenSource();
        var cancelling=new CancelOnQueuedWrite(store,stop);var provider=new ForbiddenProvider();
        await using var queue=new LaunchCoordinator(cancelling,provider,(_,_)=>throw new InvalidOperationException("No authorization expected."),_=>new(true,"Fixture"));
        await queue.InitializeAsync(default);
        var profile=AccountProfile.Create("Synthetic") with { Revision=1,RobloxUserId=123,PreferredPlaceId=6564161046 };

        var row=await queue.EnqueueAsync(profile,stop.Token,new(123,1));

        Assert.True(cancelling.Cancelled);Assert.Equal(LaunchState.Cancelled,row.State);Assert.True(queue.IsHealthy);
        using var reopened=new LaunchQueueStore(_root);var saved=Assert.Single((await reopened.LoadAsync(default)).Operations);
        Assert.Equal(row.Intent,saved.Intent);Assert.Equal(LaunchState.Cancelled,saved.State);Assert.Equal(0,provider.Calls);
    }
    [Fact]
    public async Task InitializeCanBeRetriedAfterAFailedStartupPersistWithoutDuplicatingRows()
    {
        // A crash between CreateNew and File.Replace leaves a .tmp behind a valid primary. The
        // startup write then fails closed; once the user sets that file aside, a retry must
        // rebuild from disk instead of appending the same rows again or keeping the old fault.
        var operation=Row() with { State=LaunchState.Queued,Problem=LaunchProblem.None,ProcessReceipt=null };
        await Write("",new(true,[operation]));await Write(".tmp",new(false,[Row()]));
        using var store=new LaunchQueueStore(_root);var provider=new ForbiddenProvider();
        await using var queue=new LaunchCoordinator(store,provider,(_,_)=>throw new InvalidOperationException("No authorization expected."),_=>new(true,"Fixture"));
        await Assert.ThrowsAsync<IOException>(()=>queue.InitializeAsync(default));
        Assert.False(queue.IsHealthy);
        File.Move(Primary+".tmp",Path.Combine(_root,"kept-by-fixture.json"));

        await queue.InitializeAsync(default);

        Assert.True(queue.IsHealthy);
        var saved=Assert.Single((await store.LoadAsync(default)).Operations);
        Assert.Equal(operation.Intent,saved.Intent);Assert.Equal(0,provider.Calls);
    }
    [Fact]
    public async Task AnInterruptedWriteBehindAValidPrimaryIsNamedAndCanBeSetAsideWithoutLosingItsBytes()
    {
        var current=Row();var primary=await Write("",new(true,[current]));var interrupted=await Write(".tmp",new(false,[Row()]));
        using var store=new LaunchQueueStore(_root);
        await Assert.ThrowsAsync<IOException>(()=>store.SaveAsync(new(true,[current]),default));
        Assert.Equal(new LaunchQueueBlocker(LaunchQueueBlockerKind.InterruptedWrite,"launch-queue.json.tmp"),store.RecoveryBlocker);

        string? kept=await store.SetAsideInterruptedWriteAsync(default);

        Assert.NotNull(kept);Assert.StartsWith("launch-queue.json.interrupted-",kept);Assert.Null(store.RecoveryBlocker);
        Assert.False(File.Exists(Primary+".tmp"));Assert.Equal(interrupted,await File.ReadAllBytesAsync(Path.Combine(_root,kept)));
        Assert.Equal(primary,await File.ReadAllBytesAsync(Primary));
        Assert.Null(await store.SetAsideInterruptedWriteAsync(default)); // Nothing left to set aside; nothing else changes.
        var updated=Row();await store.SaveAsync(new(true,[updated]),default);Assert.Null(store.RecoveryBlocker);
        Assert.Equal(updated,Assert.Single((await store.LoadAsync(default)).Operations));
        Assert.Equal(interrupted,await File.ReadAllBytesAsync(Path.Combine(_root,kept)));
    }

    [Theory]
    [InlineData("missing")] [InlineData("future")] [InlineData("damaged")] [InlineData("unknownBackup")]
    public async Task SetAsideRefusesUnlessTheCommittedPrimaryIsReadableAndNothingElseBlocks(string state)
    {
        // Without a readable supported primary the interrupted save may be the only evidence of
        // newer work; moving it would turn a deliberate fail-closed state into a quieter queue.
        if(state=="future")await Write("",new(true,[Row()]),99);
        if(state=="damaged"){Directory.CreateDirectory(_root);await File.WriteAllTextAsync(Primary,"damaged");await Write(".bak",new(true,[Row()]));}
        if(state=="unknownBackup"){await Write("",new(true,[Row()]));await File.WriteAllTextAsync(Primary+".bak","not a declared format");}
        var interrupted=await Write(".tmp",new(false,[Row()]));
        var before=Directory.GetFiles(_root).ToDictionary(path=>path,File.ReadAllBytes);
        using var store=new LaunchQueueStore(_root);
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.SetAsideInterruptedWriteAsync(default));
        Assert.Equal(state switch { "future"=>LaunchQueueBlockerKind.UnsupportedFormat,"unknownBackup"=>LaunchQueueBlockerKind.UnreadableBackup,_=>LaunchQueueBlockerKind.OrphanEvidence },
            store.RecoveryBlocker?.Kind);
        Assert.Equal(before.Keys.Order(),Directory.GetFiles(_root).Order());
        foreach(var (path,bytes) in before)Assert.Equal(bytes,await File.ReadAllBytesAsync(path));
        Assert.Equal(interrupted,await File.ReadAllBytesAsync(Primary+".tmp"));
    }

    [Theory]
    [InlineData("orphanTmp",LaunchQueueBlockerKind.OrphanEvidence,"launch-queue.json.tmp")]
    [InlineData("orphanDamaged",LaunchQueueBlockerKind.OrphanEvidence,"launch-queue.json.damaged")]
    [InlineData("damagedBackup",LaunchQueueBlockerKind.UnreadableBackup,"launch-queue.json.bak")]
    [InlineData("damagedPrimary",LaunchQueueBlockerKind.UnreadableQueue,"launch-queue.json")]
    [InlineData("futurePrimary",LaunchQueueBlockerKind.UnsupportedFormat,"launch-queue.json")]
    public async Task EveryFailClosedLoadNamesItsKeptFile(string state,LaunchQueueBlockerKind kind,string file)
    {
        Directory.CreateDirectory(_root);
        if(state=="orphanTmp")await Write(".tmp",new(false,[Row()]));
        if(state=="orphanDamaged")await File.WriteAllTextAsync(Primary+".damaged","damaged");
        if(state=="damagedBackup")await File.WriteAllTextAsync(Primary+".bak","not json");
        if(state=="damagedPrimary")await File.WriteAllTextAsync(Primary,"not json");
        if(state=="futurePrimary")await Write("",new(true,[Row()]),99);
        using var store=new LaunchQueueStore(_root);
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.LoadAsync(default));
        Assert.Equal(new LaunchQueueBlocker(kind,file),store.RecoveryBlocker);
    }

    [Theory]
    [InlineData("conflictingDamaged",LaunchQueueBlockerKind.ConflictingDamagedCopy,"launch-queue.json.damaged")]
    [InlineData("unknownBackup",LaunchQueueBlockerKind.UnreadableBackup,"launch-queue.json.bak")]
    [InlineData("oversizedBackup",LaunchQueueBlockerKind.UnreadableBackup,"launch-queue.json.bak")]
    [InlineData("oversizedPrimary",LaunchQueueBlockerKind.UnreadableQueue,"launch-queue.json")]
    [InlineData("orphanDamaged",LaunchQueueBlockerKind.OrphanEvidence,"launch-queue.json.damaged")]
    [InlineData("futureBackup",LaunchQueueBlockerKind.UnsupportedFormat,"launch-queue.json.bak")]
    public async Task EveryFailClosedSaveNamesItsKeptFile(string state,LaunchQueueBlockerKind kind,string file)
    {
        Directory.CreateDirectory(_root);byte[] oversized=new byte[1048577];Array.Fill(oversized,(byte)'x');
        switch(state)
        {
            case "conflictingDamaged":await Write(".bak",new(true,[Row()]));await File.WriteAllTextAsync(Primary+".damaged","first");await File.WriteAllTextAsync(Primary,"second");break;
            case "unknownBackup":await Write("",new(true,[Row()]));await File.WriteAllTextAsync(Primary+".bak","{}");break;
            case "oversizedBackup":await Write("",new(true,[Row()]));await File.WriteAllBytesAsync(Primary+".bak",oversized);break;
            case "oversizedPrimary":await Write(".bak",new(true,[Row()]));await File.WriteAllBytesAsync(Primary,oversized);break;
            case "orphanDamaged":await File.WriteAllTextAsync(Primary+".damaged","damaged");break;
            case "futureBackup":await Write("",new(true,[Row()]));await Write(".bak",new(true,[Row()]),99);break;
        }
        using var store=new LaunchQueueStore(_root);
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.SaveAsync(new(true,[Row()]),default));
        Assert.Equal(new LaunchQueueBlocker(kind,file),store.RecoveryBlocker);
    }
    private sealed class CancelOnQueuedWrite(ILaunchQueueStore inner,CancellationTokenSource stop):ILaunchQueueStore
    {
        public bool Cancelled;
        public Task<LaunchQueueSnapshot> LoadAsync(CancellationToken ct)=>inner.LoadAsync(ct);
        public Task SaveAsync(LaunchQueueSnapshot snapshot,CancellationToken ct)
        {
            if(!Cancelled&&snapshot.Operations.Any(o=>o.State==LaunchState.Queued)){Cancelled=true;stop.Cancel();}
            return inner.SaveAsync(snapshot,ct);
        }
    }
    private sealed class CleanupFailureRows(string temporary,LaunchOperation operation):IReadOnlyList<LaunchOperation>
    {
        public bool Triggered;
        public int Count=>1;
        public LaunchOperation this[int index]=>index==0?operation:throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<LaunchOperation> GetEnumerator()
        {
            if(File.Exists(temporary))
            {
                File.SetAttributes(temporary,FileAttributes.ReadOnly);Triggered=true;
                throw new IOException("Controlled serialization failure after temporary creation.");
            }
            return ((IEnumerable<LaunchOperation>)new[]{operation}).GetEnumerator();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
    }

    private sealed class ForbiddenProvider : ILaunchProvider
    {
        public int Calls;
        public ProviderCapability AccountLaunch => new("Synthetic", CapabilityState.Supported, "No native work");
        public Task<ProviderLaunchResult> LaunchAsync(LaunchIntent intent, CancellationToken ct)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("Recovery must never start a client."); }
        public Task<bool> ConfirmArrivalAsync(LaunchIntent intent,ProviderLaunchResult observed,CancellationToken ct)
            => throw new InvalidOperationException("Recovery must never confirm arrival.");
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-queue-recovery-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
