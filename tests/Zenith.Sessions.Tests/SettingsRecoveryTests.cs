using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class SettingsRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-settings-recovery-" + Guid.NewGuid().ToString("N"));
    private string Primary => Path.Combine(_root, "settings.json");
    private static SessionsSettings Chosen => new SessionsSettings(Theme: "ocean-blue", DenseAccounts: true,
        Capacity: new(SparePhysicalGiB: 7), Performance: new(Profile: ManagerPerformanceProfile.MaximumEfficiency)).Validated();

    [Fact]
    public async Task GenuinelyFreshStorageCreatesSchemaOneOnceAndNormalAutosavesRetainTheLastValidCopy()
    {
        using var store = new SettingsStore(_root); Assert.False(Directory.Exists(_root));
        var first = await store.LoadAsync(default); Assert.Null(first.Notice); Assert.Equal(new SessionsSettings().Validated(), first.Settings);
        byte[] original = await File.ReadAllBytesAsync(Primary);
        var repeat = await store.LoadAsync(default); Assert.Equal(first.Settings, repeat.Settings); Assert.Equal(original, await File.ReadAllBytesAsync(Primary));
        await store.SaveAsync(Chosen, default); Assert.Equal(original, await File.ReadAllBytesAsync(Primary + ".bak"));
        Assert.Equal(Chosen, (await store.LoadAsync(default)).Settings);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Primary)); Assert.Equal(1, json.RootElement.GetProperty("Version").GetInt32());
        Assert.False(File.Exists(Primary + ".tmp")); Assert.False(File.Exists(Primary + ".damaged"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidOrphanBackupIsReadOnlyUntilAnExplicitSaveAndNeverBecomesDefaults(bool implicitVersion)
    {
        var document = JsonNode.Parse(JsonSerializer.Serialize(Chosen))!; if (implicitVersion) document.AsObject().Remove("Version");
        await Write(".bak", document.ToJsonString()); var before = await Snapshot();
        using var store = new SettingsStore(_root); var recovered = await store.LoadAsync(default);
        Assert.Equal(Chosen, recovered.Settings); Assert.Contains("missing", recovered.Notice!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("last valid", recovered.Notice!, StringComparison.OrdinalIgnoreCase);
        if (implicitVersion) Assert.Contains("schema 1", recovered.Notice!, StringComparison.OrdinalIgnoreCase);
        await Preserved(before); Assert.False(File.Exists(Primary));
        await store.SaveAsync(recovered.Settings, default); Assert.Equal(Chosen, (await store.LoadAsync(default)).Settings);
        Assert.Equal(before[".bak"], await File.ReadAllBytesAsync(Primary + ".bak"));
        Assert.False(File.Exists(Primary + ".tmp")); Assert.False(File.Exists(Primary + ".damaged"));
    }

    [Theory]
    [InlineData("temporary")]
    [InlineData("damaged")]
    [InlineData("both")]
    public async Task EvidenceWithoutAMainFileOrBackupCannotInitializeOrSaveDefaults(string evidence)
    {
        if (evidence is "temporary" or "both") await Write(".tmp", JsonSerializer.Serialize(Chosen));
        if (evidence is "damaged" or "both") await Write(".damaged", "preserve owned damaged settings bytes");
        var before = await Snapshot(); using var store = new SettingsStore(_root);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new(), default));
        await Preserved(before); Assert.False(File.Exists(Primary));
    }

    [Theory]
    [InlineData("")]
    [InlineData("broken synthetic settings")]
    [InlineData("{\"Version\":1,\"Capacity\":{\"SparePhysicalGiB\":-1}}")]
    public async Task InvalidOrphanBackupFailsWithRecoveryFeedbackAndPreservesItsBytes(string backup)
    {
        await Write(".bak", backup); var before = await Snapshot(); using var store = new SettingsStore(_root);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(default)); Assert.Contains("preserved", error.Message);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Chosen, default));
        await Preserved(before); Assert.False(File.Exists(Primary));
    }

    [Theory]
    [InlineData("{\"Version\":2,\"Capacity\":\"future shape\"}")]
    [InlineData("{\"Version\":99,\"FutureSetting\":true}")]
    [InlineData("{\"Version\":1,\"Version\":2}")]
    [InlineData("{\"Version\":1,\"Version\":1}")]
    [InlineData("{\"Version\":\"1\"}")]
    [InlineData("{\"Version\":null}")]
    [InlineData("{\"Version\":0}")]
    [InlineData("{\"Version\":2147483648}")]
    public async Task UnsupportedOrDuplicateVersionsCannotUseAValidOlderBackupOrBeOverwritten(string unsupported)
    {
        using var store = new SettingsStore(_root); await Seed(store); await Write("", unsupported); var before = await Snapshot();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Chosen, default));
        await Preserved(before); Assert.False(File.Exists(Primary + ".tmp")); Assert.False(File.Exists(Primary + ".damaged"));
    }

    public static IEnumerable<object[]> DeepVersionEvidence()
    {
        foreach (bool backup in new[] { false, true }) foreach (bool bom in new[] { false, true }) foreach (bool duplicate in new[] { false, true })
            yield return [backup, bom, duplicate];
    }
    [Theory]
    [MemberData(nameof(DeepVersionEvidence))]
    public async Task DeepUnfamiliarPrefixCannotHideAFutureOrDuplicateVersionInEitherGeneration(bool backup, bool bom, bool duplicate)
    {
        using var store = new SettingsStore(_root); await Seed(store);
        string deep = new string('[', 128) + "0" + new string(']', 128);
        string unsupported = "{" + (duplicate ? "\"Version\":1," : "") + "\"UnfamiliarPrefix\":" + deep
            + (duplicate ? ",\"Version\":1}" : ",\"Version\":2}");
        if (backup) await Write("", "preserve bounded malformed primary");
        await WriteEncoded(backup ? ".bak" : "", unsupported, bom); var before = await Snapshot();
        var load = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(default)); Assert.Contains("unsupported format", load.Message);
        var save = await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Chosen, default)); Assert.Contains("unsupported format", save.Message);
        await Preserved(before); Assert.False(File.Exists(Primary + ".tmp")); Assert.False(File.Exists(Primary + ".damaged"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AnAlreadyObservedFutureOrDuplicateVersionWinsOverMalformedLaterJson(bool backup, bool duplicate)
    {
        using var store = new SettingsStore(_root); await Seed(store);
        string unsupported = duplicate ? "{\"Version\":1,\"Version\":1,\"Malformed\":[" : "{\"Version\":2,\"Malformed\":[";
        if (backup) await Write("", "preserve bounded malformed primary");
        await Write(backup ? ".bak" : "", unsupported); var before = await Snapshot();
        var load = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(default)); Assert.Contains("unsupported format", load.Message);
        var save = await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Chosen, default)); Assert.Contains("unsupported format", save.Message);
        await Preserved(before); Assert.False(File.Exists(Primary + ".tmp")); Assert.False(File.Exists(Primary + ".damaged"));
    }

    [Fact]
    public async Task VersionPreflightDoesNotRaiseTheNormalSettingsDecodeDepthLimit()
    {
        using var store = new SettingsStore(_root); await Seed(store);
        string deep = "{\"UnfamiliarPrefix\":" + new string('[', 128) + "0" + new string(']', 128) + ",\"Version\":1}";
        await Write("", deep); var before = await Snapshot(); var loaded = await store.LoadAsync(default);
        Assert.Equal(Chosen, loaded.Settings); Assert.Contains("last valid", loaded.Notice!, StringComparison.OrdinalIgnoreCase);
        await Preserved(before);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("corrupt")]
    [InlineData("missing")]
    public async Task FutureBackupIsNeverUsedAsDefaultsOrOverwrittenRegardlessOfPrimaryState(string primaryState)
    {
        using var store = new SettingsStore(_root); await Seed(store);
        if (primaryState == "corrupt") await Write("", "preserve damaged primary");
        if (primaryState == "missing") File.Delete(Primary);
        await Write(".bak", "{\"Version\":2,\"FutureSetting\":true}"); var before = await Snapshot();
        if (primaryState == "valid") Assert.Equal("amber-night", (await store.LoadAsync(default)).Settings.Theme);
        else await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Chosen, default));
        await Preserved(before); Assert.False(File.Exists(Primary + ".tmp")); Assert.False(File.Exists(Primary + ".damaged"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SchemaOneStillDefaultsMissingOptionalFieldsWithoutRewritingOnLoad(bool explicitVersion)
    {
        string old = (explicitVersion ? "{\"Version\":1," : "{")
            + "\"Theme\":\"ocean-blue\",\"DenseAccounts\":true,\"Capacity\":{\"SparePhysicalGiB\":7}}";
        await Write("", old); using var store = new SettingsStore(_root); var loaded = await store.LoadAsync(default);
        Assert.Equal("ocean-blue", loaded.Settings.Theme); Assert.True(loaded.Settings.DenseAccounts); Assert.Equal(7, loaded.Settings.Capacity!.SparePhysicalGiB);
        Assert.Equal(ManagerPerformanceProfile.Balanced, loaded.Settings.Performance!.Profile); Assert.NotNull(loaded.Settings.WindowLayout!.Tile);
        if (explicitVersion) Assert.Null(loaded.Notice); else Assert.Contains("schema 1", loaded.Notice!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(old, await File.ReadAllTextAsync(Primary));
        await store.SaveAsync(loaded.Settings, default); Assert.Equal(old, await File.ReadAllTextAsync(Primary + ".bak"));
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Primary)); Assert.Equal(1, saved.RootElement.GetProperty("Version").GetInt32());
    }

    [Fact]
    public async Task ExistingSchemaOneUnknownMetadataRemainsReadableAndIsNotRewrittenOnLoad()
    {
        const string original = "{\"Version\":1,\"Theme\":\"ocean-blue\",\"ExtraMetadata\":{\"Description\":\"owned nonsecret compatibility fixture\"}}";
        await Write("", original); using var store = new SettingsStore(_root); var loaded = await store.LoadAsync(default);
        Assert.Equal("ocean-blue", loaded.Settings.Theme); Assert.Null(loaded.Notice);
        Assert.Equal(original, await File.ReadAllTextAsync(Primary)); Assert.False(File.Exists(Primary + ".bak")); Assert.False(File.Exists(Primary + ".tmp"));
    }

    [Fact]
    public async Task ExistingUtf8BomSettingsRemainReadableAndTheOriginalBackupKeepsItsExactBytes()
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(Chosen), original = new byte[body.Length + 3];
        original[0] = 0xEF; original[1] = 0xBB; original[2] = 0xBF; body.CopyTo(original, 3);
        Directory.CreateDirectory(_root); await File.WriteAllBytesAsync(Primary, original);
        using var store = new SettingsStore(_root); Assert.Equal(Chosen, (await store.LoadAsync(default)).Settings);
        Assert.Equal(original, await File.ReadAllBytesAsync(Primary)); await store.SaveAsync(Chosen with { DenseAccounts = false }, default);
        Assert.Equal(original, await File.ReadAllBytesAsync(Primary + ".bak"));
    }

    [Fact]
    public async Task DeliberateRepairPreservesOriginalAndBackupAndRepeatedDamageIsIdempotentButDifferentDamageBlocks()
    {
        using var store = new SettingsStore(_root); await Seed(store); byte[] backup = await File.ReadAllBytesAsync(Primary + ".bak");
        const string firstDamage = "first owned damaged original"; await Write("", firstDamage);
        var recovered = await store.LoadAsync(default); Assert.Equal(Chosen, recovered.Settings); Assert.NotNull(recovered.Notice);
        Assert.Equal(firstDamage, await File.ReadAllTextAsync(Primary)); await store.SaveAsync(recovered.Settings, default);
        Assert.Equal(firstDamage, await File.ReadAllTextAsync(Primary + ".damaged")); Assert.Equal(backup, await File.ReadAllBytesAsync(Primary + ".bak"));
        await Write("", firstDamage);
        // Sharing permits reads but denies rewriting/replacing the occupied damage slot.
        // An identical original must not need a second copy or truncate that first copy.
        await using (var hold = new FileStream(Primary + ".damaged", FileMode.Open, FileAccess.Read, FileShare.Read))
            await store.SaveAsync(Chosen with { Theme = "violet-dusk" }, default);
        Assert.Equal(firstDamage, await File.ReadAllTextAsync(Primary + ".damaged")); Assert.Equal(backup, await File.ReadAllBytesAsync(Primary + ".bak"));
        await Write("", "different second damaged original"); var before = await Snapshot();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Chosen, default));
        await Preserved(before); Assert.False(File.Exists(Primary + ".tmp"));
    }

    [Fact]
    public async Task AnExistingDamageCopyDoesNotDisableOrdinaryAutosaveOfAValidMainFile()
    {
        using var store = new SettingsStore(_root); await Seed(store); await Write(".damaged", "historic evidence");
        byte[] previous = await File.ReadAllBytesAsync(Primary); await store.SaveAsync(Chosen with { Theme = "violet-dusk" }, default);
        Assert.Equal("violet-dusk", (await store.LoadAsync(default)).Settings.Theme);
        Assert.Equal(previous, await File.ReadAllBytesAsync(Primary + ".bak")); Assert.Equal("historic evidence", await File.ReadAllTextAsync(Primary + ".damaged"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"Version\":1,\"Theme\":")]
    public async Task PreexistingTemporaryIsNeverTruncatedOrDeletedByAFailedSave(string interrupted)
    {
        using var store = new SettingsStore(_root); await Seed(store); await Write(".tmp", interrupted); var before = await Snapshot();
        await using (var hold = new FileStream(Primary + ".tmp", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var loaded = await store.LoadAsync(default); Assert.Equal("amber-night", loaded.Settings.Theme);
            Assert.Contains("temporary", loaded.Notice!, StringComparison.OrdinalIgnoreCase);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Chosen, default));
        }
        await Preserved(before);
    }

    [Theory]
    [InlineData("fresh")]
    [InlineData("orphan")]
    [InlineData("primary")]
    [InlineData("damaged")]
    public async Task CancellationBeforeEntryPreservesEveryOwnedFileAndNeverInitializesDefaults(string state)
    {
        if (state == "orphan") await Write(".bak", JsonSerializer.Serialize(Chosen));
        if (state == "primary") { using var seeded = new SettingsStore(_root); await Seed(seeded); }
        if (state == "damaged") await Write(".damaged", "keep owned evidence");
        var before = await Snapshot(); using var store = new SettingsStore(_root); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(Chosen, stop.Token));
        await Preserved(before); if (state == "fresh") Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("primary")]
    [InlineData("backup")]
    [InlineData("damage")]
    public async Task OversizedEvidenceIsBoundedAndCannotBeCopiedOrOverwrittenDuringRepair(string location)
    {
        using var store = new SettingsStore(_root); await Seed(store);
        if (location == "damage") { await Write("", "bounded malformed main"); await Write(".damaged", new string('x', 65537)); }
        else await Write(location == "primary" ? "" : ".bak", new string('x', 65537));
        var before = await Snapshot(); var loaded = await store.LoadAsync(default);
        Assert.Equal(location == "backup" ? "amber-night" : "ocean-blue", loaded.Settings.Theme);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(loaded.Settings, default));
        await Preserved(before); Assert.False(File.Exists(Primary + ".tmp"));
    }

    [Fact]
    public async Task AValidSettingsFileAtTheByteLimitRemainsCompatibleAndCanBeBackedUpExactly()
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(Chosen), padded = new byte[65536];
        Array.Fill(padded, (byte)' '); body.CopyTo(padded, 0); Directory.CreateDirectory(_root); await File.WriteAllBytesAsync(Primary, padded);
        using var store = new SettingsStore(_root); Assert.Equal(Chosen, (await store.LoadAsync(default)).Settings);
        Assert.Equal(padded, await File.ReadAllBytesAsync(Primary)); await store.SaveAsync(Chosen with { DenseAccounts = false }, default);
        Assert.Equal(padded, await File.ReadAllBytesAsync(Primary + ".bak")); Assert.False((await store.LoadAsync(default)).Settings.DenseAccounts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharingFailuresPropagateInsteadOfBecomingFallbackOrFreshSettings(bool backupLocked)
    {
        using var store = new SettingsStore(_root); await Seed(store); var before = await Snapshot();
        await using (var hold = new FileStream(Primary + (backupLocked ? ".bak" : ""), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (backupLocked) Assert.Equal("amber-night", (await store.LoadAsync(default)).Settings.Theme);
            else await Assert.ThrowsAsync<IOException>(() => store.LoadAsync(default));
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(Chosen, default));
        }
        await Preserved(before); Assert.False(File.Exists(Primary + ".tmp")); Assert.False(File.Exists(Primary + ".damaged"));
    }

    [Fact]
    public async Task DeniedReplacementPreservesPreferencesAndCleansOnlyTheTemporaryCreatedByThisAttempt()
    {
        using var store = new SettingsStore(_root); await Seed(store); var before = await Snapshot();
        File.SetAttributes(Primary, FileAttributes.ReadOnly);
        try
        {
            var failure = await Record.ExceptionAsync(() => store.SaveAsync(Chosen, default));
            Assert.True(failure is UnauthorizedAccessException or IOException, "A denied native replacement must propagate as an access or I/O failure.");
        }
        finally { File.SetAttributes(Primary, FileAttributes.Normal); }
        await Preserved(before); Assert.False(File.Exists(Primary + ".tmp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".bak")]
    public async Task AnOwnedEntryThatIsADirectoryCannotMasqueradeAsAbsent(string suffix)
    {
        Directory.CreateDirectory(Primary + suffix); string retained = Path.Combine(Primary + suffix, "owned-evidence.txt"); await File.WriteAllTextAsync(retained, "preserve");
        using var store = new SettingsStore(_root);
        await Assert.ThrowsAsync<IOException>(() => store.LoadAsync(default)); await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(Chosen, default));
        Assert.Equal("preserve", await File.ReadAllTextAsync(retained)); Assert.False(File.Exists(Primary + ".tmp"));
    }

    private static async Task Seed(SettingsStore store)
    { await store.SaveAsync(Chosen, default); await store.SaveAsync(Chosen with { Theme = "amber-night", DenseAccounts = false }, default); }
    private async Task Write(string suffix, string value)
    { Directory.CreateDirectory(_root); await File.WriteAllTextAsync(Primary + suffix, value); }
    private async Task WriteEncoded(string suffix, string value, bool bom)
    {
        byte[] body = Encoding.UTF8.GetBytes(value), bytes = body;
        if (bom) { bytes = new byte[body.Length + 3]; bytes[0] = 0xEF; bytes[1] = 0xBB; bytes[2] = 0xBF; body.CopyTo(bytes, 3); }
        Directory.CreateDirectory(_root); await File.WriteAllBytesAsync(Primary + suffix, bytes);
    }
    private async Task<Dictionary<string, byte[]>> Snapshot()
    {
        var result = new Dictionary<string, byte[]>();
        foreach (string suffix in new[] { "", ".bak", ".tmp", ".damaged" })
            if (File.Exists(Primary + suffix)) result.Add(suffix, await File.ReadAllBytesAsync(Primary + suffix));
        return result;
    }
    private async Task Preserved(IReadOnlyDictionary<string, byte[]> expected)
    {
        var actual = await Snapshot(); Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var item in expected) Assert.Equal(item.Value, actual[item.Key]);
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-settings-recovery-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected settings fixture cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
