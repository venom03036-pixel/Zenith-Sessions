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
        Assert.Equal(LaunchState.ReviewRequired, row.State); Assert.Equal(LaunchProblem.Interrupted, row.Problem);
        Assert.Equal(operation.Intent, row.Intent); Assert.Equal(operation.ProcessReceipt, row.ProcessReceipt); Assert.Equal(operation.ResourceBudget, row.ResourceBudget);
        Assert.Equal(original, await File.ReadAllBytesAsync(Primary + ".bak")); Assert.Equal(0, provider.Calls);
        await queue.SetPausedAsync(false,default); await queue.SetPausedAsync(true,default); Assert.Equal(0, provider.Calls);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public async Task EverySupportedOrphanBackupIsPreservedAndLoadedForReview(int version)
    {
        var operation = Row() with { State = LaunchState.Queued, Problem = LaunchProblem.None, ProcessReceipt = null, ResourceBudget = null };
        var bytes = await Write(".bak", new(false, [operation]), version);
        using var store = new LaunchQueueStore(_root); var loaded = await store.LoadAsync(default);
        Assert.True(loaded.Paused); Assert.Single(loaded.Operations);
        Assert.Equal(operation.Intent, loaded.Operations[0].Intent); Assert.Null(loaded.Operations[0].ResourceBudget);
        Assert.Equal(version < 4 ? LaunchState.ReviewRequired : LaunchState.Queued, loaded.Operations[0].State);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Primary + ".bak")); Assert.False(File.Exists(Primary));
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
    [InlineData(8)] [InlineData(999)]
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
