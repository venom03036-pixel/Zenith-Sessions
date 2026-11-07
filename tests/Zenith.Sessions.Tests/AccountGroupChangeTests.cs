using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Newly owned Temp SQLite libraries and one synthetic DPAPI preservation fixture only.
// No real profile, browser, account, network, native client or input is accessed.
public sealed class AccountGroupChangeTests : IDisposable
{
    private readonly string _name = "zenith-group-lifecycle-" + Guid.NewGuid().ToString("N");
    private string Root => Path.Combine(Path.GetTempPath(), _name);
    private string DatabasePath => Path.Combine(Root, "accounts.db");
    private static AccountGroupChange Rename(string source = "Raid", string destination = "Guild", bool merge = false)
        => new(AccountGroupChangeKind.Rename, source, destination, merge);
    private static AccountGroupChange Remove(string source = "Raid", string fallback = "General")
        => new(AccountGroupChangeKind.Remove, source, fallback);
    private static AccountProfile Profile(string label, string primary = "Raid", string extra = "")
        => AccountProfile.Create(label) with { Group = primary, AdditionalGroups = extra };
    private async Task<AccountRepository> OpenAsync()
    {
        var repository = new AccountRepository(Root);
        try { await repository.InitializeAsync(default); return repository; }
        catch { repository.Dispose(); throw; }
    }
    private async Task<SqliteConnection> DatabaseAsync()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false, DefaultTimeout = 5 }.ToString());
        try { await db.OpenAsync(); return db; } catch { await db.DisposeAsync(); throw; }
    }

    [Fact]
    public void ChangesValidateNamesAndKindsWhileAllowingExactAndCaseOnlyRename()
    {
        Assert.Equal(Rename(), Rename(" Raid ", " Guild ").Validated());
        Assert.Equal(Rename("Raid", "RAID"), Rename("Raid", "RAID").Validated());
        Assert.Equal(Rename("Raid", "Raid"), Rename("Raid", "Raid").Validated());
        Assert.Throws<ArgumentException>(() => Remove("Raid", "rAID").Validated());
        Assert.Throws<ArgumentException>(() => new AccountGroupChange((AccountGroupChangeKind)200, "Raid", "Guild").Validated());
        Assert.Throws<ArgumentException>(() => Rename(null!, "Guild").Validated());
        Assert.Throws<ArgumentException>(() => Rename("Raid", null!).Validated());
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Bad\nName")]
    [InlineData("Bad\tName")]
    [InlineData("Bad\0Name")]
    [InlineData("Bad\u007fName")]
    public void InvalidSourceAndDestinationNamesAreRejected(string name)
    {
        Assert.Throws<ArgumentException>(() => Rename(name, "Guild").Validated());
        Assert.Throws<ArgumentException>(() => Remove("Raid", name).Validated());
        Assert.Throws<ArgumentException>(() => Rename(new string('a', 61), "Guild").Validated());
        Assert.Throws<ArgumentException>(() => Rename("Raid", "bad" + (char)0xd800).Validated());
    }

    [Fact]
    public async Task GlobalRenameUpdatesMoreThanOnePageAndPreservesEveryNonGroupField()
    {
        using var repository = await OpenAsync();
        var profiles = Enumerable.Range(0, 301).Select(index => Profile("Synthetic " + index,
            index < 150 ? "Raid" : "Other", index is >= 150 and < 300 ? "Before\nRaid\nAfter" : "Keep") with
        { Username = "player_" + index, Notes = "Private metadata " + index, RobloxUserId = 1000 + index,
            PreferredPlaceId = 42, Priority = index % 100, IsFavorite = index % 2 == 0 }).ToArray();
        await repository.CreateManyAsync(profiles, default); var before = await Rows(repository);
        var preview = await repository.PreviewGroupChangeAsync(Rename(), default);
        Assert.Equal(300, preview.AffectedCount); Assert.Equal(150, preview.PrimaryCount); Assert.False(preview.DestinationExists);
        Assert.Equal(64, preview.SnapshotFingerprint.Length); Assert.Equal(300, await repository.ApplyGroupChangeAsync(preview, default));
        var after = (await Rows(repository)).ToDictionary(profile => profile.Id);
        foreach (var old in before)
        {
            var current = after[old.Id];
            if (!AccountGroups.Contains(old, "Raid")) { Assert.Equal(old, current); continue; }
            Assert.Equal(old with { Group = old.Group == "Raid" ? "Guild" : "Other",
                AdditionalGroups = old.Group == "Raid" ? "Keep" : "Before\nGuild\nAfter", Revision = old.Revision + 1, UpdatedUtc = current.UpdatedUtc }, current);
        }
        Assert.Equal(0, (await repository.ReadAsync(new(Group: "Raid"), default)).Total);
        Assert.Equal(300, (await repository.ReadAsync(new(Group: "Guild"), default)).Total);
        using var reopened = await OpenAsync(); Assert.Equal(after.Values.OrderBy(row => row.Id), (await Rows(reopened)).OrderBy(row => row.Id));
    }

    [Theory]
    [InlineData("Raid", "RAID")]
    [InlineData("Équipe", "éQUIPE")]
    [InlineData("%", "%")]
    [InlineData("' OR 1=1 --", "' OR 1=1 --")]
    public async Task SourceMatchingUsesExactOrdinalUnicodeMembershipAndLiteralNames(string source, string stored)
    {
        using var repository = await OpenAsync();
        await repository.CreateManyAsync([Profile("Primary", stored), Profile("Extra", "Other", "Before\n" + stored + "\nAfter"),
            Profile("Not a substring", "Other", source + " longer")], default);
        var preview = await repository.PreviewGroupChangeAsync(Rename(source, "Changed"), default);
        Assert.Equal(2, preview.AffectedCount); Assert.Equal(1, preview.PrimaryCount);
        Assert.Equal(2, await repository.ApplyGroupChangeAsync(preview, default));
        Assert.Equal(2, (await repository.ReadAsync(new(Group: "Changed"), default)).Total);
        Assert.Equal(1, (await repository.ReadAsync(new(Group: source + " longer"), default)).Total);
    }

    [Fact]
    public async Task MergeRequiresExplicitConsentAndDeduplicatesInOriginalMembershipOrder()
    {
        using var repository = await OpenAsync();
        await repository.CreateGroupAsync("Crew",null,(await repository.ReadGroupCatalogAsync(new(),default)).CatalogRevision,default);
        var primary = await repository.SaveAsync(Profile("Primary", "Raid", "Before\nCREW\nAfter"), default);
        var sourceFirst = await repository.SaveAsync(Profile("Source first", "Other", "Before\nRAID\nMiddle\ncrew\nAfter"), default);
        var destinationFirst = await repository.SaveAsync(Profile("Destination first", "Other", "Before\ncReW\nMiddle\nRaid\nAfter"), default);
        var untouched = await repository.SaveAsync(Profile("Destination only", "Crew", "Stable"), default);
        var preview = await repository.PreviewGroupChangeAsync(Rename("Raid", "Crew"), default); string before = await RawState();
        Assert.True(preview.DestinationExists); Assert.Equal(3, preview.AffectedCount);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => repository.ApplyGroupChangeAsync(preview, default));
        Assert.Contains("merg", error.Message, StringComparison.OrdinalIgnoreCase); Assert.Equal(before, await RawState());
        var confirmed = await repository.PreviewGroupChangeAsync(Rename("Raid", "Crew", true), default);
        Assert.NotEqual(preview.SnapshotFingerprint, confirmed.SnapshotFingerprint);
        Assert.Equal(3, await repository.ApplyGroupChangeAsync(confirmed, default));
        Assert.Equal(("Crew", "Before\nAfter"), Groups((await repository.FindAsync(primary.Id, default))!));
        Assert.Equal(("Other", "Before\nCrew\nMiddle\nAfter"), Groups((await repository.FindAsync(sourceFirst.Id, default))!));
        Assert.Equal(("Other", "Before\ncReW\nMiddle\nAfter"), Groups((await repository.FindAsync(destinationFirst.Id, default))!));
        Assert.Equal(untouched, await repository.FindAsync(untouched.Id, default));
    }

    [Fact]
    public async Task RemoveReassignsOnlyPrimaryMembersToTheExplicitFallbackAndNeverDeletesProfiles()
    {
        using var repository = await OpenAsync();
        var primary = await repository.SaveAsync(Profile("Primary", "Raid", "Before\nFallback\nAfter"), default);
        var extra = await repository.SaveAsync(Profile("Extra", "Other", "Raid\nAfter"), default);
        var untouched = await repository.SaveAsync(Profile("Fallback only", "Fallback", "Stable"), default);
        var preview = await repository.PreviewGroupChangeAsync(Remove("Raid", "Fallback"), default);
        Assert.Equal(2, preview.AffectedCount); Assert.Equal(1, preview.PrimaryCount); Assert.True(preview.DestinationExists);
        Assert.Equal(2, await repository.ApplyGroupChangeAsync(preview, default));
        Assert.Equal(("Fallback", "Before\nAfter"), Groups((await repository.FindAsync(primary.Id, default))!));
        Assert.Equal(("Other", "After"), Groups((await repository.FindAsync(extra.Id, default))!));
        Assert.Equal(untouched, await repository.FindAsync(untouched.Id, default));
        Assert.Equal(3, (await repository.ReadAsync(new(), default)).Total); Assert.Equal(0, (await repository.ReadCredentialCleanupAsync(default)).Total);
        Assert.DoesNotContain(await repository.ReadGroupsAsync(default), group => StringComparer.OrdinalIgnoreCase.Equals(group, "Raid"));
    }

    [Fact]
    public async Task NewFallbackIsNotAddedToProfilesWhereOnlyTheAdditionalMembershipWasRemoved()
    {
        using var repository = await OpenAsync();
        var primary = await repository.SaveAsync(Profile("Primary", "Raid", "Keep"), default);
        var extra = await repository.SaveAsync(Profile("Extra", "Other", "Keep\nRaid"), default);
        var preview = await repository.PreviewGroupChangeAsync(Remove("Raid", "Unassigned"), default); Assert.False(preview.DestinationExists);
        Assert.Equal(2, await repository.ApplyGroupChangeAsync(preview, default));
        Assert.Equal(("Unassigned", "Keep"), Groups((await repository.FindAsync(primary.Id, default))!));
        Assert.Equal(("Other", "Keep"), Groups((await repository.FindAsync(extra.Id, default))!));
    }

    [Fact]
    public async Task CaseOnlyRenameChangesOnlyDifferentSpellingsAndAnExactRepeatPreservesRevisionsAndTimes()
    {
        using var repository = await OpenAsync();
        var first = await repository.SaveAsync(Profile("A", "Raid"), default);
        var second = await repository.SaveAsync(Profile("B", "raid"), default);
        var noop = await repository.SaveAsync(Profile("C", "Other", "Before\nRAID\nAfter"), default);
        var preview = await repository.PreviewGroupChangeAsync(Rename("Raid", "RAID"), default);
        Assert.Equal(3, preview.AffectedCount); Assert.Equal(2, preview.PrimaryCount); Assert.True(preview.DestinationExists);
        Assert.Equal(2, await repository.ApplyGroupChangeAsync(preview, default));
        Assert.Equal(noop, await repository.FindAsync(noop.Id, default));
        Assert.Equal(first.Revision + 1, (await repository.FindAsync(first.Id, default))!.Revision);
        Assert.Equal(second.Revision + 1, (await repository.FindAsync(second.Id, default))!.Revision);
        string before = await RawState(); var exact = await repository.PreviewGroupChangeAsync(Rename("RAID", "RAID"), default);
        Assert.Equal(3, exact.AffectedCount); Assert.Equal(0, await repository.ApplyGroupChangeAsync(exact, default)); Assert.Equal(before, await RawState());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingSourcePreviewIsBoundedAndApplyIsAnUnchangedZero(bool removing)
    {
        using var repository = await OpenAsync(); await repository.SaveAsync(Profile("Private alias", "Guild") with { Notes = "Private note", Username = "private_username" }, default);
        var preview = await repository.PreviewGroupChangeAsync(removing ? Remove("Absent", "Guild") : Rename("Absent", "Guild"), default);
        Assert.Equal(0, preview.AffectedCount); Assert.Equal(0, preview.PrimaryCount); Assert.True(preview.DestinationExists);
        string before = await RawState(); Assert.Equal(0, await repository.ApplyGroupChangeAsync(preview, default)); Assert.Equal(before, await RawState());
        string json = JsonSerializer.Serialize(preview); Assert.InRange(json.Length, 1, 600);
        foreach (string excluded in new[] { "Private alias", "Private note", "private_username", "ProfileId", "Credential" }) Assert.DoesNotContain(excluded, json);
    }

    [Fact]
    public async Task FingerprintIsDeterministicAcrossRepositoriesAndUnrelatedEditsDoNotInvalidateIt()
    {
        using var first = await OpenAsync(); using var other = await OpenAsync();
        await first.SaveAsync(Profile("Affected", "Raid", "Keep"), default);
        var unrelated = await first.SaveAsync(Profile("Unrelated", "Other"), default);
        var preview = await first.PreviewGroupChangeAsync(Rename(), default);
        Assert.Equal(preview, await other.PreviewGroupChangeAsync(Rename(), default));
        await other.SaveAsync(unrelated with { Notes = "Changed outside the reviewed membership" }, default);
        Assert.Equal(preview, await first.PreviewGroupChangeAsync(Rename(), default));
        Assert.Equal(1, await first.ApplyGroupChangeAsync(preview, default));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("new-primary")]
    [InlineData("new-extra")]
    [InlineData("removed")]
    [InlineData("membership-left")]
    [InlineData("destination-primary-added")]
    [InlineData("destination-extra-added")]
    [InlineData("destination-removed")]
    public async Task ForeignRevisionMembershipAndDestinationChangesInvalidateTheEntirePreview(string change)
    {
        using var first = await OpenAsync(); using var other = await OpenAsync();
        var member = await first.SaveAsync(Profile("Member"), default);
        AccountProfile? destination = change == "destination-removed" ? await first.SaveAsync(Profile("Destination", "Guild"), default) : null;
        var preview = await first.PreviewGroupChangeAsync(Rename(), default);
        switch (change)
        {
            case "revision": await other.SaveAsync(member with { Notes = "Newer note" }, default); break;
            case "new-primary": await other.SaveAsync(Profile("New member"), default); break;
            case "new-extra": await other.SaveAsync(Profile("New extra member", "Other", "Raid"), default); break;
            case "removed": await other.DeleteAsync(member.Id, member.Revision, default); break;
            case "membership-left": await other.SaveAsync(member with { Group = "Other" }, default); break;
            case "destination-primary-added": await other.SaveAsync(Profile("New target", "Guild"), default); break;
            case "destination-extra-added": await other.SaveAsync(Profile("New target", "Other", "Guild"), default); break;
            case "destination-removed":
                await other.DeleteAsync(destination!.Id, destination.Revision, default);
                Assert.NotNull(await other.FindGroupAsync("Guild",default));
                Assert.Equal(0,await other.ApplyGroupChangeAsync(await other.PreviewGroupChangeAsync(Remove("Guild","General"),default),default));
                break;
        }
        string current = await RawState();
        await Assert.ThrowsAsync<RevisionConflictException>(() => first.ApplyGroupChangeAsync(preview, default)); Assert.Equal(current, await RawState());
    }

    [Fact]
    public async Task StaleNoopAndNewMembershipAfterAnEmptyPreviewAreStillRejected()
    {
        using var repository = await OpenAsync(); var member = await repository.SaveAsync(Profile("Member"), default);
        var noop = await repository.PreviewGroupChangeAsync(Rename("Raid", "Raid"), default);
        await repository.SaveAsync(member with { Notes = "New revision" }, default);
        await Assert.ThrowsAsync<RevisionConflictException>(() => repository.ApplyGroupChangeAsync(noop, default));
        var empty = await repository.PreviewGroupChangeAsync(Rename("Absent", "Guild"), default);
        await repository.SaveAsync(Profile("New matching row", "Absent"), default);
        await Assert.ThrowsAsync<RevisionConflictException>(() => repository.ApplyGroupChangeAsync(empty, default));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("primary")]
    [InlineData("destination")]
    [InlineData("hash")]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("merge")]
    public async Task ForgedPreviewFieldsCannotReuseAnOtherwiseValidFingerprint(string field)
    {
        using var repository = await OpenAsync(); await repository.SaveAsync(Profile("Member"), default);
        var preview = await repository.PreviewGroupChangeAsync(Rename(), default);
        var forged = field switch
        {
            "count" => preview with { AffectedCount = 2 }, "primary" => preview with { PrimaryCount = 0 },
            "destination" => preview with { DestinationExists = true }, "hash" => preview with { SnapshotFingerprint = new string('A', 64) },
            "source" => preview with { Change = Rename("Different", "Guild") }, "target" => preview with { Change = Rename("Raid", "Different") },
            _ => preview with { Change = Rename(merge: true) }
        };
        string before = await RawState(); await Assert.ThrowsAsync<RevisionConflictException>(() => repository.ApplyGroupChangeAsync(forged, default));
        Assert.Equal(before, await RawState());
    }

    [Theory]
    [InlineData("empty-hash")]
    [InlineData("long-hash")]
    [InlineData("invalid-hash")]
    [InlineData("negative")]
    [InlineData("excess-primary")]
    [InlineData("noncanonical")]
    public async Task MalformedPreviewsAreRejectedBeforeMutation(string field)
    {
        using var repository = await OpenAsync(); await repository.SaveAsync(Profile("Member"), default);
        var preview = await repository.PreviewGroupChangeAsync(Rename(), default);
        var invalid = field switch
        {
            "empty-hash" => preview with { SnapshotFingerprint = "" }, "long-hash" => preview with { SnapshotFingerprint = new string('A', 65) },
            "invalid-hash" => preview with { SnapshotFingerprint = new string('Z', 64) }, "negative" => preview with { AffectedCount = -1 },
            "excess-primary" => preview with { PrimaryCount = 2 }, _ => preview with { Change = Rename(" Raid ", "Guild") }
        };
        string before = await RawState(); await Assert.ThrowsAsync<ArgumentException>(() => repository.ApplyGroupChangeAsync(invalid, default)); Assert.Equal(before, await RawState());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[1]")]
    [InlineData("[\"Crew\",\"crew\"]")]
    [InlineData("[\"Raid\"]")]
    [InlineData("[\" Crew\"]")]
    [InlineData("[\"bad\\nname\"]")]
    public async Task CorruptStoredGroupMetadataIsPreservedAndPreventsAnyGlobalChange(string invalid)
    {
        using var repository = await OpenAsync(); var first = await repository.SaveAsync(Profile("A"), default);
        var corrupted = await repository.SaveAsync(Profile("B"), default); var preview = await repository.PreviewGroupChangeAsync(Rename(), default);
        await Execute("PRAGMA ignore_check_constraints=ON; UPDATE profiles SET additional_groups=$value WHERE id=$id", ("$value", invalid), ("$id", corrupted.Id.ToString("D")));
        string before = await RawState();
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.PreviewGroupChangeAsync(Rename(), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.ApplyGroupChangeAsync(preview, default));
        Assert.Equal(before, await RawState()); Assert.Equal(first, await repository.FindAsync(first.Id, default));
    }

    [Fact]
    public async Task UnrelatedCorruptGroupsAreNotSilentlySkippedByASourceFilter()
    {
        using var repository = await OpenAsync(); var member = await repository.SaveAsync(Profile("Member"), default);
        var unrelated = await repository.SaveAsync(Profile("Unrelated", "Other"), default); var preview = await repository.PreviewGroupChangeAsync(Rename(), default);
        await Execute("PRAGMA ignore_check_constraints=ON; UPDATE profiles SET additional_groups='{}' WHERE id=$id", ("$id", unrelated.Id.ToString("D")));
        string before = await RawState(); await Assert.ThrowsAsync<InvalidDataException>(() => repository.ApplyGroupChangeAsync(preview, default));
        Assert.Equal(before, await RawState()); Assert.Equal(member, await repository.FindAsync(member.Id, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedStoredTextIncludingAnEmbeddedNulFailsWithoutRepair(bool primary)
    {
        using var repository = await OpenAsync(); var member = await repository.SaveAsync(Profile("Member"), default);
        var preview = await repository.PreviewGroupChangeAsync(Rename(), default);
        string value = primary ? "Raid\0" + new string('x', 100000) : "[\"Crew\"]\0" + new string(' ', 100000);
        // Bind bytes, then CAST inside SQLite: string binding/decoding can stop at the NUL
        // and would otherwise leave a valid short prefix instead of the intended corrupt row.
        byte[] intended = Encoding.UTF8.GetBytes(value);
        await Execute(primary ? "UPDATE profiles SET group_name=CAST($value AS TEXT) WHERE id=$id"
            : "PRAGMA ignore_check_constraints=ON; UPDATE profiles SET additional_groups=CAST($value AS TEXT) WHERE id=$id", ("$value", intended), ("$id", member.Id.ToString("D")));
        string column = primary ? "group_name" : "additional_groups";
        await using (var db = await DatabaseAsync())
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT length(CAST(" + column + " AS BLOB)),instr(CAST(" + column + " AS BLOB),x'00') FROM profiles WHERE id=$id";
            command.Parameters.AddWithValue("$id", member.Id.ToString("D")); await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            Assert.Equal((long)intended.Length, reader.GetInt64(0)); Assert.Equal((long)Array.IndexOf(intended, (byte)0) + 1, reader.GetInt64(1));
        }
        Assert.Equal(intended, await StoredBytes());
        string before = await RawState();
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.PreviewGroupChangeAsync(Rename(), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.ApplyGroupChangeAsync(preview, default));
        Assert.Equal(before, await RawState()); Assert.Equal(intended, await StoredBytes());

        async Task<byte[]> StoredBytes()
        {
            await using var db = await DatabaseAsync(); using var command = db.CreateCommand();
            command.CommandText = "SELECT CAST(" + column + " AS BLOB) FROM profiles WHERE id=$id";
            command.Parameters.AddWithValue("$id", member.Id.ToString("D")); return Assert.IsType<byte[]>(await command.ExecuteScalarAsync());
        }
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(long.MaxValue - 1)]
    [InlineData(long.MaxValue)]
    public async Task InvalidOrExhaustedRevisionsPreventEveryRowFromChanging(long revision)
    {
        using var repository = await OpenAsync(); var first = await repository.SaveAsync(Profile("A"), default);
        var exhausted = await repository.SaveAsync(Profile("B"), default); var preview = await repository.PreviewGroupChangeAsync(Rename(), default);
        await Execute("UPDATE profiles SET revision=$revision WHERE id=$id", ("$revision", revision), ("$id", exhausted.Id.ToString("D")));
        string before = await RawState(); await Assert.ThrowsAsync<InvalidDataException>(() => repository.ApplyGroupChangeAsync(preview, default));
        Assert.Equal(before, await RawState()); Assert.Equal(first, await repository.FindAsync(first.Id, default));
    }

    [Fact]
    public async Task AnExactNoopDoesNotAdvanceAnOtherwiseValidExhaustedRevision()
    {
        using var repository = await OpenAsync(); var member = await repository.SaveAsync(Profile("Member"), default);
        await Execute("UPDATE profiles SET revision=$revision WHERE id=$id", ("$revision", long.MaxValue - 1), ("$id", member.Id.ToString("D")));
        var preview = await repository.PreviewGroupChangeAsync(Rename("Raid", "Raid"), default); string before = await RawState();
        Assert.Equal(0, await repository.ApplyGroupChangeAsync(preview, default)); Assert.Equal(before, await RawState());
    }

    [Fact]
    public async Task ATriggerFailureOnALaterRowRollsBackAlreadyExecutedMembershipUpdates()
    {
        using var repository = await OpenAsync();
        var first = await repository.SaveAsync(Profile("A") with { Id = Guid.Parse("00000000-0000-4000-8000-000000000001") }, default);
        var second = await repository.SaveAsync(Profile("B") with { Id = Guid.Parse("00000000-0000-4000-8000-000000000002") }, default);
        await Execute("CREATE TRIGGER synthetic_later_group_failure BEFORE UPDATE OF group_name ON profiles WHEN OLD.label='B' BEGIN SELECT RAISE(ABORT,'synthetic later group failure'); END");
        var preview = await repository.PreviewGroupChangeAsync(Rename(), default); string before = await RawState();
        var error = await Assert.ThrowsAsync<SqliteException>(() => repository.ApplyGroupChangeAsync(preview, default)); Assert.Equal(19, error.SqliteErrorCode);
        Assert.Equal(before, await RawState()); Assert.Equal(first, await repository.FindAsync(first.Id, default)); Assert.Equal(second, await repository.FindAsync(second.Id, default));
    }

    [Fact]
    public async Task CancelledPreviewAndApplyPreserveAllMetadata()
    {
        using var repository = await OpenAsync(); await repository.SaveAsync(Profile("Member"), default);
        var preview = await repository.PreviewGroupChangeAsync(Rename(), default); string before = await RawState(); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.PreviewGroupChangeAsync(Rename(), stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ApplyGroupChangeAsync(preview, stop.Token)); Assert.Equal(before, await RawState());
    }

    [Fact]
    public async Task CancellationOfAPendingForeignWriterWaitIsJoinedBeforeReturningWithoutChanges()
    {
        using var repository = await OpenAsync(); await repository.SaveAsync(Profile("Member"), default);
        var preview = await repository.PreviewGroupChangeAsync(Rename(), default); string before = await RawState();
        await using var foreign = await DatabaseAsync(); using var writer = foreign.BeginTransaction(deferred: false);
        using var stop = new CancellationTokenSource(); var applying = repository.ApplyGroupChangeAsync(preview, stop.Token);
        Assert.False(applying.IsCompleted); stop.Cancel(); writer.Rollback();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => applying.WaitAsync(TimeSpan.FromSeconds(4))); Assert.Equal(before, await RawState());
    }

    [Fact]
    public async Task ConcurrentRepositoryWritersHaveOneAtomicWinnerAndOneStalePreview()
    {
        using var first = await OpenAsync(); using var second = await OpenAsync();
        await first.CreateManyAsync(Enumerable.Range(0, 260).Select(index => Profile("Synthetic " + index)).ToArray(), default);
        var preview = await first.PreviewGroupChangeAsync(Rename(), default); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<(int? Count, Exception? Error)> Run(AccountRepository repository)
        { await gate.Task; try { return (await repository.ApplyGroupChangeAsync(preview, default), null); } catch (Exception error) { return (null, error); } }
        var one = Run(first); var two = Run(second); gate.TrySetResult(); var results = await Task.WhenAll(one, two).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(260, Assert.Single(results.Where(result => result.Count is not null)).Count);
        Assert.IsType<RevisionConflictException>(Assert.Single(results.Where(result => result.Error is not null)).Error);
        Assert.Equal(260, (await first.ReadAsync(new(Group: "Guild"), default)).Total); Assert.Equal(0, (await second.ReadAsync(new(Group: "Raid"), default)).Total);
        Assert.All(await Rows(first), row => Assert.Equal(2, row.Revision));
    }

    [Fact]
    public async Task PreviewUsesAReadSnapshotThenRejectsAForeignCommitThatHappensAfterward()
    {
        using var repository = await OpenAsync(); var member = await repository.SaveAsync(Profile("Member"), default);
        await using var foreign = await DatabaseAsync(); using var writer = foreign.BeginTransaction(deferred: false);
        using var command = foreign.CreateCommand(); command.Transaction = writer;
        command.CommandText = "UPDATE profiles SET revision=revision+1,group_name='Other' WHERE id=$id"; command.Parameters.AddWithValue("$id", member.Id.ToString("D"));
        await command.ExecuteNonQueryAsync(); var preview = await repository.PreviewGroupChangeAsync(Rename(), default); Assert.Equal(1, preview.AffectedCount);
        writer.Commit(); string before = await RawState();
        await Assert.ThrowsAsync<RevisionConflictException>(() => repository.ApplyGroupChangeAsync(preview, default)); Assert.Equal(before, await RawState());
    }

    [Fact]
    public async Task GroupLifecyclePreservesProtectedAuthorizationCleanupAndSchemaWithoutMigration()
    {
        using var repository = await OpenAsync();
        var member = await repository.SaveAsync(Profile("Preserved alias") with { Username = "metadata_hint", RobloxUserId = 456, Notes = "Preserved note",
            IsFavorite = true, Priority = 17, PreferredPlaceId = 42 }, default);
        var deleted = await repository.SaveAsync(Profile("Previously removed", "Other"), default); await repository.DeleteAsync(deleted.Id, deleted.Revision, default);
        using var vault = new ProtectedCredentialVault(Root);
        var grant = new AuthorizationRecord(member.Id, RobloxSessionIdentity.Provider, 456, 1, "", "", DateTimeOffset.MaxValue,
            "verified_player", AuthorizationHealth.Linked, RobloxSessionIdentity.Issuer, "456", SessionCookie: "synthetic-group-preservation-session");
        await vault.SaveAsync(grant, default); var hashes = ProtectedHashes(); var cleanup = await repository.ReadCredentialCleanupAsync(default);
        Assert.Equal(1, await repository.ApplyGroupChangeAsync(await repository.PreviewGroupChangeAsync(Rename(), default), default));
        Assert.Equal(1, await repository.ApplyGroupChangeAsync(await repository.PreviewGroupChangeAsync(Remove("Guild", "General"), default), default));
        Assert.Equal(hashes, ProtectedHashes()); Assert.Equal(grant, await vault.ReadAsync(member.Id, default));
        var current = (await repository.FindAsync(member.Id, default))!;
        Assert.Equal(member with { Group = "General", Revision = member.Revision + 2, UpdatedUtc = current.UpdatedUtc }, current);
        Assert.Equal(cleanup.ProfileIds, (await repository.ReadCredentialCleanupAsync(default)).ProfileIds);
        await using var db = await DatabaseAsync(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version";
        Assert.Equal(5L, await command.ExecuteScalarAsync()); Assert.Empty(Directory.GetFiles(Root, "*.schema*.bak"));
    }

    private static (string Primary, string Additional) Groups(AccountProfile profile) => (profile.Group, profile.AdditionalGroups);
    private static async Task<List<AccountProfile>> Rows(AccountRepository repository)
    {
        var rows = new List<AccountProfile>(); int page = 0;
        while (true) { var next = await repository.ReadAsync(new(Page: page++), default); rows.AddRange(next.Items); if (rows.Count >= next.Total) return rows; }
    }
    private async Task Execute(string sql, params (string Name, object Value)[] parameters)
    {
        await using var db = await DatabaseAsync(); using var command = db.CreateCommand(); command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }
    private async Task<string> RawState()
    {
        await using var db = await DatabaseAsync(); using var command = db.CreateCommand();
        command.CommandText = "SELECT id,group_name,additional_groups,revision,updated_utc FROM profiles ORDER BY id";
        var rows = new List<object[]>(); await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add([reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4)]);
        return JsonSerializer.Serialize(rows);
    }
    private string[] ProtectedHashes() => Directory.GetFiles(Path.Combine(Root, "accounts.vault"), "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(Root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    public void Dispose()
    {
        string absolute = Path.GetFullPath(Root), temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(Path.GetDirectoryName(absolute), temp, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(absolute) != _name)
            throw new InvalidOperationException("Unexpected synthetic group-lifecycle cleanup path.");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
    }
}
