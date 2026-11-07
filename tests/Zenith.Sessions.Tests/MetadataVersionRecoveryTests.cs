using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Newly owned metadata-only Temp folders. No credentials, account stores, queues, browser,
// native clients or existing user files are read or changed.
public sealed class MetadataVersionRecoveryTests : IDisposable
{
    private readonly string _name = "zenith-metadata-version-" + Guid.NewGuid().ToString("N");
    private string Root => Path.Combine(Path.GetTempPath(), _name);
    private const string Marker = "synthetic-secret-marker";
    public static IEnumerable<object[]> UnsupportedDocuments()
    {
        string[] documents =
        [
            """{"Version":99,"Version":1,"Revision":99,"Items":[]}""",
            """{"Version":1,"Version":99,"Revision":99,"Items":[]}""",
            """{"Version":1,"Version":1,"Revision":99,"Items":[]}""",
            """{"Version":"future","Version":1,"Revision":99,"Items":[]}""",
            """{"Version":1,"Version":null,"Revision":99,"Items":[]}""",
            """{"Version":1,"\u0056ersion":1,"Revision":99,"Items":[]}""",
            """{"Version":99.0,"Revision":99,"Items":[]}""",
            """{"Version":1.0,"Revision":99,"Items":[]}""",
            """{"Version":1e0,"Revision":99,"Items":[]}""",
            """{"Version":9223372036854775808,"Revision":99,"Items":[]}""",
            """{"Version":99999999999999999999999999999999999999999999,"Revision":99,"Items":[]}""",
            """{"Version":-1,"Revision":99,"Items":[]}""",
            """{"Version":0,"Revision":99,"Items":[]}""",
            """{"Version":99,"Revision":""",
            """{"Version":1,"Version":""",
            """{"Version":1,"Revision":0,"Items":null,"Version":99}""",
            """{"Version":null,"Items":[],"Version":1,"Revision":1}""",
            """{"\u0056ersion":99,"Version":1,"Revision":99,"Items":[]}""",
            "{\"Extra\":" + new string('[', 32) + "0" + new string(']', 32) + ",\"Version\":99,\"Revision\":99,\"Items\":[]}"
        ];
        foreach (bool bundles in new[] { false, true }) foreach (string document in documents) yield return [bundles, document];
    }

    [Theory]
    [MemberData(nameof(UnsupportedDocuments))]
    public async Task IncompatibleOrDuplicateRootVersionsPreservePrimaryAndPopulatedBackupOnLoadAndSave(bool bundles, string document)
    {
        using var library = await Library.Open(Root, bundles); byte[] backup = await File.ReadAllBytesAsync(library.Path + ".bak");
        Assert.Single((await library.Load()).Ids); Assert.NotEmpty(backup);
        using (var json = JsonDocument.Parse(backup)) Assert.Equal(library.BackupId,
            Assert.Single(json.RootElement.GetProperty("Items").EnumerateArray()).GetProperty("Id").GetGuid());
        await File.WriteAllTextAsync(library.Path, document); byte[] primary = await File.ReadAllBytesAsync(library.Path);
        AssertProblem(await Record.ExceptionAsync(async () => { await library.Load(); }), bundles, unsupported: true);
        Assert.Equal(primary, await File.ReadAllBytesAsync(library.Path)); Assert.Equal(backup, await File.ReadAllBytesAsync(library.Path + ".bak"));
        AssertProblem(await Record.ExceptionAsync(library.SaveBackup), bundles, unsupported: true);
        Assert.Equal(primary, await File.ReadAllBytesAsync(library.Path)); Assert.Equal(backup, await File.ReadAllBytesAsync(library.Path + ".bak"));
        Assert.False(File.Exists(library.Path + ".damaged")); Assert.False(File.Exists(library.Path + ".tmp"));
    }

    public static IEnumerable<object[]> MalformedSingleVersions()
    {
        foreach (bool bundles in new[] { false, true })
            foreach (string value in new[] { "\"1\"", "true", "null", "{}", "[]" })
                yield return [bundles, "{\"Version\":" + value + ",\"Revision\":99,\"Items\":[]}"];
    }
    [Theory]
    [MemberData(nameof(MalformedSingleVersions))]
    public async Task MalformedSingleNonnumericVersionRetainsOrdinaryCorruptionRecovery(bool bundles, string document)
    {
        using var library = await Library.Open(Root, bundles); await RecoverKnownBackup(library, document);
        using var withoutBackup = await Library.Open(Path.Combine(Root, "without-backup"), bundles);
        File.Delete(withoutBackup.Path + ".bak"); await File.WriteAllTextAsync(withoutBackup.Path, document);
        byte[] before = await File.ReadAllBytesAsync(withoutBackup.Path);
        AssertProblem(await Record.ExceptionAsync(async () => { await withoutBackup.Load(); }), bundles, unsupported: false);
        Assert.Equal(before, await File.ReadAllBytesAsync(withoutBackup.Path)); Assert.False(File.Exists(withoutBackup.Path + ".bak"));
    }

