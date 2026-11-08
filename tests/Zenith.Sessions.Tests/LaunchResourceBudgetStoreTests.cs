using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class LaunchResourceBudgetStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-queue-budget-tests-" + Guid.NewGuid().ToString("N"));
    private string PathToQueue => Path.Combine(_root, "launch-queue.json");
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly LaunchResourceBudget ExactBudget = new(5 * CapacityPolicy.GiB / 2 + 17, 3 * CapacityPolicy.GiB + 29);

    [Fact]
    public async Task FormatSevenRoundTripsMixedExactByteBudgetsAndLegacyUnknownWithoutCredentialMaterial()
    {
        var first = Row(ExactBudget);
        var second = Row(new(LaunchResourceBudget.MaximumBytes, 1)) with
        { State = LaunchState.ClientStartedUnverified, Problem = LaunchProblem.ClientStartedUnverified, ProcessReceipt = new(700001, Now.AddMinutes(-1).UtcTicks, Now) };
        var legacy = Row() with { State = LaunchState.Uncertain, Problem = LaunchProblem.Interrupted };
        var snapshot = new LaunchQueueSnapshot(false, [first, second, legacy]);
        using (var store = new LaunchQueueStore(_root)) await store.SaveAsync(snapshot, default);
        using var reopened = new LaunchQueueStore(_root); var loaded = await reopened.LoadAsync(default);
        Assert.Equal(snapshot.Paused, loaded.Paused); Assert.Equal(snapshot.Operations, loaded.Operations);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(PathToQueue));
        Assert.Equal(7, json.RootElement.GetProperty("Version").GetInt32());
        var operations = json.RootElement.GetProperty("Queue").GetProperty("Operations");
        Assert.Equal(ExactBudget.PhysicalBytes, operations[0].GetProperty("ResourceBudget").GetProperty("PhysicalBytes").GetUInt64());
        Assert.Equal(ExactBudget.CommitBytes, operations[0].GetProperty("ResourceBudget").GetProperty("CommitBytes").GetUInt64());
        Assert.Null(loaded.Operations[2].ResourceBudget); Assert.Equal(second.ProcessReceipt, loaded.Operations[1].ProcessReceipt);
        foreach (string forbidden in new[] { "SessionCookie", "AccessToken", "RefreshToken", "gameinfo", "roblox-player:" })
            Assert.DoesNotContain(forbidden, json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> LegacyFormats()
    { for (int version = 1; version <= 6; version++) foreach (bool explicitNull in new[] { false, true }) yield return [version, explicitNull]; }
    [Theory]
    [MemberData(nameof(LegacyFormats))]
    public async Task FormatsOneThroughSixKeepUnknownBudgetsAndDoNotRewriteOnRead(int version, bool explicitNull)
    {
        var original = Row(); var document = JsonNode.Parse(JsonSerializer.Serialize(new { Version = version, Queue = new LaunchQueueSnapshot(false, [original]) }))!;
        if (!explicitNull) document["Queue"]!["Operations"]![0]!.AsObject().Remove("ResourceBudget");
        string old = document.ToJsonString(); await Write(old);
        using var store = new LaunchQueueStore(_root); var loaded = await store.LoadAsync(default); var row = Assert.Single(loaded.Operations);
        Assert.Null(row.ResourceBudget); Assert.Equal(original.Intent, row.Intent); Assert.Equal(old, await File.ReadAllTextAsync(PathToQueue));
        if (version < 4) { Assert.True(loaded.Paused); Assert.Equal(LaunchState.ReviewRequired, row.State); Assert.Equal(LaunchProblem.Interrupted, row.Problem); }
        else { Assert.False(loaded.Paused); Assert.Equal(original, row); }
        await store.SaveAsync(loaded, default); var rewritten = await store.LoadAsync(default);
        Assert.Null(Assert.Single(rewritten.Operations).ResourceBudget); Assert.Equal(loaded.Operations, rewritten.Operations);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(PathToQueue)); Assert.Equal(7, json.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal(old, await File.ReadAllTextAsync(PathToQueue + ".bak"));
    }

    public static IEnumerable<object[]> HiddenLegacyBudgets()
    {
        for (int version = 1; version <= 6; version++)
            foreach (string shape in new[] { "single", "duplicate-budget", "duplicate-operations", "duplicate-queue" }) yield return [version, shape];
    }
    [Theory]
    [MemberData(nameof(HiddenLegacyBudgets))]
    public async Task OlderBudgetSemanticsCannotHideBehindUnknownOrDuplicateFieldsAndSelectAnOlderBackup(int version, string shape)
    {
        using var store = new LaunchQueueStore(_root); await SeedBackup(store);
        string backup = await File.ReadAllTextAsync(PathToQueue + ".bak"), unsupported = BudgetEnvelope(version, shape);
        await Write(unsupported);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(LaunchQueueSnapshot.Empty, default));
        Assert.Equal(unsupported, await File.ReadAllTextAsync(PathToQueue)); Assert.Equal(backup, await File.ReadAllTextAsync(PathToQueue + ".bak"));
        Assert.False(File.Exists(PathToQueue + ".damaged")); Assert.False(File.Exists(PathToQueue + ".tmp"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("false")]
    [InlineData("\"unknown\"")]
    public async Task AnyNonNullOldBudgetIsUnsupportedEvenBeforeItsValueCanBeDecoded(string budget)
    {
        using var store = new LaunchQueueStore(_root); await SeedBackup(store);
        string original = Envelope(6, OperationWithBudget(budget)); await Write(original);
        string backup = await File.ReadAllTextAsync(PathToQueue + ".bak");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(LaunchQueueSnapshot.Empty, default));
        Assert.Equal(original, await File.ReadAllTextAsync(PathToQueue)); Assert.Equal(backup, await File.ReadAllTextAsync(PathToQueue + ".bak"));
    }

    public static IEnumerable<object[]> InvalidJsonBudgets()
    {
        string over = (LaunchResourceBudget.MaximumBytes + 1).ToString(CultureInfo.InvariantCulture);
        yield return ["zero physical", "{\"PhysicalBytes\":0,\"CommitBytes\":1}"];
        yield return ["zero commit", "{\"PhysicalBytes\":1,\"CommitBytes\":0}"];
        yield return ["physical limit", "{\"PhysicalBytes\":" + over + ",\"CommitBytes\":1}"];
        yield return ["commit limit", "{\"PhysicalBytes\":1,\"CommitBytes\":" + over + "}"];
        yield return ["negative", "{\"PhysicalBytes\":-1,\"CommitBytes\":1}"];
        yield return ["fraction", "{\"PhysicalBytes\":1.5,\"CommitBytes\":1}"];
        yield return ["number as text", "{\"PhysicalBytes\":\"1\",\"CommitBytes\":1}"];
        yield return ["null counter", "{\"PhysicalBytes\":null,\"CommitBytes\":1}"];
        yield return ["boolean counter", "{\"PhysicalBytes\":true,\"CommitBytes\":1}"];
        yield return ["missing physical", "{\"CommitBytes\":1}"];
        yield return ["missing commit", "{\"PhysicalBytes\":1}"];
        yield return ["unknown field", "{\"PhysicalBytes\":1,\"CommitBytes\":1,\"FutureCost\":7}"];
        yield return ["duplicate physical", "{\"PhysicalBytes\":9,\"PhysicalBytes\":1,\"CommitBytes\":1}"];
        yield return ["duplicate commit", "{\"PhysicalBytes\":1,\"CommitBytes\":9,\"CommitBytes\":1}"];
        yield return ["overflow", "{\"PhysicalBytes\":18446744073709551616,\"CommitBytes\":1}"];
        yield return ["wrong object kind", "[]"];
        yield return ["string budget", "\"unknown\""];
    }
    [Theory]
    [MemberData(nameof(InvalidJsonBudgets))]
    public async Task MalformedCurrentBudgetFailsWithoutRepairOrInventedCost(string label, string budget)
    {
        string malformed = Envelope(7, OperationWithBudget(budget)); await Write(malformed);
        using var store = new LaunchQueueStore(_root); await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(default));
        Assert.True(malformed == await File.ReadAllTextAsync(PathToQueue), "Malformed " + label + " must be preserved for recovery.");
        Assert.False(File.Exists(PathToQueue + ".bak")); Assert.False(File.Exists(PathToQueue + ".damaged"));
    }

    public static IEnumerable<object[]> InvalidBudgets()
    {
        yield return [0UL, 1UL]; yield return [1UL, 0UL]; yield return [LaunchResourceBudget.MaximumBytes + 1, 1UL];
        yield return [1UL, LaunchResourceBudget.MaximumBytes + 1]; yield return [ulong.MaxValue, 1UL];
    }
    [Theory]
    [MemberData(nameof(InvalidBudgets))]
    public async Task InvalidInMemoryBudgetCannotReplaceEitherDurableGeneration(ulong physical, ulong commit)
    {
        using var store = new LaunchQueueStore(_root); await SeedBackup(store);
        byte[] before = await File.ReadAllBytesAsync(PathToQueue), backup = await File.ReadAllBytesAsync(PathToQueue + ".bak");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new(false, [Row(new(physical, commit))]), default));
        Assert.Equal(before, await File.ReadAllBytesAsync(PathToQueue)); Assert.Equal(backup, await File.ReadAllBytesAsync(PathToQueue + ".bak"));
        Assert.False(File.Exists(PathToQueue + ".tmp"));
    }

    [Theory]
    [InlineData("duplicate-budget")]
    [InlineData("duplicate-operations")]
    [InlineData("duplicate-queue")]
    public async Task CurrentDuplicateStructureRecoversOnlyTheVerifiedBackupAndPreservesTheMalformedOriginal(string shape)
    {
        using var store = new LaunchQueueStore(_root); var first = await SeedBackup(store);
        string backup = await File.ReadAllTextAsync(PathToQueue + ".bak"), malformed = BudgetEnvelope(7, shape, unknown: false);
        await Write(malformed); var recovered = await store.LoadAsync(default);
        // The backup is one generation behind: its queued row keeps its exact budget but returns for review.
        var forReview = first.Operations.Select(o => o with { State = LaunchState.ReviewRequired, Problem = LaunchProblem.Interrupted }).ToArray();
        Assert.Equal(forReview, recovered.Operations); Assert.Equal(ExactBudget, Assert.Single(recovered.Operations).ResourceBudget);
        Assert.Equal(malformed, await File.ReadAllTextAsync(PathToQueue)); Assert.Equal(backup, await File.ReadAllTextAsync(PathToQueue + ".bak"));
        await store.SaveAsync(recovered, default);
        Assert.Equal(malformed, await File.ReadAllTextAsync(PathToQueue + ".damaged")); Assert.Equal(backup, await File.ReadAllTextAsync(PathToQueue + ".bak"));
        Assert.Equal(forReview, (await store.LoadAsync(default)).Operations); Assert.False(File.Exists(PathToQueue + ".tmp"));
    }

    [Theory]
    [InlineData("primary")]
    [InlineData("backup-valid-primary")]
    [InlineData("backup-corrupt-primary")]
    public async Task UnknownFuturePrimaryOrBackupCannotBeOverwrittenOrSelectedAsAnOlderFallback(string location)
    {
        using var store = new LaunchQueueStore(_root); await SeedBackup(store);
        const string future = "{\"Version\":9,\"Queue\":{\"FutureResourceCosts\":true}}";
        if (location == "primary") await Write(future);
        else
        {
            await File.WriteAllTextAsync(PathToQueue + ".bak", future);
            if (location == "backup-corrupt-primary") await Write("preserve this damaged original");
        }
        string primary = await File.ReadAllTextAsync(PathToQueue), backup = await File.ReadAllTextAsync(PathToQueue + ".bak");
        if (location == "backup-valid-primary") Assert.NotEmpty((await store.LoadAsync(default)).Operations);
        else await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(LaunchQueueSnapshot.Empty, default));
        Assert.Equal(primary, await File.ReadAllTextAsync(PathToQueue)); Assert.Equal(backup, await File.ReadAllTextAsync(PathToQueue + ".bak"));
        Assert.False(File.Exists(PathToQueue + ".damaged")); Assert.False(File.Exists(PathToQueue + ".tmp"));
    }

    [Fact]
    public async Task MaximumBoundedQueueKeepsEveryPinnedByteCostWithinTheExistingOneMiBEnvelope()
    {
        var rows = Enumerable.Range(0, 1101).Select(index => Row(new(LaunchResourceBudget.MaximumBytes - (ulong)index,
            LaunchResourceBudget.MaximumBytes - (ulong)(2 * index)))).ToArray();
        var snapshot = new LaunchQueueSnapshot(true, rows);
        using var store = new LaunchQueueStore(_root); await store.SaveAsync(snapshot, default);
        Assert.InRange(new FileInfo(PathToQueue).Length, 1L, 1048576L);
        using var reopened = new LaunchQueueStore(_root); var loaded = await reopened.LoadAsync(default);
        Assert.Equal(1101, loaded.Operations.Count); Assert.Equal(snapshot.Operations, loaded.Operations);
        Assert.All(loaded.Operations, row => Assert.NotNull(row.ResourceBudget));
    }

    [Fact]
    public async Task InterruptedTemporaryAndCancellationDoNotReplaceOrEraseTheLastCommittedBudget()
    {
        using var store = new LaunchQueueStore(_root); await SeedBackup(store);
        byte[] before = await File.ReadAllBytesAsync(PathToQueue), backup = await File.ReadAllBytesAsync(PathToQueue + ".bak");
        const string interrupted = "{\"Version\":7,\"Queue\":{\"Operations\":[";
        await File.WriteAllTextAsync(PathToQueue + ".tmp", interrupted);
        Assert.NotNull(Assert.Single((await store.LoadAsync(default)).Operations).ResourceBudget);
        Assert.Equal(interrupted, await File.ReadAllTextAsync(PathToQueue + ".tmp"));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new(false, [Row(new(1, 1))]), stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(stop.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(PathToQueue)); Assert.Equal(backup, await File.ReadAllBytesAsync(PathToQueue + ".bak"));
        Assert.Equal(interrupted, await File.ReadAllTextAsync(PathToQueue + ".tmp"));
    }

    private static LaunchOperation Row(LaunchResourceBudget? budget = null) => new(
        new(Guid.NewGuid(), Guid.NewGuid(), 1, 123, 42, 0, Now, AuthorizationGeneration: 3), LaunchState.Queued, LaunchProblem.None, Now, ResourceBudget: budget);
    private async Task Write(string json) { Directory.CreateDirectory(_root); await File.WriteAllTextAsync(PathToQueue, json); }
    private static async Task<LaunchQueueSnapshot> SeedBackup(LaunchQueueStore store)
    {
        var first = new LaunchQueueSnapshot(true, [Row(ExactBudget)]); await store.SaveAsync(first, default);
        await store.SaveAsync(new(false, [Row(new(1, 2))]), default); return first;
    }
    private static string OperationWithBudget(string budget, bool unknown = false)
    {
        var operation = JsonNode.Parse(JsonSerializer.Serialize(Row()))!.AsObject(); operation.Remove("ResourceBudget");
        string plain = operation.ToJsonString();
        return plain[..^1] + ",\"ResourceBudget\":" + budget + (unknown ? ",\"FutureBudgetField\":true" : "") + "}";
    }
    private static string Envelope(int version, string operation) => "{\"Version\":" + version.ToString(CultureInfo.InvariantCulture)
        + ",\"Queue\":{\"Paused\":true,\"Operations\":[" + operation + "]}}";
    private static string BudgetEnvelope(int version, string shape, bool unknown = true)
    {
        string operation = OperationWithBudget("{\"PhysicalBytes\":1,\"CommitBytes\":2}", unknown);
        string prefix = "{\"Version\":" + version.ToString(CultureInfo.InvariantCulture) + ",\"Queue\":{\"Paused\":true,\"Operations\":[" + operation + "]";
        return shape switch
        {
            "single" => prefix + "}}",
            "duplicate-budget" => Envelope(version, operation[..^1] + ",\"ResourceBudget\":null}"),
            "duplicate-operations" => prefix + ",\"Operations\":[]}}",
            "duplicate-queue" => prefix + "},\"Queue\":{\"Paused\":true,\"Operations\":[]}}",
            _ => throw new ArgumentException("Unexpected synthetic envelope shape.")
        };
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-queue-budget-tests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected budget fixture cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
