using System.IO;
using System.Text.RegularExpressions;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// The packaged --self-check is the published-binary evidence. These pin what it covers: the staging script accepts
// exactly the stages the self-check runs, and the private-server stage really exercises the shipped protected store.
public sealed class PackagedSelfCheckCoverageTests : IDisposable
{
    private const string PrivateServerStage = "Protected private-server store round trip";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-packaged-private-servers-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void StagingScriptExpectsExactlyTheStagesTheSelfCheckRuns()
    {
        var stages = Regex.Matches(Resource("DistributionSelfCheck.cs.txt"), "\\bstage\\s*=\\s*\"(?<name>[^\"]+)\"")
            .Select(match => match.Groups["name"].Value).ToList();
        var list = Regex.Match(Resource("Check-ZenithSessionsManager.ps1"), "\\$expectedCases\\s*=\\s*@\\((?<list>[^)]*)\\)");
        Assert.True(list.Success, "The staging script no longer declares its expected self-check cases.");
        var expected = Regex.Matches(list.Groups["list"].Value, "'(?<name>[^']*)'").Select(match => match.Groups["name"].Value).ToList();
        Assert.Contains(PrivateServerStage, stages);
        Assert.Equal(stages.Count, stages.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(stages.Order(StringComparer.Ordinal), expected.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task PrivateServerStageRoundTripsAndClearsAnEntryLeftByAnAbortedRun()
    {
        string fixture = Path.Combine(_root, "private-server-fixture");
        using (var earlier = new PrivateServerStore(fixture))
        using (var code = new ProtectedPrivateServerCode("SyntheticCode0123"))
            await earlier.AddAsync(PackagedPrivateServerCheck.FixtureName, PackagedPrivateServerCheck.FixturePlaceId, 1, null, code, default);

        await PackagedPrivateServerCheck.VerifyAsync(_root, default);
        await PackagedPrivateServerCheck.VerifyAsync(_root, default); // The fixture root is reused across runs.

        using var reopened = new PrivateServerStore(fixture);
        Assert.Empty(await reopened.ReadAsync(default));
        Assert.True(File.Exists(reopened.Path));
    }

    [Fact]
    public async Task PrivateServerStageRefusesAndPreservesARecordItDidNotCreate()
    {
        string fixture = Path.Combine(_root, "private-server-fixture");
        SavedPrivateServer other;
        using (var store = new PrivateServerStore(fixture))
        using (var code = new ProtectedPrivateServerCode("OtherServerCode4567"))
            other = await store.AddAsync("Owner's weekend server", 5, 6, null, code, default);

        await Assert.ThrowsAsync<InvalidDataException>(() => PackagedPrivateServerCheck.VerifyAsync(_root, default));

        using var reopened = new PrivateServerStore(fixture);
        Assert.Equal(other, Assert.Single(await reopened.ReadAsync(default)));
    }

    private static string Resource(string name)
    {
        using var stream = typeof(PackagedSelfCheckCoverageTests).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-packaged-private-servers-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected fixture cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