    public static IEnumerable<object[]> NestedVersionDocuments()
    {
        string[] documents =
        [
            """{"Version":1,"Revision":99,"Items":[],"Extra":{"Version":99}}""",
            """{"Version":1,"Revision":99,"Items":[],"Extra":{"Version":99,"Version":1}}""",
            """{"Version":1,"Revision":99,"Items":[{"Version":99}]}""",
            """{"Version":1,"Revision":99,"Items":[],"Extra":"\"Version\":99"}""",
            "{\"Version\":1,\"Revision\":99,\"Items\":[],\"Extra\":" + new string('[', 32) + "{\"Version\":99}" + new string(']', 32) + "}"
        ];
        foreach (bool bundles in new[] { false, true }) foreach (string document in documents) yield return [bundles, document];
    }
    [Theory]
    [MemberData(nameof(NestedVersionDocuments))]
    public async Task NestedVersionAndQuotedTextAreNotRootFormatAuthority(bool bundles, string document)
    {
        using var library = await Library.Open(Root, bundles); await RecoverKnownBackup(library, document);
    }

    public static IEnumerable<object[]> CurrentDamagedDocuments()
    {
        string[] documents =
        [
            """{"Version":1,"Revision":0,"Items":[]}""",
            """{"Version":1,"Revision":99,"Items":null}""",
            """{"Version":1,"Revision":99,"Items":[{}]}"""
        ];
        foreach (bool bundles in new[] { false, true }) foreach (string document in documents) yield return [bundles, document];
    }
    [Theory]
    [MemberData(nameof(CurrentDamagedDocuments))]
    public async Task CurrentVersionWithBadRecordsStillLoadsAndDeliberatelyRepairsTheKnownBackup(bool bundles, string document)
    {
        using var library = await Library.Open(Root, bundles); await RecoverKnownBackup(library, document);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnsupportedFallbackBackupCannotBeTreatedAsDamagedOrRewritten(bool bundles, bool missingPrimary)
    {
        using var library = await Library.Open(Root, bundles);
        if (missingPrimary) File.Delete(library.Path); else await File.WriteAllTextAsync(library.Path, "truncated " + Marker);
        const string future = """{"Version":99,"Version":1,"Revision":99,"Items":[]}""";
        await File.WriteAllTextAsync(library.Path + ".bak", future); byte[] backup = await File.ReadAllBytesAsync(library.Path + ".bak");
        byte[]? primary = missingPrimary ? null : await File.ReadAllBytesAsync(library.Path);
        AssertProblem(await Record.ExceptionAsync(async () => { await library.Load(); }), bundles, unsupported: true);
        AssertProblem(await Record.ExceptionAsync(library.SaveBackup), bundles, unsupported: true);
        Assert.Equal(backup, await File.ReadAllBytesAsync(library.Path + ".bak")); Assert.Equal(!missingPrimary, File.Exists(library.Path));
        if (primary is not null) Assert.Equal(primary, await File.ReadAllBytesAsync(library.Path));
        Assert.False(File.Exists(library.Path + ".damaged")); Assert.False(File.Exists(library.Path + ".tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingPrimaryUsesAPopulatedKnownBackupWithoutInventingOrReplacingItsMembers(bool bundles)
    {
        using var library = await Library.Open(Root, bundles); byte[] backup = await File.ReadAllBytesAsync(library.Path + ".bak"); File.Delete(library.Path);
        var recovered = await library.Load(); Assert.NotNull(recovered.Notice); Assert.Equal(library.BackupId, Assert.Single(recovered.Ids));
        await library.AssertBackupContents();
        Assert.False(File.Exists(library.Path)); Assert.Equal(backup, await File.ReadAllBytesAsync(library.Path + ".bak"));
        await library.SaveBackup(); var repaired = await library.Load(); Assert.Equal(library.BackupId, Assert.Single(repaired.Ids)); Assert.Null(repaired.Notice);
        await library.AssertBackupContents(repaired: true);
        Assert.Equal(backup, await File.ReadAllBytesAsync(library.Path + ".bak")); Assert.False(File.Exists(library.Path + ".damaged"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALongNumericVersionWithinTheExistingReadBoundCannotFallBackOverFutureData(bool bundles)
    {
        using var library = await Library.Open(Root, bundles);
        string document = "{\"Version\":" + new string('9', FavoriteDestinationsStore.MaximumFileBytes - 100) + ",\"Revision\":99,\"Items\":[]}";
        byte[] bytes = Encoding.UTF8.GetBytes(document); Assert.InRange(bytes.Length, 1, FavoriteDestinationsStore.MaximumFileBytes);
        Assert.Equal(FavoriteDestinationsStore.MaximumFileBytes, RecipientBundleStore.MaximumFileBytes);
        byte[] backup = await File.ReadAllBytesAsync(library.Path + ".bak"); await File.WriteAllBytesAsync(library.Path, bytes);
        AssertProblem(await Record.ExceptionAsync(async () => { await library.Load(); }), bundles, unsupported: true);
        AssertProblem(await Record.ExceptionAsync(library.SaveBackup), bundles, unsupported: true);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(library.Path)); Assert.Equal(backup, await File.ReadAllBytesAsync(library.Path + ".bak"));
        Assert.False(File.Exists(library.Path + ".damaged")); Assert.False(File.Exists(library.Path + ".tmp"));
    }

    public static IEnumerable<object[]> UnusedUnsupportedBackups()
    {
        string[] versions = ["\"Version\":99", "\"Version\":99,\"Version\":1", "\"Version\":1,\"Version\":99", "\"Version\":1,\"Version\":1"];
        foreach (bool bundles in new[] { false, true })
            foreach (string mutation in bundles ? new[] { "save", "delete", "rename" } : new[] { "save", "delete" })
                foreach (string version in versions) yield return [bundles, mutation, "{" + version + ",\"Revision\":99,\"Items\":[]}"];
    }
    [Theory]
    [MemberData(nameof(UnusedUnsupportedBackups))]
    public async Task ValidPrimaryLoadsButEveryMutationPreservesAnUnsupportedUnusedBackup(bool bundles, string mutation, string document)
    {
        using var library = await Library.Open(Root, bundles); var current = await library.Load();
        byte[] primary = await File.ReadAllBytesAsync(library.Path); await File.WriteAllTextAsync(library.Path + ".bak", document);
        byte[] backup = await File.ReadAllBytesAsync(library.Path + ".bak"); var loaded = await library.Load();
        Assert.Equal(current.Revision, loaded.Revision); Assert.Equal(current.Ids, loaded.Ids); Assert.Null(loaded.Notice);
        AssertProblem(await Record.ExceptionAsync(() => library.MutateCurrent(mutation)), bundles, unsupported: true);
        Assert.Equal(primary, await File.ReadAllBytesAsync(library.Path)); Assert.Equal(backup, await File.ReadAllBytesAsync(library.Path + ".bak"));
        Assert.False(File.Exists(library.Path + ".damaged")); Assert.False(File.Exists(library.Path + ".tmp"));
    }

    public static IEnumerable<object[]> CurrentMalformedBackups()
    {
        string[] documents = ["""{"Version":1,"Revision":0,"Items":null}""", """{"Version":1,"Revision":""", """{"Version":1,"Revision":99,"Items":[{}]}"""];
        foreach (bool bundles in new[] { false, true }) foreach (string document in documents) yield return [bundles, document];
    }
    [Theory]
    [MemberData(nameof(CurrentMalformedBackups))]
    public async Task KnownCurrentDamagedUnusedBackupCanBeReplacedByTheAcceptedPrimary(bool bundles, string document)
    {
        using var library = await Library.Open(Root, bundles); byte[] primary = await File.ReadAllBytesAsync(library.Path);
        await File.WriteAllTextAsync(library.Path + ".bak", document); Assert.Null((await library.Load()).Notice);
        await library.MutateCurrent("save"); Assert.Single((await library.Load()).Ids);
        Assert.Equal(primary, await File.ReadAllBytesAsync(library.Path + ".bak"));
        Assert.NotEqual(primary, await File.ReadAllBytesAsync(library.Path)); Assert.False(File.Exists(library.Path + ".damaged")); Assert.False(File.Exists(library.Path + ".tmp"));
    }

    public static IEnumerable<object[]> UnknownBackups()
    {
        foreach (bool bundles in new[] { false, true }) foreach (string document in new[] { "not-json " + Marker, "{\"Revision\":99,\"Items\":[]}", "{\"Version\":null,\"Revision\":99,\"Items\":[]}" })
            yield return [bundles, document];
    }
    [Theory]
    [MemberData(nameof(UnknownBackups))]
    public async Task UnknownUnusedBackupIsNotAssumedToBeCurrentFormatForReplacement(bool bundles, string document)
    {
        using var library = await Library.Open(Root, bundles); byte[] primary = await File.ReadAllBytesAsync(library.Path);
        await File.WriteAllTextAsync(library.Path + ".bak", document); byte[] backup = await File.ReadAllBytesAsync(library.Path + ".bak");
        Assert.Null((await library.Load()).Notice); AssertProblem(await Record.ExceptionAsync(() => library.MutateCurrent("save")), bundles, unsupported: false);
        Assert.Equal(primary, await File.ReadAllBytesAsync(library.Path)); Assert.Equal(backup, await File.ReadAllBytesAsync(library.Path + ".bak"));
        Assert.False(File.Exists(library.Path + ".damaged")); Assert.False(File.Exists(library.Path + ".tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnreadOversizedBackupIsNeverAssumedSafeToOverwrite(bool bundles)
    {
        using var library = await Library.Open(Root, bundles); byte[] primary = await File.ReadAllBytesAsync(library.Path);
        byte[] backup = Encoding.UTF8.GetBytes("{\"Version\":99,\"Extra\":\"" + new string('x', FavoriteDestinationsStore.MaximumFileBytes) + "\"}");
        await File.WriteAllBytesAsync(library.Path + ".bak", backup); Assert.Null((await library.Load()).Notice);
        AssertProblem(await Record.ExceptionAsync(() => library.MutateCurrent("save")), bundles, unsupported: false);
        Assert.Equal(primary, await File.ReadAllBytesAsync(library.Path)); Assert.Equal(backup, await File.ReadAllBytesAsync(library.Path + ".bak"));
        Assert.False(File.Exists(library.Path + ".damaged")); Assert.False(File.Exists(library.Path + ".tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnInaccessibleUnusedBackupPropagatesTheFailureBeforeAnyWrite(bool bundles)
    {
        using var library = await Library.Open(Root, bundles); byte[] primary = await File.ReadAllBytesAsync(library.Path), backup = await File.ReadAllBytesAsync(library.Path + ".bak");
        await using (var held = new FileStream(library.Path + ".bak", FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null((await library.Load()).Notice);
            var error = Assert.IsType<IOException>(await Record.ExceptionAsync(() => library.MutateCurrent("save")));
            Assert.Contains(error.HResult & 0xffff, new[] { 32, 33 });
            Assert.Equal(primary, await File.ReadAllBytesAsync(library.Path)); Assert.False(File.Exists(library.Path + ".damaged")); Assert.False(File.Exists(library.Path + ".tmp"));
        }
        Assert.Equal(backup, await File.ReadAllBytesAsync(library.Path + ".bak"));
    }

    private static async Task RecoverKnownBackup(Library library, string document)
    {
        byte[] backup = await File.ReadAllBytesAsync(library.Path + ".bak"); await File.WriteAllTextAsync(library.Path, document);
        byte[] primary = await File.ReadAllBytesAsync(library.Path); var recovered = await library.Load();
        Assert.NotNull(recovered.Notice); Assert.Equal(library.BackupId, Assert.Single(recovered.Ids));
        await library.AssertBackupContents();
        Assert.Equal(primary, await File.ReadAllBytesAsync(library.Path)); Assert.Equal(backup, await File.ReadAllBytesAsync(library.Path + ".bak"));
        await library.SaveBackup(); Assert.Equal(library.BackupId, Assert.Single((await library.Load()).Ids));
        await library.AssertBackupContents(repaired: true);
        Assert.Equal(primary, await File.ReadAllBytesAsync(library.Path + ".damaged")); Assert.Equal(backup, await File.ReadAllBytesAsync(library.Path + ".bak"));
    }
    private static void AssertProblem(Exception? error, bool bundles, bool unsupported)
    {
        if (bundles) Assert.Equal(unsupported ? RecipientBundleStorageProblem.UnsupportedVersion : RecipientBundleStorageProblem.Damaged,
            Assert.IsType<RecipientBundleStorageException>(error).Problem);
        else Assert.Equal(unsupported ? FavoriteDestinationStorageProblem.UnsupportedVersion : FavoriteDestinationStorageProblem.Damaged,
            Assert.IsType<FavoriteDestinationStorageException>(error).Problem);
        Assert.DoesNotContain(Marker, error!.ToString());
    }
    private sealed class Library : IDisposable
    {
        private FavoriteDestinationsStore? _favorites; private RecipientBundleStore? _bundles;
        private FavoriteDestination? _favoriteBackup; private RecipientBundle? _bundleBackup;
        private FavoriteDestination? _favoriteCurrent; private RecipientBundle? _bundleCurrent;
        public string Path => _favorites?.Path ?? _bundles!.Path;
        public Guid BackupId => _favoriteBackup?.Id ?? _bundleBackup!.Id;
        public static async Task<Library> Open(string root, bool bundles)
        {
            Directory.CreateDirectory(root); var library = new Library();
            try
            {
                if (bundles)
                {
                    library._bundles = new(root);
                    var original = await library._bundles.SaveAsync(new(Guid.NewGuid(), "Known bundle", new[] { Guid.NewGuid(), Guid.NewGuid() }), default);
                    library._bundleBackup = original;
                    library._bundleCurrent = await library._bundles.SaveAsync(original with { Name = "Current bundle" }, default);
                }
                else
                {
                    library._favorites = new(root);
                    var original = await library._favorites.SaveAsync(new(Guid.NewGuid(), "Known favorite", long.MaxValue,
                        "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"), default);
                    library._favoriteBackup = original;
                    library._favoriteCurrent = await library._favorites.SaveAsync(original with { Name = "Current favorite" }, default);
                }
                return library;
            }
            catch { library.Dispose(); throw; }
        }
        public async Task<(long Revision, Guid[] Ids, string? Notice)> Load()
        {
            if (_favorites is not null) { var snapshot = await _favorites.LoadAsync(default); return (snapshot.Revision, snapshot.Destinations.Select(item => item.Id).ToArray(), snapshot.Notice); }
            var bundles = await _bundles!.LoadAsync(default); return (bundles.Revision, bundles.Bundles.Select(item => item.Id).ToArray(), bundles.Notice);
        }
        public async Task SaveBackup()
        {
            if (_favorites is not null) await _favorites.SaveAsync(_favoriteBackup! with { Name = "Deliberate favorite repair" }, default);
            else await _bundles!.SaveAsync(_bundleBackup! with { Name = "Deliberate bundle repair" }, default);
        }
        public async Task MutateCurrent(string mutation)
        {
            if (_favorites is not null)
            {
                var current = _favoriteCurrent!;
                if (mutation == "delete") await _favorites.DeleteAsync(current.Id, current.Revision, current.ChangeId, default);
                else await _favorites.SaveAsync(current with { Name = "Current favorite changed" }, default);
            }
            else
            {
                var current = _bundleCurrent!;
                if (mutation == "delete") await _bundles!.DeleteAsync(current.Id, current.Revision, current.ChangeId, default);
                else if (mutation == "rename") await _bundles!.RenameAsync(current.Id, current.Revision, current.ChangeId, "Current bundle renamed", default);
                else await _bundles!.SaveAsync(current with { Name = "Current bundle changed" }, default);
            }
        }
        public async Task AssertBackupContents(bool repaired = false)
        {
            if (_favorites is not null)
            {
                var expected = _favoriteBackup!;
                var item = Assert.Single((await _favorites.LoadAsync(default)).Destinations);
                Assert.Equal(expected.PlaceId, item.PlaceId); Assert.Equal(expected.JobId, item.JobId);
                Assert.Equal(repaired ? "Deliberate favorite repair" : expected.Name, item.Name);
            }
            else
            {
                var expected = _bundleBackup!;
                var item = Assert.Single((await _bundles!.LoadAsync(default)).Bundles);
                Assert.Equal(expected.ProfileIds, item.ProfileIds);
                Assert.Equal(repaired ? "Deliberate bundle repair" : expected.Name, item.Name);
            }
        }
        public void Dispose() { _favorites?.Dispose(); _bundles?.Dispose(); }
    }
    public void Dispose()
    {
        string path = System.IO.Path.GetFullPath(Root), temp = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
        if (!string.Equals(System.IO.Path.GetDirectoryName(path), temp, StringComparison.OrdinalIgnoreCase) || System.IO.Path.GetFileName(path) != _name)
            throw new InvalidOperationException("Unexpected owned metadata-recovery fixture path.");
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
