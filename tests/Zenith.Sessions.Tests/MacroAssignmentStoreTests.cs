using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Actual SQLite, but only newly owned synthetic roots. No accounts/vaults/macros/clients are opened.
public sealed class MacroAssignmentStoreTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static MacroAssignment Assignment(MacroAssignmentTargetKind kind = MacroAssignmentTargetKind.Account, Guid? id = null, string name = "Saved target")
        => new(new(kind, id ?? Guid.NewGuid()), name, Guid.NewGuid(), "Saved macro");
    private static Task<MacroAssignmentPage> Read(MacroAssignmentStore store, MacroAssignmentQuery? query = null)
        => store.ReadAsync(query ?? new(), default).WaitAsync(Deadline);

    [Fact]
    public async Task MissingReadAndCheckedAbsentRemovalDoNotCreateStorage()
    {
        await using var fixture = new Fixture(); var store = fixture.Store("missing");
        var page = await Read(store); Assert.Equal(MacroAssignmentStamp.Empty, page.Stamp); Assert.Empty(page.Items);
        var removed = await store.RemoveAsync(Assignment().Target, page.Stamp, default).WaitAsync(Deadline);
        Assert.Equal(MacroAssignmentWriteState.Unchanged, removed.State); Assert.Equal(page.Stamp, removed.Stamp);
        Assert.False(Directory.Exists(System.IO.Path.GetDirectoryName(store.Path)));
        await Assert.ThrowsAsync<MacroAssignmentConflictException>(() => store.RemoveAsync(Assignment().Target, new(Guid.NewGuid(), 1), default));
        Assert.False(File.Exists(store.Path));
    }

    [Fact]
    public async Task SaveReopenReplaceAndRemoveAreDurableAndAdvanceOnlyRealChanges()
    {
        await using var fixture = new Fixture(); var store = fixture.Store(); var assignment = Assignment();
        var first = await store.SaveAsync(assignment, MacroAssignmentStamp.Empty, default).WaitAsync(Deadline);
        Assert.Equal(MacroAssignmentWriteState.Applied, first.State); Assert.NotEqual(Guid.Empty, first.Stamp.Generation); Assert.Equal(1, first.Stamp.Revision);
        var reopened = fixture.Store(); var page = await Read(reopened); Assert.Equal(first.Stamp, page.Stamp); Assert.Equal(assignment, Assert.Single(page.Items));
        var same = await reopened.SaveAsync(assignment, page.Stamp, default); Assert.Equal(MacroAssignmentWriteState.Unchanged, same.State); Assert.Equal(page.Stamp, same.Stamp);
        var changed = assignment with { MacroId = Guid.NewGuid(), MacroName = "Replacement macro" };
        var second = await store.SaveAsync(changed, same.Stamp, default); Assert.Equal(2, second.Stamp.Revision); Assert.Equal(first.Stamp.Generation, second.Stamp.Generation);
        var removed = await reopened.RemoveAsync(assignment.Target, second.Stamp, default); Assert.Equal(3, removed.Stamp.Revision); Assert.Equal(MacroAssignmentWriteState.Applied, removed.State);
        var noOp = await store.RemoveAsync(assignment.Target, removed.Stamp, default); Assert.Equal(MacroAssignmentWriteState.Unchanged, noOp.State); Assert.Equal(removed.Stamp, noOp.Stamp);
        var empty = await Read(fixture.Store()); Assert.Empty(empty.Items); Assert.Equal(removed.Stamp, empty.Stamp);
    }

    [Fact]
    public async Task TargetNamespacesAndRecreatedSameNamesNeverAdoptOldAssignments()
    {
        await using var fixture = new Fixture(); var store = fixture.Store(); var id = Guid.NewGuid(); var stamp = MacroAssignmentStamp.Empty;
        var assignments = new[] { Assignment(MacroAssignmentTargetKind.Account, id, "Same name"), Assignment(MacroAssignmentTargetKind.Group, id, "Same name"),
            Assignment(MacroAssignmentTargetKind.Bundle, id, "Same name"), Assignment(MacroAssignmentTargetKind.Group, name: "Same name") };
        foreach (var assignment in assignments) stamp = (await store.SaveAsync(assignment, stamp, default)).Stamp;
        var page = await Read(store); Assert.Equal(4, page.Total); Assert.Equal(4, page.Items.Select(item => item.Target).Distinct().Count());
        Assert.All(assignments, assignment => Assert.Contains(assignment, page.Items));
        Assert.Equal(2, page.Items.Count(item => item.Target.Kind == MacroAssignmentTargetKind.Group));
    }

    [Fact]
    public async Task TwoStoreInstancesWithTheSameReviewedStampHaveOneWinnerAndOneConflict()
    {
        await using var fixture = new Fixture(); var first = fixture.Store(); var second = fixture.Store(); var a = Assignment(); var b = Assignment();
        var outcomes = await Task.WhenAll(Attempt(first, a), Attempt(second, b)).WaitAsync(Deadline);
        Assert.Single(outcomes.Where(result => result.Applied)); Assert.Single(outcomes.Where(result => result.Error is MacroAssignmentConflictException));
        var page = await Read(fixture.Store()); Assert.Equal(1, page.Total); Assert.Equal(1, page.Stamp.Revision);
        Assert.Equal(outcomes.Single(result => result.Applied).Assignment, Assert.Single(page.Items));
        static async Task<(bool Applied, MacroAssignment Assignment, Exception? Error)> Attempt(MacroAssignmentStore store, MacroAssignment assignment)
        {
            try { var result = await store.SaveAsync(assignment, MacroAssignmentStamp.Empty, default); return (result.State == MacroAssignmentWriteState.Applied, assignment, null); }
            catch (MacroAssignmentConflictException error) { return (false, assignment, error); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreatorAppearingBetweenUnownedPrimaryAndJournalObservationsNeverLooksLikeOrphanedStorage(bool reading)
    {
        await using var fixture = new Fixture();
        var missingObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sidecarCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueWinner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int missingOnce = 0, journalObserved = 0;
        var observer = fixture.Store(checkpoint: phase =>
        {
            if (phase == MacroAssignmentStore.NewPrimaryCheckpoint.MissingPrimaryBeforeUnownedSidecars && Interlocked.Increment(ref missingOnce) == 1)
            { missingObserved.TrySetResult(); continueScan.Task.GetAwaiter().GetResult(); }
            if (phase == MacroAssignmentStore.NewPrimaryCheckpoint.UnownedJournalAttributesObserved)
            { Interlocked.Increment(ref journalObserved); continueWinner.TrySetResult(); }
        });
        string path = observer.Path;
        var winner = fixture.Store(checkpoint: phase =>
        {
            if (phase != MacroAssignmentStore.NewPrimaryCheckpoint.CreatedBeforeOpen) return;
            // A zero-byte rollback-journal creation is an ordinary transient file shape. The real
            // SQLite writer subsequently initializes/commits its database while retaining its lease.
            File.WriteAllBytes(path + "-journal", Array.Empty<byte>());
            sidecarCreated.TrySetResult(); continueWinner.Task.GetAwaiter().GetResult();
        });
        try
        {
            Task<MacroAssignmentPage>? read = reading ? observer.ReadAsync(new(), default) : null;
            Task<MacroAssignmentWriteResult>? losingWrite = reading ? null : observer.SaveAsync(Assignment(), MacroAssignmentStamp.Empty, default);
            await missingObserved.Task.WaitAsync(Deadline);
            var actualWinner = Assignment(); var winningWrite = winner.SaveAsync(actualWinner, MacroAssignmentStamp.Empty, default);
            await sidecarCreated.Task.WaitAsync(Deadline); Assert.True(File.Exists(path)); Assert.True(File.Exists(path + "-journal"));
            continueScan.TrySetResult();
            var committed = await winningWrite.WaitAsync(Deadline); Assert.Equal(MacroAssignmentWriteState.Applied, committed.State);
            Assert.True(Volatile.Read(ref journalObserved) > 0);
            if (reading)
            {
                var page = await read!.WaitAsync(Deadline); Assert.Equal(committed.Stamp, page.Stamp); Assert.Equal(actualWinner, Assert.Single(page.Items));
            }
            else await Assert.ThrowsAsync<MacroAssignmentConflictException>(() => losingWrite!.WaitAsync(Deadline));
            var reopened = await Read(fixture.Store()); Assert.Equal(committed.Stamp, reopened.Stamp); Assert.Equal(actualWinner, Assert.Single(reopened.Items));
        }
        finally { continueScan.TrySetResult(); continueWinner.TrySetResult(); }
    }

    [Fact]
    public async Task JournalDisappearingAfterUnownedAttributesBeforeLengthDoesNotBecomeDamage()
    {
        await using var fixture = new Fixture(); var seed = fixture.Store(); var assignment = Assignment();
        var stamp = (await seed.SaveAsync(assignment, MacroAssignmentStamp.Empty, default)).Stamp;
        string journal = seed.Path + "-journal"; File.WriteAllBytes(journal, Array.Empty<byte>()); int removed = 0;
        var reader = fixture.Store(checkpoint: phase =>
        {
            if (phase == MacroAssignmentStore.NewPrimaryCheckpoint.UnownedJournalAttributesObserved && Interlocked.Increment(ref removed) == 1)
                File.Delete(journal);
        });
        var page = await Read(reader); Assert.Equal(1, removed); Assert.Equal(stamp, page.Stamp); Assert.Equal(assignment, Assert.Single(page.Items));
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public async Task StaleReplaceRemoveAndPaginationPreserveTheWinningMetadata()
    {
        await using var fixture = new Fixture(); var store = fixture.Store(); var first = Assignment(); var stamp = (await store.SaveAsync(first, MacroAssignmentStamp.Empty, default)).Stamp;
        var winning = Assignment(); var next = (await store.SaveAsync(winning, stamp, default)).Stamp; byte[] before = File.ReadAllBytes(store.Path);
        await Assert.ThrowsAsync<MacroAssignmentConflictException>(() => store.SaveAsync(first with { MacroName = "Stale" }, stamp, default));
        await Assert.ThrowsAsync<MacroAssignmentConflictException>(() => store.RemoveAsync(winning.Target, stamp, default));
        await Assert.ThrowsAsync<MacroAssignmentConflictException>(() => store.ReadAsync(new(Expected: stamp), default));
        Assert.Equal(before, File.ReadAllBytes(store.Path)); var page = await Read(store); Assert.Equal(next, page.Stamp); Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task DatabaseReplacementWithEqualRevisionStillConflictsOnGeneration()
    {
        await using var fixture = new Fixture(); var original = fixture.Store("a"); var replacement = fixture.Store("b");
        var old = Assignment(); var oldStamp = (await original.SaveAsync(old, MacroAssignmentStamp.Empty, default)).Stamp;
        var current = Assignment(); var newStamp = (await replacement.SaveAsync(current, MacroAssignmentStamp.Empty, default)).Stamp;
        Assert.Equal(oldStamp.Revision, newStamp.Revision); Assert.NotEqual(oldStamp.Generation, newStamp.Generation);
        File.WriteAllBytes(original.Path, File.ReadAllBytes(replacement.Path)); byte[] before = File.ReadAllBytes(original.Path);
        await Assert.ThrowsAsync<MacroAssignmentConflictException>(() => original.SaveAsync(old, oldStamp, default));
        Assert.Equal(before, File.ReadAllBytes(original.Path)); Assert.Equal(current, Assert.Single((await Read(original)).Items));
    }

    [Fact]
    public async Task PagedReadsAreBoundedAndRequireTheSameExactStoreStamp()
    {
        await using var fixture = new Fixture(); var store = fixture.Store(); var seed = Assignment(); var stamp = (await store.SaveAsync(seed, MacroAssignmentStamp.Empty, default)).Stamp;
        fixture.Sql(store.Path, db =>
        {
            using var tx = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = tx;
            command.CommandText = "INSERT INTO assignments(kind,target_id,target_name,macro_id,macro_name) VALUES(2,$id,'Saved group',$macro,'Saved macro')";
            for (int index = 0; index < 104; index++)
            { command.Parameters.Clear(); command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$macro", Guid.NewGuid().ToString("N")); command.ExecuteNonQuery(); }
            tx.Commit();
        });
        var first = await Read(store); Assert.Equal(105, first.Total); Assert.Equal(100, first.Items.Count); Assert.Equal(100, first.NextOffset);
        var second = await Read(store, new(100, 100, first.Stamp)); Assert.Equal(5, second.Items.Count); Assert.Null(second.NextOffset);
        Assert.Empty(first.Items.Select(item => item.Target).Intersect(second.Items.Select(item => item.Target)));
        var changed = await store.SaveAsync(seed with { MacroName = "Changed" }, stamp, default);
        await Assert.ThrowsAsync<MacroAssignmentConflictException>(() => store.ReadAsync(new(100, 100, first.Stamp), default));
        Assert.Equal(stamp.Revision + 1, changed.Stamp.Revision);
    }

    [Theory]
    [InlineData("-journal")]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public async Task MissingPrimaryWithAnOrphanSidecarIsNeverRecreated(string suffix)
    {
        await using var fixture = new Fixture(); var store = fixture.Store(); byte[] orphan = [11, 22, 33]; File.WriteAllBytes(store.Path + suffix, orphan);
        await Assert.ThrowsAsync<MacroAssignmentStorageException>(() => Read(store));
        await Assert.ThrowsAsync<MacroAssignmentStorageException>(() => store.SaveAsync(Assignment(), MacroAssignmentStamp.Empty, default));
        Assert.False(File.Exists(store.Path)); Assert.Equal(orphan, File.ReadAllBytes(store.Path + suffix));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingBlankOrForeignDatabaseIsPreservedWithoutSchemaInitialization(bool foreign)
    {
        await using var fixture = new Fixture(); int creationCallbacks = 0;
        var store = fixture.Store(checkpoint: _ => Interlocked.Increment(ref creationCallbacks));
        if (foreign) fixture.Sql(store.Path, db => { using var command = db.CreateCommand(); command.CommandText = "CREATE TABLE foreign_data(value TEXT); INSERT INTO foreign_data VALUES('fixture'); PRAGMA user_version=1"; command.ExecuteNonQuery(); });
        else File.WriteAllBytes(store.Path, []);
        byte[] original = File.ReadAllBytes(store.Path);
        var error = await Assert.ThrowsAsync<MacroAssignmentStorageException>(() => Read(store)); Assert.Equal(MacroAssignmentStorageProblem.Damaged, error.Problem);
        await Assert.ThrowsAsync<MacroAssignmentStorageException>(() => store.SaveAsync(Assignment(), MacroAssignmentStamp.Empty, default));
        Assert.Equal(original, File.ReadAllBytes(store.Path)); Assert.Equal(0, creationCallbacks);
    }

    [Fact]
    public async Task FutureVersionIsRejectedForReadsAndWritesWithoutChangingItsBytes()
    {
        await using var fixture = new Fixture(); var store = fixture.Store(); var saved = Assignment(); var stamp = (await store.SaveAsync(saved, MacroAssignmentStamp.Empty, default)).Stamp;
        fixture.Sql(store.Path, db => { using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version=2"; command.ExecuteNonQuery(); });
        byte[] future = File.ReadAllBytes(store.Path);
        foreach (Func<Task> action in new Func<Task>[] { async () => { await Read(store); }, async () => { await store.SaveAsync(saved, stamp, default); }, async () => { await store.RemoveAsync(saved.Target, stamp, default); } })
        { var error = await Assert.ThrowsAsync<MacroAssignmentStorageException>(action); Assert.Equal(MacroAssignmentStorageProblem.UnsupportedVersion, error.Problem); Assert.Equal(future, File.ReadAllBytes(store.Path)); }
    }

    [Theory]
    [InlineData("target_name")]
    [InlineData("macro_name")]
    public async Task EmbeddedNulStoredAsActualUtf8BytesIsRejectedWithoutRepair(string column)
    {
        await using var fixture = new Fixture(); var store = fixture.Store(); var saved = Assignment(); var stamp = (await store.SaveAsync(saved, MacroAssignmentStamp.Empty, default)).Stamp;
        byte[] bytes = Encoding.UTF8.GetBytes("Valid\0hidden suffix");
        fixture.Sql(store.Path, db =>
        {
            using var command = db.CreateCommand(); command.CommandText = "UPDATE assignments SET " + column + "=CAST($bytes AS TEXT)"; command.Parameters.AddWithValue("$bytes", bytes); command.ExecuteNonQuery();
            command.CommandText = "SELECT length(CAST(" + column + " AS BLOB)) FROM assignments"; command.Parameters.Clear(); Assert.Equal(bytes.Length, Convert.ToInt32(command.ExecuteScalar()));
        });
        byte[] before = File.ReadAllBytes(store.Path);
        await Assert.ThrowsAsync<MacroAssignmentStorageException>(() => Read(store));
        await Assert.ThrowsAsync<MacroAssignmentStorageException>(() => store.SaveAsync(saved, stamp, default));
        Assert.Equal(before, File.ReadAllBytes(store.Path));
    }

    [Fact]
    public async Task ARowSavedUnderTheEarlierNarrowerNameRuleStaysReadableButNewNamesMeetTheWholeRule()
    {
        await using var fixture = new Fixture(); var store = fixture.Store(); var saved = Assignment();
        await store.SaveAsync(saved, MacroAssignmentStamp.Empty, default);
        // "code=" joined the assignment name rule when the four display-name rules were unified; a row saved before must stay readable.
        fixture.Sql(store.Path, db => { using var command = db.CreateCommand(); command.CommandText = "UPDATE assignments SET macro_name='Promo code=1'"; command.ExecuteNonQuery(); });
        var page = await Read(store); Assert.Equal("Promo code=1", Assert.Single(page.Items).MacroName);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(saved with { MacroName = "Promo code=2" }, page.Stamp, default));
        var written = await store.SaveAsync(saved with { MacroName = "Promo" }, page.Stamp, default).WaitAsync(Deadline);
        Assert.Equal(MacroAssignmentWriteState.Applied, written.State); Assert.Equal("Promo", Assert.Single((await Read(store)).Items).MacroName);
    }

    [Fact]
    public async Task MalformedOffPageRowCannotHideBehindAHealthyFirstPageOrWrite()
    {
        await using var fixture = new Fixture(); var store = fixture.Store(); var saved = Assignment(); var stamp = (await store.SaveAsync(saved, MacroAssignmentStamp.Empty, default)).Stamp;
        fixture.Sql(store.Path, db => { using var command = db.CreateCommand(); command.CommandText = "INSERT INTO assignments VALUES(3,$id,'Valid',$macro,$name)";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$macro", Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$name", new string('x', 201)); command.ExecuteNonQuery(); });
        byte[] before = File.ReadAllBytes(store.Path);
        await Assert.ThrowsAsync<MacroAssignmentStorageException>(() => store.ReadAsync(new(Limit: 1), default));
        await Assert.ThrowsAsync<MacroAssignmentStorageException>(() => store.RemoveAsync(saved.Target, stamp, default));
        Assert.Equal(before, File.ReadAllBytes(store.Path));
    }

    [Fact]
    public async Task ExtraSchemaObjectsAreRejectedBeforeTheirWriteSideEffectsCanRun()
    {
        await using var fixture = new Fixture(); var store = fixture.Store(); var saved = Assignment(); var stamp = (await store.SaveAsync(saved, MacroAssignmentStamp.Empty, default)).Stamp;
        fixture.Sql(store.Path, db => { using var command = db.CreateCommand(); command.CommandText = "CREATE TRIGGER unexpected BEFORE UPDATE ON assignments BEGIN DELETE FROM assignments; END"; command.ExecuteNonQuery(); });
        byte[] before = File.ReadAllBytes(store.Path);
        await Assert.ThrowsAsync<MacroAssignmentStorageException>(() => store.SaveAsync(saved with { MacroName = "New" }, stamp, default));
        Assert.Equal(before, File.ReadAllBytes(store.Path));
    }

    [Fact]
    public async Task RevisionOverflowRejectsChangesButCheckedIdenticalSaveRemainsANoOp()
    {
        await using var fixture = new Fixture(); var store = fixture.Store(); var saved = Assignment(); var stamp = (await store.SaveAsync(saved, MacroAssignmentStamp.Empty, default)).Stamp;
        fixture.Sql(store.Path, db => { using var command = db.CreateCommand(); command.CommandText = "UPDATE assignment_meta SET revision=$revision"; command.Parameters.AddWithValue("$revision", long.MaxValue); command.ExecuteNonQuery(); });
        var max = stamp with { Revision = long.MaxValue }; byte[] before = File.ReadAllBytes(store.Path);
        Assert.Equal(MacroAssignmentWriteState.Unchanged, (await store.SaveAsync(saved, max, default)).State);
        await Assert.ThrowsAsync<MacroAssignmentStorageException>(() => store.SaveAsync(saved with { MacroName = "Changed" }, max, default));
        Assert.Equal(before, File.ReadAllBytes(store.Path)); Assert.Equal(saved, Assert.Single((await Read(store)).Items));
    }

    [Fact]
    public async Task ExactReferenceBudgetAllowsReplacementButRejectsAnotherTargetWithoutPartialMutation()
    {
        await using var fixture = new Fixture(); var store = fixture.Store(); var saved = Assignment(); var stamp = (await store.SaveAsync(saved, MacroAssignmentStamp.Empty, default)).Stamp;
        fixture.Sql(store.Path, db =>
        {
            using var tx = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = tx;
            command.CommandText = "INSERT INTO assignments VALUES(2,$id,'Group',$macro,'Macro')";
            for (int index = 1; index < MacroAssignment.MaximumCount; index++)
            { command.Parameters.Clear(); command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$macro", Guid.NewGuid().ToString("N")); command.ExecuteNonQuery(); }
            tx.Commit();
        });
        Assert.Equal(MacroAssignment.MaximumCount, (await Read(store)).Total);
        var replacement = await store.SaveAsync(saved with { MacroName = "Replacement" }, stamp, default); byte[] before = File.ReadAllBytes(store.Path);
        await Assert.ThrowsAsync<MacroAssignmentCapacityException>(() => store.SaveAsync(Assignment(), replacement.Stamp, default));
        Assert.Equal(before, File.ReadAllBytes(store.Path)); Assert.Equal(MacroAssignment.MaximumCount, (await Read(store)).Total);
    }

    [Fact]
    public async Task OversizedFilesAreRejectedWithoutTruncatingThem()
    {
        await using var fixture = new Fixture(); var store = fixture.Store();
        using (var file = new FileStream(store.Path, FileMode.CreateNew)) file.SetLength(MacroAssignmentStore.MaximumDatabaseBytes + 1L);
        await Assert.ThrowsAsync<MacroAssignmentCapacityException>(() => Read(store));
        await Assert.ThrowsAsync<MacroAssignmentCapacityException>(() => store.SaveAsync(Assignment(), MacroAssignmentStamp.Empty, default));
        Assert.Equal(MacroAssignmentStore.MaximumDatabaseBytes + 1L, new FileInfo(store.Path).Length);
    }

    [Fact]
    public async Task PrecancelledMutationCreatesNoFileAndCompletedAppliedMetadataSurvivesLaterCancellation()
    {
        await using var fixture = new Fixture(); var store = fixture.Store("new"); using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var saved = Assignment();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(saved, MacroAssignmentStamp.Empty, cancelled.Token)); Assert.False(File.Exists(store.Path));
        using var after = new CancellationTokenSource(); var result = await store.SaveAsync(saved, MacroAssignmentStamp.Empty, after.Token); after.Cancel();
        Assert.Equal(MacroAssignmentWriteState.Applied, result.State); var page = await Read(store); Assert.Equal(result.Stamp, page.Stamp); Assert.Equal(saved, Assert.Single(page.Items));
    }

    [Fact]
    public async Task CancellationAfterOwnedCreateBeforeSqliteOpenRemovesOnlyTheBlankClaimAndAllowsRetry()
    {
        await using var fixture = new Fixture(); using var stop = new CancellationTokenSource();
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int creations = 0;
        var store = fixture.Store(checkpoint: phase =>
        {
            if (phase == MacroAssignmentStore.NewPrimaryCheckpoint.CreatedBeforeOpen && Interlocked.Increment(ref creations) == 1)
            { created.TrySetResult(); release.Task.GetAwaiter().GetResult(); }
        });
        try
        {
            var pending = store.SaveAsync(Assignment(), MacroAssignmentStamp.Empty, stop.Token);
            await created.Task.WaitAsync(Deadline); Assert.True(File.Exists(store.Path)); Assert.Equal(0, new FileInfo(store.Path).Length);
            stop.Cancel(); Assert.False(pending.IsCompleted); release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline));
            Assert.False(File.Exists(store.Path)); Assert.False(File.Exists(store.Path + "-journal"));
            Assert.Equal(MacroAssignmentStamp.Empty, (await Read(store)).Stamp);
            var saved = Assignment(); var applied = await store.SaveAsync(saved, MacroAssignmentStamp.Empty, default).WaitAsync(Deadline);
            Assert.Equal(MacroAssignmentWriteState.Applied, applied.State); Assert.Equal(saved, Assert.Single((await Read(store)).Items));
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncommittedCleanupPreservesReplacedOrNonemptyForeignWorkAfterTheOriginalPinCloses(bool replaceIdentity)
    {
        await using var fixture = new Fixture(); var foreign = fixture.Store("foreign"); var other = Assignment();
        var otherStamp = (await foreign.SaveAsync(other, MacroAssignmentStamp.Empty, default)).Stamp; byte[] otherBytes = File.ReadAllBytes(foreign.Path);
        using var stop = new CancellationTokenSource(); string targetPath = System.IO.Path.Combine(fixture.Root, "target", "macro-assignments.db");
        bool replaced = false;
        var target = fixture.Store("target", phase =>
        {
            if (phase == MacroAssignmentStore.NewPrimaryCheckpoint.CreatedBeforeOpen) stop.Cancel();
            if (phase != MacroAssignmentStore.NewPrimaryCheckpoint.BeforeUncommittedCleanup) return;
            // This exact seam runs after actual SQLite/pin disposal while the cooperative lease remains held.
            if (replaceIdentity) File.Delete(targetPath);
            File.WriteAllBytes(targetPath, replaceIdentity ? Array.Empty<byte>() : otherBytes); replaced = true;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => target.SaveAsync(Assignment(), MacroAssignmentStamp.Empty, stop.Token).WaitAsync(Deadline));
        Assert.True(replaced); Assert.True(File.Exists(target.Path));
        if (replaceIdentity)
        {
            // Length remains zero, so preserving this new actor's claim requires the file-identity fence.
            Assert.Empty(File.ReadAllBytes(target.Path));
            await Assert.ThrowsAsync<MacroAssignmentStorageException>(() => Read(target));
            await Assert.ThrowsAsync<MacroAssignmentStorageException>(() => target.SaveAsync(Assignment(), MacroAssignmentStamp.Empty, default));
            Assert.True(File.Exists(target.Path)); Assert.Empty(File.ReadAllBytes(target.Path));
        }
        else
        {
            // Same original file object, but foreign nonempty work must also prevent deletion.
            Assert.Equal(otherBytes, File.ReadAllBytes(target.Path));
            var page = await Read(target); Assert.Equal(otherStamp, page.Stamp); Assert.Equal(other, Assert.Single(page.Items));
            await Assert.ThrowsAsync<MacroAssignmentConflictException>(() => target.SaveAsync(Assignment(), MacroAssignmentStamp.Empty, default));
            Assert.Equal(otherBytes, File.ReadAllBytes(target.Path));
        }
    }

    [Fact]
    public async Task SuccessfulFirstCommitNeverEntersUncommittedCleanupEvenAfterLaterCancellationAndDisposal()
    {
        await using var fixture = new Fixture(); var phases = new System.Collections.Concurrent.ConcurrentQueue<MacroAssignmentStore.NewPrimaryCheckpoint>();
        var store = fixture.Store(checkpoint: phase =>
        { if (phase is MacroAssignmentStore.NewPrimaryCheckpoint.CreatedBeforeOpen or MacroAssignmentStore.NewPrimaryCheckpoint.BeforeUncommittedCleanup) phases.Enqueue(phase); });
        using var stop = new CancellationTokenSource(); var assignment = Assignment();
        var result = await store.SaveAsync(assignment, MacroAssignmentStamp.Empty, stop.Token).WaitAsync(Deadline); stop.Cancel();
        Assert.Equal(MacroAssignmentWriteState.Applied, result.State); Assert.Equal(MacroAssignmentStore.NewPrimaryCheckpoint.CreatedBeforeOpen, Assert.Single(phases));
        await store.DisposeAsync(); Assert.True(File.Exists(store.Path));
        var reopened = fixture.Store(); var page = await Read(reopened); Assert.Equal(result.Stamp, page.Stamp); Assert.Equal(assignment, Assert.Single(page.Items));
        Assert.Single(phases); Assert.DoesNotContain(MacroAssignmentStore.NewPrimaryCheckpoint.BeforeUncommittedCleanup, phases);
    }

    [Fact]
    public async Task PendingCallsAreBoundedAndDisposalCancelsAndJoinsTheirActualOwners()
    {
        await using var fixture = new Fixture(); var store = fixture.Store(); await store.SaveAsync(Assignment(), MacroAssignmentStamp.Empty, default);
        using var foreign = new FileStream(store.Path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var pending = Enumerable.Range(0, MacroAssignmentStore.MaximumPendingOperations).Select(_ => store.ReadAsync(new(), default)).ToArray();
        // Admission rejects synchronously instead of creating a fifth owned worker.
        Assert.Throws<MacroAssignmentBusyException>(() => { _ = store.ReadAsync(new(), default); });
        var disposing = store.DisposeAsync().AsTask();
        foreach (var task in pending) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Deadline));
        await disposing.WaitAsync(Deadline); Assert.All(pending, task => Assert.True(task.IsCompleted));
        Assert.Throws<ObjectDisposedException>(() => { _ = store.ReadAsync(new(), default); });
    }

    [Fact]
    public async Task CancellationWhileAnExternalSqliteWriterHoldsTheTransactionPreservesTheOriginal()
    {
        await using var fixture = new Fixture(); var store = fixture.Store(); var saved = Assignment(); var stamp = (await store.SaveAsync(saved, MacroAssignmentStamp.Empty, default)).Stamp;
        using var external = fixture.Open(store.Path); using var held = external.BeginTransaction(deferred: false); using var stop = new CancellationTokenSource();
        var pending = store.SaveAsync(saved with { MacroName = "Must not commit" }, stamp, stop.Token);
        await UntilStoreOwnsLease(store.Path + ".lock"); stop.Cancel(); held.Rollback();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline));
        var page = await Read(store); Assert.Equal(stamp, page.Stamp); Assert.Equal(saved, Assert.Single(page.Items));
    }

    [Fact]
    public async Task JunctionDatabaseDirectoryIsRejectedAndItsOtherOwnedFixtureStaysUntouched()
    {
        await using var fixture = new Fixture(); var actual = fixture.Store("actual"); var redirected = fixture.Store("redirected");
        await actual.SaveAsync(Assignment(), MacroAssignmentStamp.Empty, default);
        byte[] before = File.ReadAllBytes(actual.Path);
        // An actual directory junction exercises native redirection without changing Windows'
        // developer-mode policy or requiring symbolic-link privilege on the owner's PC.
        string alias = System.IO.Path.GetDirectoryName(redirected.Path)!, destination = System.IO.Path.GetDirectoryName(actual.Path)!;
        static string Quote(string path) => "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'";
        string script = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path " + Quote(alias) + " -Target " + Quote(destination) + " | Out-Null";
        var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32/WindowsPowerShell/v1.0/powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start)!; using var deadline = new CancellationTokenSource(Deadline);
        try { await process.WaitForExitAsync(deadline.Token); Assert.Equal(0, process.ExitCode); }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync().WaitAsync(Deadline); } }
        try
        {
            var failure = await Assert.ThrowsAsync<MacroAssignmentStorageException>(() => Read(redirected)); Assert.Equal(MacroAssignmentStorageProblem.UnsafePath, failure.Problem);
            await Assert.ThrowsAsync<MacroAssignmentStorageException>(() => redirected.SaveAsync(Assignment(), MacroAssignmentStamp.Empty, default)); Assert.Equal(before, File.ReadAllBytes(actual.Path));
        }
        finally { if (Directory.Exists(alias)) Directory.Delete(alias, false); }
    }

    private static async Task UntilStoreOwnsLease(string path)
    {
        long started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < Deadline)
        {
            try { using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return; }
            await Task.Delay(10);
        }
        Assert.Fail("The controlled store did not acquire its fixture lease before the deadline.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zenith-macro-assignments-" + Guid.NewGuid().ToString("N"));
        private readonly List<MacroAssignmentStore> _stores = [];
        public Fixture() { Directory.CreateDirectory(Root); File.WriteAllText(System.IO.Path.Combine(Root, ".owner"), "zenith-macro-assignment-tests-v1"); }
        public MacroAssignmentStore Store(string? child = null, Action<MacroAssignmentStore.NewPrimaryCheckpoint>? checkpoint = null)
        {
            string root = child is null ? Root : System.IO.Path.Combine(Root, child);
            var store = checkpoint is null ? new MacroAssignmentStore(root) : new MacroAssignmentStore(root, checkpoint);
            _stores.Add(store); return store;
        }
        public SqliteConnection Open(string path)
        { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 2 }.ToString()); db.Open(); return db; }
        public void Sql(string path, Action<SqliteConnection> action) { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); using var db = Open(path); action(db); }
        public async ValueTask DisposeAsync()
        {
            foreach (var store in _stores) await store.DisposeAsync();
            string full = System.IO.Path.GetFullPath(Root), temporary = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            Assert.StartsWith(temporary, full, StringComparison.OrdinalIgnoreCase); Assert.StartsWith("zenith-macro-assignments-", System.IO.Path.GetFileName(full));
            Assert.Equal("zenith-macro-assignment-tests-v1", File.ReadAllText(System.IO.Path.Combine(full, ".owner")));
            Directory.Delete(full, recursive: true);
        }
    }
}
