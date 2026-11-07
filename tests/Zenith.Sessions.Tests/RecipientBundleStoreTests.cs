using System.IO;
using System.Text.Json;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class RecipientBundleStoreTests : IDisposable
{
    private readonly string _name = "zenith-recipient-bundles-" + Guid.NewGuid().ToString("N");
    private string Root => Path.Combine(Path.GetTempPath(), _name);
    private RecipientBundleStore Store() { Directory.CreateDirectory(Root); return new(Root); }
    private static RecipientBundle Draft(string name = "Friends") => RecipientBundle.Create(name, [Guid.NewGuid(), Guid.NewGuid()]);
    private static string Document(IEnumerable<RecipientBundle> bundles, long revision = 1) => JsonSerializer.Serialize(new
    {
        Version = 1, Revision = revision,
        Items = bundles.Select(item => new { item.Id, item.Name, item.ProfileIds, item.Revision, item.ChangeId }).ToArray()
    });
    private static RecipientBundle Stored(string name, IReadOnlyList<Guid>? ids = null) => new(Guid.NewGuid(), name,
        ids ?? [Guid.NewGuid()], 1, Guid.NewGuid());

    [Fact]
    public void NamesPreserveHumanPunctuationAndMemberListsAreDefensivelyFrozen()
    {
        var ids = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() }; Guid first = ids[0];
        var bundle = RecipientBundle.Create("  友達 👑 — Friday: A&B / (2)!  ", ids);
        ids[0] = Guid.NewGuid();
        Assert.Equal("友達 👑 — Friday: A&B / (2)!", bundle.Name); Assert.Equal(first, bundle.ProfileIds[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<Guid>)bundle.ProfileIds)[0] = Guid.NewGuid());
        Assert.DoesNotContain(bundle.Name, bundle.ToString());
        Assert.DoesNotContain(first.ToString(), bundle.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("name\nsecond")]
    [InlineData("name\u2028second")]
    [InlineData("name\0hidden")]
    [InlineData("https://www.roblox.com/games/123?accessCode=secret-marker")]
    [InlineData(".ROBLOSECURITY secret-marker")]
    [InlineData("cookie=secret-marker")]
    [InlineData("Bearer secret-marker")]
    [InlineData("privateServerLinkCode=secret-marker")]
    public void NamesCannotRetainRawLinksOrRecognizableCredentialPayloads(string name)
    {
        var error = Assert.Throws<ArgumentException>(() => RecipientBundle.Create(name, [Guid.NewGuid()]));
        Assert.DoesNotContain("secret-marker", error.ToString());
    }

    [Fact]
    public void EmptyDuplicateOversizedAndMalformedMembersAreExplicitlyRejected()
    {
        Guid id = Guid.NewGuid();
        foreach (var invalid in new RecipientBundle[]
        {
            new RecipientBundle(Guid.Empty, "Name", [id]), new(Guid.NewGuid(), "Name", []),
            new(Guid.NewGuid(), "Name", [Guid.Empty]), new(Guid.NewGuid(), "Name", [id, id]),
            new(Guid.NewGuid(), "Name", Enumerable.Range(0, RecipientBundle.MaximumRecipients + 1).Select(_ => Guid.NewGuid()).ToArray()),
            new(Guid.NewGuid(), new string('x', RecipientBundle.MaximumNameLength + 1), [id]),
            new(Guid.NewGuid(), "bad\ud800", [id]), new(Guid.NewGuid(), "Name", [id], -1),
            new(Guid.NewGuid(), "Name", [id], 1), new(Guid.NewGuid(), "Name", [id], ChangeId: Guid.NewGuid())
        }) Assert.Throws<ArgumentException>(() => invalid.Validated());
    }

    [Fact]
    public void MissingProfileReferencesAreReportedInOrderAndNeverBecomeAuthorization()
    {
        Guid first = Guid.NewGuid(), missing = Guid.NewGuid(), last = Guid.NewGuid();
        var bundle = RecipientBundle.Create("Ordered friends", [first, missing, last]);
        var resolution = bundle.ResolveRecipients(new HashSet<Guid> { last, first, Guid.NewGuid() });
        Assert.Equal(new[] { first, last }, resolution.AvailableProfileIds); Assert.Equal(new[] { missing }, resolution.MissingProfileIds);
        Assert.True(resolution.RequiresReview); Assert.False(resolution.AuthorizesLaunch);
        Assert.Equal(new[] { first, missing, last }, bundle.ProfileIds);
        Assert.Contains("do not establish sign-in", resolution.Summary);
        Assert.False(bundle.ResolveRecipients(new HashSet<Guid>(bundle.ProfileIds)).RequiresReview);
        Assert.Equal(3, bundle.ResolveRecipients(new HashSet<Guid>()).MissingProfileIds.Count);
    }

    [Fact]
    public async Task NewLibraryAndSnapshotsAreVersionedBoundedAndImmutable()
    {
        using var store = Store(); var initial = await store.LoadAsync(default);
        Assert.Equal(1, initial.Revision); Assert.Empty(initial.Bundles); Assert.Null(initial.Notice);
        Assert.Throws<NotSupportedException>(() => ((IList<RecipientBundle>)initial.Bundles).Add(Draft()));
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(store.Path));
        Assert.Equal(new[] { "Items", "Revision", "Version" }, json.RootElement.EnumerateObject().Select(item => item.Name).Order());
        Assert.Equal(1, json.RootElement.GetProperty("Version").GetInt32()); Assert.False(File.Exists(store.Path + ".bak"));
        Assert.InRange(new FileInfo(store.Path).Length, 1, RecipientBundleStore.MaximumFileBytes);
    }

    [Fact]
    public async Task SaveRenameEditDeleteAndReopenKeepIdentityAndExplicitRevisionConflicts()
    {
        using var store = Store(); var first = await store.SaveAsync(Draft(), default);
        var renamed = await store.RenameAsync(first.Id, first.Revision, first.ChangeId, "Renamed recipients", default);
        Assert.Equal(first.Id, renamed.Id); Assert.Equal<Guid>(first.ProfileIds, renamed.ProfileIds);
        Assert.Equal(first.Revision + 1, renamed.Revision); Assert.NotEqual(first.ChangeId, renamed.ChangeId);
        await Assert.ThrowsAsync<RecipientBundleConflictException>(() => store.RenameAsync(first.Id, first.Revision, first.ChangeId, "Stale", default));
        await Assert.ThrowsAsync<RecipientBundleConflictException>(() => store.DeleteAsync(first.Id, first.Revision, first.ChangeId, default));
        var edited = await store.SaveAsync(renamed with { ProfileIds = [first.ProfileIds[1], Guid.NewGuid()] }, default);
        using (var reopened = new RecipientBundleStore(Root)) EqualBundle(edited, Assert.Single((await reopened.LoadAsync(default)).Bundles));
        await store.DeleteAsync(edited.Id, edited.Revision, edited.ChangeId, default);
        Assert.Empty((await store.LoadAsync(default)).Bundles);
        await Assert.ThrowsAsync<RecipientBundleConflictException>(() => store.SaveAsync(edited, default));
        var reused = await store.SaveAsync(new(edited.Id, "Deliberately recreated", edited.ProfileIds), default);
        Assert.NotEqual(first.ChangeId, reused.ChangeId);
        await Assert.ThrowsAsync<RecipientBundleConflictException>(() => store.SaveAsync(first with { Name = "Old stale draft" }, default));
    }

    [Fact]
    public async Task DurableSchemaContainsOnlyNamesGuidReferencesAndMetadataRevisions()
    {
        using var store = Store(); var draft = Draft(); await store.SaveAsync(draft, default);
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(store.Path));
        var item = Assert.Single(json.RootElement.GetProperty("Items").EnumerateArray());
        Assert.Equal(new[] { "ChangeId", "Id", "Name", "ProfileIds", "Revision" }, item.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(draft.ProfileIds, item.GetProperty("ProfileIds").EnumerateArray().Select(value => value.GetGuid()));
        foreach (string path in Directory.GetFiles(Root))
        {
            string contents = await File.ReadAllTextAsync(path);
            foreach (string forbidden in new[] { "Cookie", "Token", "VerifiedUserId", "PlaceId", "JobId", "ObservedUtc", "Running" }) Assert.DoesNotContain(forbidden, contents);
        }
    }

    [Fact]
    public async Task IndependentInstancesSerializeAddsAndOnlyOneStaleEditCanWin()
    {
        using var first = Store(); using var second = new RecipientBundleStore(Root);
        var saved = await Task.WhenAll(first.SaveAsync(Draft("A"), default), second.SaveAsync(Draft("B"), default));
        Assert.Equal(2, (await first.LoadAsync(default)).Bundles.Count);
        Exception?[] results = await Task.WhenAll(
            Xunit.Record.ExceptionAsync(() => first.RenameAsync(saved[0].Id, saved[0].Revision, saved[0].ChangeId, "Changed A", default)),
            Xunit.Record.ExceptionAsync(() => second.RenameAsync(saved[0].Id, saved[0].Revision, saved[0].ChangeId, "Changed B", default)));
        Assert.Single(results, error => error is null); Assert.Single(results, error => error is RecipientBundleConflictException);
        Assert.Equal(2, (await second.LoadAsync(default)).Bundles.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json secret-marker")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"Version\":1}")]
    [InlineData("{\"Version\":1,\"Version\":1,\"Revision\":1,\"Items\":[]}")]
    [InlineData("{\"Version\":1,\"Revision\":1,\"Items\":[],\"Cookie\":\"secret-marker\"}")]
    [InlineData("{\"Version\":1,\"Revision\":1,\"Items\":[]} trailing")]
    public async Task CorruptionNeverInventsAnEmptyLibraryOrDiscardsTheOriginal(string contents)
    {
        using var store = Store(); await File.WriteAllTextAsync(store.Path, contents);
        var error = await Assert.ThrowsAsync<RecipientBundleStorageException>(() => store.LoadAsync(default));
        Assert.Equal(contents == "{\"Version\":1,\"Version\":1,\"Revision\":1,\"Items\":[]}"
            ? RecipientBundleStorageProblem.UnsupportedVersion : RecipientBundleStorageProblem.Damaged, error.Problem); Assert.DoesNotContain("secret-marker", error.ToString());
        Assert.Equal(contents, await File.ReadAllTextAsync(store.Path)); Assert.False(File.Exists(store.Path + ".bak"));
    }

    [Fact]
    public async Task MalformedAndDuplicateRecordsAreRejectedWithoutPartialLoad()
    {
        using var store = Store(); var item = Stored("Name");
        foreach (string contents in new[]
        {
            Document([item, item]), Document([item with { ProfileIds = [Guid.Empty] }]),
            Document([item with { ProfileIds = [item.ProfileIds[0], item.ProfileIds[0]] }]),
            Document([item with { Revision = 2 }]), Document([item with { ChangeId = Guid.Empty }]),
            Document([item]).Replace("\"ProfileIds\":", "\"ProfileIds\":[],\"ProfileIds\":", StringComparison.Ordinal),
            Document([item]).Replace("\"Name\":\"Name\"", "\"Name\":\"bad\\uD800\"", StringComparison.Ordinal)
        })
        {
            await File.WriteAllTextAsync(store.Path, contents);
            await Assert.ThrowsAsync<RecipientBundleStorageException>(() => store.LoadAsync(default));
            Assert.Equal(contents, await File.ReadAllTextAsync(store.Path));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task UnsupportedVersionNeverDowngradesToTheValidBackup(int version)
    {
        using var store = Store(); var saved = await store.SaveAsync(Draft(), default);
        byte[] backup = await File.ReadAllBytesAsync(store.Path + ".bak");
        string future = $"{{\"Version\":{version},\"Revision\":100,\"Items\":[],\"FutureField\":true}}";
        await File.WriteAllTextAsync(store.Path, future);
        var error = await Assert.ThrowsAsync<RecipientBundleStorageException>(() => store.LoadAsync(default));
        Assert.Equal(RecipientBundleStorageProblem.UnsupportedVersion, error.Problem);
        await Assert.ThrowsAsync<RecipientBundleStorageException>(() => store.SaveAsync(saved, default));
        Assert.Equal(future, await File.ReadAllTextAsync(store.Path)); Assert.Equal(backup, await File.ReadAllBytesAsync(store.Path + ".bak"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LastValidRecoveryIsExplicitAndRepairPreservesTheDamagedOriginal(bool emptyOriginal)
    {
        using var store = Store(); var first = await store.SaveAsync(Draft("Original"), default);
        var second = await store.RenameAsync(first.Id, first.Revision, first.ChangeId, "Second revision", default);
        byte[] backup = await File.ReadAllBytesAsync(store.Path + ".bak"); string damaged = emptyOriginal ? "" : "truncated secret-marker";
        await File.WriteAllTextAsync(store.Path, damaged);
        var recovered = await store.LoadAsync(default); Assert.NotNull(recovered.Notice); EqualBundle(first, Assert.Single(recovered.Bundles));
        Assert.Equal(damaged, await File.ReadAllTextAsync(store.Path));
        var repaired = await store.RenameAsync(first.Id, first.Revision, first.ChangeId, "Deliberate repair", default);
        Assert.NotEqual(second.ChangeId, repaired.ChangeId); Assert.Equal(damaged, await File.ReadAllTextAsync(store.Path + ".damaged"));
        Assert.Equal(backup, await File.ReadAllBytesAsync(store.Path + ".bak"));
        await Assert.ThrowsAsync<RecipientBundleConflictException>(() => store.SaveAsync(second, default));
        await File.WriteAllTextAsync(store.Path, "another damaged original");
        var blocked = await Assert.ThrowsAsync<RecipientBundleStorageException>(() => store.RenameAsync(first.Id, first.Revision, first.ChangeId, "Cannot discard old damage", default));
        Assert.Equal(RecipientBundleStorageProblem.RecoveryBlocked, blocked.Problem);
        Assert.Equal("another damaged original", await File.ReadAllTextAsync(store.Path));
        Assert.Equal(damaged, await File.ReadAllTextAsync(store.Path + ".damaged"));
    }

    [Fact]
    public async Task InterruptedTemporaryDataIsNeverAppliedAndOversizedOriginalsArePreserved()
    {
        using var store = Store(); await File.WriteAllTextAsync(store.Path + ".tmp", "unfinished");
        await Assert.ThrowsAsync<RecipientBundleStorageException>(() => store.LoadAsync(default)); Assert.False(File.Exists(store.Path));
        File.Delete(store.Path + ".tmp"); var saved = await store.SaveAsync(Draft(), default);
        await File.WriteAllTextAsync(store.Path + ".tmp", "uncommitted secret-marker");
        var loaded = await store.LoadAsync(default); EqualBundle(saved, Assert.Single(loaded.Bundles)); Assert.NotNull(loaded.Notice);
        File.Delete(store.Path + ".bak"); await File.WriteAllBytesAsync(store.Path, new byte[RecipientBundleStore.MaximumFileBytes + 1]);
        await Assert.ThrowsAsync<RecipientBundleStorageException>(() => store.LoadAsync(default));
        Assert.Equal(RecipientBundleStore.MaximumFileBytes + 1, new FileInfo(store.Path).Length);
    }

    [Fact]
    public async Task MissingMainUsesOnlyTheKnownBackupAndNeverReplacesAnUnreadableRecoveryCopy()
    {
        using var store = Store(); var first = await store.SaveAsync(Draft("Known backup"), default);
        await store.RenameAsync(first.Id, first.Revision, first.ChangeId, "Later main", default);
        File.Delete(store.Path);
        var recovered = await store.LoadAsync(default); Assert.NotNull(recovered.Notice);
        EqualBundle(first, Assert.Single(recovered.Bundles)); Assert.False(File.Exists(store.Path));
        const string damagedBackup = "unreadable backup secret-marker";
        await File.WriteAllTextAsync(store.Path + ".bak", damagedBackup);
        await Assert.ThrowsAsync<RecipientBundleStorageException>(() => store.LoadAsync(default));
        Assert.False(File.Exists(store.Path)); Assert.Equal(damagedBackup, await File.ReadAllTextAsync(store.Path + ".bak"));
    }

    [Fact]
    public async Task PreCancelledAdmissionAndDuplicateRecipientsDoNotCreateStorage()
    {
        using var store = new RecipientBundleStore(Root); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(stop.Token));
        Guid id = Guid.NewGuid();
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new(Guid.NewGuid(), "Duplicates", [id, id]), default));
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public async Task CountAndAggregateReferenceLimitsAreOperationalAndExistingBundlesRemainEditable()
    {
        using var store = Store(); var entries = Enumerable.Range(0, RecipientBundle.MaximumCount).Select(index =>
            Stored("Bundle " + index, Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToArray())).ToArray();
        await File.WriteAllTextAsync(store.Path, Document(entries));
        await Assert.ThrowsAsync<RecipientBundleCapacityException>(() => store.SaveAsync(Draft("One too many"), default));
        await Assert.ThrowsAsync<RecipientBundleCapacityException>(() => store.SaveAsync(entries[0] with { ProfileIds = entries[0].ProfileIds.Append(Guid.NewGuid()).ToArray() }, default));
        var renamed = await store.RenameAsync(entries[0].Id, entries[0].Revision, entries[0].ChangeId, "Still editable", default);
        await store.DeleteAsync(renamed.Id, renamed.Revision, renamed.ChangeId, default);
        await store.SaveAsync(Draft("Replacement"), default);
        Assert.Equal(RecipientBundle.MaximumCount, (await store.LoadAsync(default)).Bundles.Count);
    }

    [Fact]
    public async Task CancelledBlockedReplacementPreservesTheCommittedMainAndBackup()
    {
        using var store = Store(); var saved = await store.SaveAsync(Draft(), default);
        byte[] main = await File.ReadAllBytesAsync(store.Path), backup = await File.ReadAllBytesAsync(store.Path + ".bak");
        using (var held = new FileStream(store.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RenameAsync(saved.Id, saved.Revision, saved.ChangeId, "Not accepted", stop.Token));
        }
        Assert.Equal(main, await File.ReadAllBytesAsync(store.Path)); Assert.Equal(backup, await File.ReadAllBytesAsync(store.Path + ".bak"));
        EqualBundle(saved, Assert.Single((await store.LoadAsync(default)).Bundles));
        var retry = await store.RenameAsync(saved.Id, saved.Revision, saved.ChangeId, "Accepted after retry", default);
        Assert.Equal(2, retry.Revision); Assert.False(File.Exists(store.Path + ".tmp"));
    }

    [Fact]
    public async Task AdmissionAndDisposalRemainBoundedWhileAnotherOwnerHoldsTheLock()
    {
        using var store = Store(); await store.LoadAsync(default);
        using var held = new FileStream(store.Path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var stop = new CancellationTokenSource();
        var work = Enumerable.Range(0, RecipientBundleStore.MaximumPendingOperations).Select(_ => store.LoadAsync(stop.Token)).ToArray();
        try
        {
            Assert.Throws<RecipientBundleBusyException>(() => { _ = store.LoadAsync(default); });
            Assert.Throws<InvalidOperationException>(() => store.Dispose());
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(3)));
        }
        store.Dispose(); store.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = store.LoadAsync(default); });
    }

    private static void EqualBundle(RecipientBundle expected, RecipientBundle actual)
    {
        Assert.Equal(expected.Id, actual.Id); Assert.Equal(expected.Name, actual.Name); Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.ChangeId, actual.ChangeId); Assert.Equal<Guid>(expected.ProfileIds, actual.ProfileIds);
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(Root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || Path.GetFileName(full) != _name)
            throw new InvalidOperationException("Unexpected owned recipient bundle fixture path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
