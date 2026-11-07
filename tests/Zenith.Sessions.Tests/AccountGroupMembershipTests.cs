using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// New owned metadata-only Temp libraries; no credentials, network, browser or real profiles.
public sealed class AccountGroupMembershipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-group-membership-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_root, "accounts.db");
    private async Task<AccountRepository> OpenAsync()
    {
        var repository = new AccountRepository(_root);
        try { await repository.InitializeAsync(default); return repository; }
        catch { repository.Dispose(); throw; }
    }
    private async Task<SqliteConnection> DatabaseAsync()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        try { await db.OpenAsync(); return db; } catch { await db.DisposeAsync(); throw; }
    }

    [Fact]
    public void CanonicalNamesPreservePrimaryOrderAndUnicodeCaseInsensitiveMembership()
    {
        var profile = (AccountProfile.Create("Synthetic") with
        { Group = " Équipe ", AdditionalGroups = "\n éQUIPE \r\n Ωmega \nωMEGA\n Crew \n\nCrew" }).Validated();
        Assert.Equal("Équipe", profile.Group); Assert.Equal("Ωmega\nCrew", profile.AdditionalGroups);
        Assert.Equal(new[] { "Équipe", "Ωmega", "Crew" }, profile.GroupNames);
        Assert.Equal("Équipe · Ωmega · Crew", profile.GroupsSummary);
        Assert.True(AccountGroups.Contains(profile, "ωmega")); Assert.True(AccountGroups.Contains(profile, "éQUIPE"));
        Assert.False(AccountGroups.Contains(profile, "Cre"));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)profile.GroupNames)[0] = "Changed");
        Assert.Equal(profile, AccountGroups.Add(profile, "crew"));
        Assert.Equal("Ωmega\nCrew\nRaid", AccountGroups.Add(profile, " Raid ").AdditionalGroups);
    }

    [Theory]
    [InlineData("Crew\rRaid")]
    [InlineData("Crew\tRaid")]
    [InlineData("Crew\0Raid")]
    [InlineData("Crew\u007fRaid")]
    [InlineData("\t\nCrew")]
    public void NonNewlineControlsAreRejectedRatherThanSilentlyTrimmed(string groups)
        => Assert.Throws<ArgumentException>(() => (AccountProfile.Create("Synthetic") with { AdditionalGroups = groups }).Validated());

    [Fact]
    public void BoundsApplyToRawInputDistinctMembershipsAndNames()
    {
        var full = (AccountProfile.Create("Synthetic") with { AdditionalGroups = string.Join('\n', Enumerable.Range(1, 31).Select(i => "Group " + i)) }).Validated();
        Assert.Equal(32, full.GroupNames.Count); Assert.Equal(full, AccountGroups.Add(full, "General"));
        Assert.Throws<ArgumentException>(() => AccountGroups.Add(full, "Overflow"));
        Assert.Throws<ArgumentException>(() => (full with { AdditionalGroups = new string(' ', 2049) }).Validated());
        Assert.Throws<ArgumentException>(() => (full with { AdditionalGroups = new string('x', 61) }).Validated());
        Assert.Throws<ArgumentException>(() => (full with { AdditionalGroups = null! }).Validated());
        Assert.Throws<ArgumentException>(() => (full with { AdditionalGroups = "bad" + (char)0xd800 }).Validated());
        Assert.Equal("", AccountGroups.Canonicalize("General", "\n  \r\n General\nGENERAL"));
    }

    [Fact]
    public void DefaultRecordEqualityAndMetadataJsonRemainCompatible()
    {
        var old = AccountProfile.Create("Synthetic"); var explicitDefault = old with { AdditionalGroups = "" };
        Assert.Equal(old, explicitDefault); Assert.Equal(old.GetHashCode(), explicitDefault.GetHashCode());
        Assert.NotEqual(old, old with { AdditionalGroups = "Crew" });
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(old with { AdditionalGroups = "Crew" }));
        Assert.Equal("Crew", json.RootElement.GetProperty("AdditionalGroups").GetString());
        Assert.False(json.RootElement.TryGetProperty("GroupNames", out _)); Assert.False(json.RootElement.TryGetProperty("GroupsSummary", out _));
    }

    [Fact]
    public async Task SaveFindAndImportPersistCanonicalExtrasWithoutChangingOtherMetadata()
    {
        AccountProfile saved;
        using (var repository = await OpenAsync())
        {
            saved = await repository.SaveAsync(AccountProfile.Create("Synthetic") with { Group = "Main", AdditionalGroups = "Raid\r\nCrew\nraid",
                Notes = "A metadata note", IsFavorite = true, PreferredPlaceId = 42, Priority = 17 }, default);
            Assert.Equal("Raid\nCrew", saved.AdditionalGroups);
            Assert.Equal(saved, await repository.FindAsync(saved.Id, default));
            await repository.CreateManyAsync([AccountProfile.Create("Imported") with { Group = "Other", AdditionalGroups = "Raid\nCrew" }], default);
        }
        using var reopened = await OpenAsync(); Assert.Equal(saved, await reopened.FindAsync(saved.Id, default));
        Assert.Equal(2, (await reopened.ReadAsync(new(Group: "Crew"), default)).Total);
        await using var db = await DatabaseAsync(); using var query = db.CreateCommand();
        query.CommandText = "SELECT additional_groups FROM profiles WHERE id=$id"; query.Parameters.AddWithValue("$id", saved.Id.ToString("D"));
        Assert.Equal("[\"Raid\",\"Crew\"]", await query.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("Raid")]
    [InlineData("%")]
    [InlineData("' OR 1=1 --")]
    [InlineData("Ωmega")]
    public async Task FilteringMatchesExactLiteralPrimaryOrAdditionalMembership(string group)
    {
        using var repository = await OpenAsync();
        await repository.CreateManyAsync([
            AccountProfile.Create("Primary match") with { Group = group },
            AccountProfile.Create("Additional match") with { Group = "Other", AdditionalGroups = group },
            AccountProfile.Create("Substring does not match") with { AdditionalGroups = group + " longer" },
            AccountProfile.Create("Other group")], default);
        var page = await repository.ReadAsync(new(Group: group.ToLowerInvariant()), default);
        Assert.Equal(2, page.Total); Assert.Equal(new[] { "Additional match", "Primary match" }, page.Items.Select(row => row.Label));
        Assert.All(page.Items, row => Assert.True(AccountGroups.Contains(row, group)));
        Assert.Equal(0, (await repository.ReadAsync(new(Search: "absent", Group: group), default)).Total);
    }

    [Fact]
    public async Task GroupListingUnionsPrimaryAndExtrasWithUnicodeDedupAndABoundedResult()
    {
        using var repository = await OpenAsync();
        await repository.CreateManyAsync([
            AccountProfile.Create("A") with { Group = "Équipe", AdditionalGroups = "Raid\nΩmega" },
            AccountProfile.Create("B") with { Group = "éQUIPE", AdditionalGroups = "raid\nωMEGA\nCrew" }], default);
        var names = await repository.ReadGroupsAsync(default);
        Assert.Equal(4, names.Count); Assert.Equal(4, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(names, value => StringComparer.OrdinalIgnoreCase.Equals("Équipe", value));
        await repository.CreateManyAsync(Enumerable.Range(0, 1100).Select(i => AccountProfile.Create("Extra " + i) with { Group = "Group " + i }).ToArray(), default);
        Assert.Equal(1000, (await repository.ReadGroupsAsync(default)).Count);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[1]")]
    [InlineData("[\"Crew\",\"crew\"]")]
    [InlineData("[\"General\"]")]
    [InlineData("[\" Crew\"]")]
    [InlineData("[\"bad\\nname\"]")]
    [InlineData("[\"Crew\"] trailing")]
    public async Task MalformedStoredMembershipFailsWithoutRepair(string corrupted)
    {
        using var repository = await OpenAsync(); var profile = await repository.SaveAsync(AccountProfile.Create("Synthetic"), default);
        await using var db = await DatabaseAsync(); using var command = db.CreateCommand();
        command.CommandText = "PRAGMA ignore_check_constraints=ON; UPDATE profiles SET additional_groups=$value WHERE id=$id";
        command.Parameters.AddWithValue("$value", corrupted); command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.FindAsync(profile.Id, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.ReadAsync(new(), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.ReadAsync(new(Group: "Crew"), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.ReadGroupsAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.ApplyGroupAsync([profile], "Crew", default));
        command.CommandText = "SELECT additional_groups FROM profiles WHERE id=$id";
        Assert.Equal(corrupted, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    public async Task SchemaConstraintsRejectMalformedJsonOnOrdinaryWrites(string invalid)
    {
        using var repository = await OpenAsync(); var profile = await repository.SaveAsync(AccountProfile.Create("Synthetic"), default);
        await using var db = await DatabaseAsync(); using var command = db.CreateCommand();
        command.CommandText = "UPDATE profiles SET additional_groups=$value WHERE id=$id";
        command.Parameters.AddWithValue("$value", invalid); command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(profile, await repository.FindAsync(profile.Id, default));
    }

    [Fact]
    public async Task VersionThreeMigrationVerifiesBackupAndPreservesProfileRevisionAndCleanup()
    {
        Directory.CreateDirectory(_root); Guid id = Guid.NewGuid(), cleanup = Guid.NewGuid(); var expected = AccountProfile.Create("Legacy") with { Id = id, Revision = 7, Group = "Legacy group", Notes = "Legacy note", IsFavorite = true };
        await using (var db = await DatabaseAsync())
        {
            using var command = db.CreateCommand(); command.CommandText = """
                CREATE TABLE profiles(id TEXT PRIMARY KEY,label TEXT NOT NULL,username TEXT NOT NULL,group_name TEXT NOT NULL,
                priority INTEGER NOT NULL,place_id INTEGER NULL,user_id INTEGER NULL,updated_utc TEXT NOT NULL,revision INTEGER NOT NULL,
                notes TEXT NOT NULL DEFAULT '',is_favorite INTEGER NOT NULL DEFAULT 0 CHECK(is_favorite IN(0,1)));
                CREATE INDEX profiles_group_label ON profiles(group_name,label COLLATE NOCASE);
                CREATE TABLE credential_cleanup(profile_id TEXT PRIMARY KEY);
                INSERT INTO profiles VALUES($id,$label,'',$group,0,NULL,NULL,$updated,7,$notes,1);
                INSERT INTO credential_cleanup VALUES($cleanup); PRAGMA user_version=3;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D")); command.Parameters.AddWithValue("$label", expected.Label);
            command.Parameters.AddWithValue("$group", expected.Group); command.Parameters.AddWithValue("$notes", expected.Notes);
            command.Parameters.AddWithValue("$updated", expected.UpdatedUtc.ToString("O")); command.Parameters.AddWithValue("$cleanup", cleanup.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
        using var repository = await OpenAsync(); Assert.Equal(expected, await repository.FindAsync(id, default));
        Assert.Equal(cleanup, Assert.Single((await repository.ReadCredentialCleanupAsync(default)).ProfileIds));
        await using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath + ".schema3.bak", Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await backup.OpenAsync(); using var verify = backup.CreateCommand(); verify.CommandText = "PRAGMA integrity_check"; Assert.Equal("ok", await verify.ExecuteScalarAsync());
        verify.CommandText = "PRAGMA user_version"; Assert.Equal(3L, await verify.ExecuteScalarAsync());
        verify.CommandText = "SELECT count(*) FROM pragma_table_info('profiles') WHERE name='additional_groups'"; Assert.Equal(0L, await verify.ExecuteScalarAsync());
        await using var current = await DatabaseAsync(); using var version = current.CreateCommand(); version.CommandText = "PRAGMA user_version"; Assert.Equal(5L, await version.ExecuteScalarAsync());
    }

    [Fact]
    public async Task FutureSchemaCannotBeDowngradedOrRewritten()
    {
        using (var repository = await OpenAsync()) await repository.SaveAsync(AccountProfile.Create("Preserved"), default);
        await using (var db = await DatabaseAsync()) { using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version=6"; await command.ExecuteNonQueryAsync(); }
        byte[] before = await File.ReadAllBytesAsync(DatabasePath);
        using var rejected = new AccountRepository(_root); await Assert.ThrowsAsync<InvalidDataException>(() => rejected.InitializeAsync(default));
        Assert.Equal(before, await File.ReadAllBytesAsync(DatabasePath)); Assert.False(File.Exists(DatabasePath + ".schema6.bak"));
    }

    [Fact]
    public async Task AtomicGroupAddPreservesPrimaryOrderAndOnlyChangesRowsNeedingMembership()
    {
        using var repository = await OpenAsync();
        var first = await repository.SaveAsync(AccountProfile.Create("A") with { Group = "Primary", AdditionalGroups = "Existing", Notes = "Preserve", IsFavorite = true, Priority = 17 }, default);
        var second = await repository.SaveAsync(AccountProfile.Create("B") with { Group = "CREW" }, default);
        Assert.Equal(1, await repository.ApplyGroupAsync([first, second], "Crew", default));
        var changed = (await repository.FindAsync(first.Id, default))!;
        Assert.Equal(first with { AdditionalGroups = "Existing\nCrew", Revision = first.Revision + 1, UpdatedUtc = changed.UpdatedUtc }, changed);
        Assert.Equal(second, await repository.FindAsync(second.Id, default));
        Assert.Equal(0, await repository.ApplyGroupAsync([changed, second], "crew", default));
        Assert.Equal(changed, await repository.FindAsync(first.Id, default)); Assert.Equal(second, await repository.FindAsync(second.Id, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStaleRevisionIncludingANoopRollsBackEverySelectedRow(bool staleIsNoop)
    {
        using var repository = await OpenAsync(); var first = await repository.SaveAsync(AccountProfile.Create("A"), default);
        var stale = await repository.SaveAsync(AccountProfile.Create("B") with { AdditionalGroups = staleIsNoop ? "Crew" : "" }, default);
        var newer = await repository.SaveAsync(stale with { Notes = "Newer" }, default);
        await Assert.ThrowsAsync<RevisionConflictException>(() => repository.ApplyGroupAsync([first, stale], "Crew", default));
        Assert.Equal(first, await repository.FindAsync(first.Id, default)); Assert.Equal(newer, await repository.FindAsync(stale.Id, default));
    }

    [Fact]
    public async Task MissingOrFullMembershipRowsPreventPartialChanges()
    {
        using var repository = await OpenAsync(); var first = await repository.SaveAsync(AccountProfile.Create("A"), default);
        var full = await repository.SaveAsync(AccountProfile.Create("Full") with { AdditionalGroups = string.Join('\n', Enumerable.Range(1, 31).Select(i => "Group " + i)) }, default);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ApplyGroupAsync([first, full], "Overflow", default));
        Assert.Equal(first, await repository.FindAsync(first.Id, default)); Assert.Equal(full, await repository.FindAsync(full.Id, default));
        await Assert.ThrowsAsync<RevisionConflictException>(() => repository.ApplyGroupAsync([first, full with { Id = Guid.NewGuid() }], "Crew", default));
        Assert.Equal(first, await repository.FindAsync(first.Id, default));
    }

    [Fact]
    public async Task DuplicateUnsavedAndOversizedSelectionsAreRejectedAndEmptyIsANoop()
    {
        using var repository = await OpenAsync(); var saved = await repository.SaveAsync(AccountProfile.Create("Synthetic"), default);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ApplyGroupAsync([saved, saved], "Crew", default));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ApplyGroupAsync([AccountProfile.Create("Unsaved")], "Crew", default));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ApplyGroupAsync(Enumerable.Range(0, 101).Select(i => saved with { Id = Guid.NewGuid() }).ToArray(), "Crew", default));
        Assert.Equal(0, await repository.ApplyGroupAsync([], "Crew", default)); Assert.Equal(saved, await repository.FindAsync(saved.Id, default));
    }

    [Fact]
    public async Task ADatabaseFailureAfterAnEarlierUpdateRollsBackTheWholeGroupEdit()
    {
        using var repository = await OpenAsync(); var first = await repository.SaveAsync(AccountProfile.Create("A"), default);
        var second = await repository.SaveAsync(AccountProfile.Create("B"), default);
        await using (var db = await DatabaseAsync())
        {
            using var trigger = db.CreateCommand(); trigger.CommandText = "CREATE TRIGGER synthetic_group_failure BEFORE UPDATE OF additional_groups ON profiles WHEN OLD.label='B' BEGIN SELECT RAISE(ABORT,'synthetic failure'); END";
            await trigger.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => repository.ApplyGroupAsync([first, second], "Crew", default));
        Assert.Equal(first, await repository.FindAsync(first.Id, default)); Assert.Equal(second, await repository.FindAsync(second.Id, default));
    }

    [Fact]
    public async Task CancelledGroupEditDoesNotChangeMembershipRevisionOrTime()
    {
        using var repository = await OpenAsync(); var saved = await repository.SaveAsync(AccountProfile.Create("Synthetic"), default);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ApplyGroupAsync([saved], "Crew", stop.Token));
        Assert.Equal(saved, await repository.FindAsync(saved.Id, default));
    }

    [Fact]
    public async Task CompetingRevisionBasedAddsHaveOneWinnerWithoutLostMembershipUpdates()
    {
        using var first = await OpenAsync(); using var second = await OpenAsync();
        var saved = await first.SaveAsync(AccountProfile.Create("Synthetic"), default);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var one = Task.Run(async () => { await gate.Task; return await Record.ExceptionAsync(() => first.ApplyGroupAsync([saved], "Crew", default)); });
        var two = Task.Run(async () => { await gate.Task; return await Record.ExceptionAsync(() => second.ApplyGroupAsync([saved], "Raid", default)); });
        gate.TrySetResult(); var results = await Task.WhenAll(one, two);
        Assert.Single(results, error => error is null); Assert.Single(results, error => error is RevisionConflictException);
        var current = (await first.FindAsync(saved.Id, default))!; Assert.Equal(2, current.Revision); Assert.Equal(2, current.GroupNames.Count);
        Assert.Contains(current.AdditionalGroups, new[] { "Crew", "Raid" });
    }

    [Fact]
    public async Task TenThousandProfilesRemainPagedByExactAdditionalMembership()
    {
        using var repository = await OpenAsync();
        await repository.CreateManyAsync(Enumerable.Range(0, 10000).Select(i => AccountProfile.Create($"Account {i:00000}") with
            { Group = "Primary", AdditionalGroups = i % 2 == 0 ? "Even\nShared" : "Odd\nShared" }).ToArray(), default);
        var page = await repository.ReadAsync(new(Group: "even", Page: 49), default);
        Assert.Equal(5000, page.Total); Assert.Equal(100, page.Items.Count); Assert.Equal("Account 09800", page.Items[0].Label);
        Assert.All(page.Items, row => Assert.True(AccountGroups.Contains(row, "Even")));
        var all = await repository.ReadAsync(new(Group: "Shared", Page: 99), default);
        Assert.Equal(10000, all.Total); Assert.Equal(100, all.Items.Count); Assert.Equal("Account 09900", all.Items[0].Label);
        Assert.Equal(new[] { "Even", "Odd", "Primary", "Shared" }, await repository.ReadGroupsAsync(default));
    }

    public void Dispose()
    {
        string absolute = Path.GetFullPath(_root), temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(Path.GetDirectoryName(absolute), temp, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(absolute).StartsWith("zenith-group-membership-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected synthetic library path.");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
    }
}
