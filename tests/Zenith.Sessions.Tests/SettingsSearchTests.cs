using System.Globalization;
using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class SettingsSearchTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u00a0\u2002")]
    public void BlankQueryReturnsTheEightUsefulFixedNavigationTopics(string? query)
    {
        var topics = SettingsSearchCatalog.Search(query);
        Assert.Equal(8, topics.Count);
        Assert.Equal(Enum.GetValues<SettingsSearchTarget>(), topics.Select(entry => entry.Target));
        Assert.Same(SettingsSearchCatalog.Search(null), topics);
    }

    [Theory]
    [InlineData("Graphite Mint", "theme-graphite-mint")]
    [InlineData("Midnight", "theme-midnight")]
    [InlineData("Amber Night", "theme-amber-night")]
    [InlineData("Ocean Blue", "theme-ocean-blue")]
    [InlineData("Violet Dusk", "theme-violet-dusk")]
    public void EachExistingThemeIsFindableWithoutInventingAnAppearanceControl(string query, string id)
    {
        var first = SettingsSearchCatalog.Search(query)[0];
        Assert.Equal(id, first.Id);
        Assert.Equal(SettingsSearchTarget.Appearance, first.Target);
    }

    [Theory]
    [InlineData("Maximum Efficiency", "profile-maximum-efficiency", SettingsSearchTarget.Performance)]
    [InlineData("Balanced", "profile-balanced", SettingsSearchTarget.Performance)]
    [InlineData("High Performance", "profile-high-performance", SettingsSearchTarget.Performance)]
    [InlineData("Main Account Priority", "profile-main-account-priority", SettingsSearchTarget.Performance)]
    [InlineData("Custom", "profile-custom", SettingsSearchTarget.CustomTiming)]
    public void EachManagerPresetIsAReadOnlyNavigationResult(string query, string id, SettingsSearchTarget target)
    {
        var first = SettingsSearchCatalog.Search(query)[0];
        Assert.Equal(id, first.Id);
        Assert.Equal(target, first.Target);
    }

    [Theory]
    [InlineData("process interval", "process-timing", SettingsSearchTarget.CustomTiming)]
    [InlineData("dashboard", "dashboard-timing", SettingsSearchTarget.CustomTiming)]
    [InlineData("stagger", "launch-spacing", SettingsSearchTarget.CustomTiming)]
    [InlineData("stabilisation", "stabilization-timing", SettingsSearchTarget.CustomTiming)]
    [InlineData("spare ram", "spare-ram", SettingsSearchTarget.Memory)]
    [InlineData("per client physical", "physical-per-client", SettingsSearchTarget.Memory)]
    [InlineData("per client commit", "commit-per-client", SettingsSearchTarget.Memory)]
    [InlineData("optional maximum", "maximum-clients", SettingsSearchTarget.Memory)]
    [InlineData("Authenticode", "publisher-verification", SettingsSearchTarget.Installations)]
    [InlineData("Bloxstrap", "installations", SettingsSearchTarget.Installations)]
    [InlineData("WebView2", "webview-runtime", SettingsSearchTarget.SignIn)]
    [InlineData("open settings folder", "settings-folder", SettingsSearchTarget.Save)]
    [InlineData("credential cleanup", "protected-recovery", SettingsSearchTarget.Recovery)]
    public void ActualControlsAndExplanationsHaveUsefulAliases(string query, string expected, SettingsSearchTarget target)
    {
        Assert.Contains(SettingsSearchCatalog.Search(query), entry => entry.Id == expected && entry.Target == target);
    }

    [Fact]
    public void PerformanceTransferFindsReviewAndNeverAdvertisesCredentialExport()
    {
        var result = Assert.Single(SettingsSearchCatalog.Search("performance export"));
        Assert.Equal("performance-transfer", result.Id);
        Assert.Equal(SettingsSearchTarget.Performance, result.Target);
        Assert.Contains("review", result.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("credentials are excluded", result.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fps")]
    [InlineData("MSAA")]
    [InlineData("lowest graphics")]
    [InlineData("client cpu")]
    public void UnavailableClientPerformanceOptionsOnlyFindThePendingExplanation(string query)
    {
        var result = Assert.Single(SettingsSearchCatalog.Search(query));
        Assert.Equal("pending-client-performance", result.Id);
        Assert.Equal(SettingsSearchTarget.Performance, result.Target);
        Assert.Contains("not configurable", result.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("client cpu")]
    [InlineData("cpu priority")]
    [InlineData("process priority")]
    public void ClientCpuSearchPointsToTheSessionsPriorityControl(string query)
    {
        var result = Assert.Single(SettingsSearchCatalog.Search(query));
        Assert.Equal("pending-client-performance", result.Id);
        Assert.Contains("Sessions", result.Description, StringComparison.Ordinal);
        Assert.Contains("Client CPU priority", result.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("client CPU/render", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomNavigationExplainsThatItDoesNotChangeTheProfileOrDraft()
    {
        var result = SettingsSearchCatalog.Search("Custom")[0];
        Assert.Equal(SettingsSearchTarget.CustomTiming, result.Target);
        Assert.Contains("does not select", result.Description, StringComparison.Ordinal);
        Assert.Contains("change a draft", result.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MÉMÓRY", "memory")]
    [InlineData("me\u0301mo\u0301ry", "memory")]
    [InlineData("STÁBILIZÁTÍON", "stabilization")]
    [InlineData("SIGN-IN", "sign in")]
    public void SearchIsCaseAndAccentInsensitiveWithNormalizedWordSeparators(string query, string equivalent)
        => Assert.Equal(Ids(equivalent), Ids(query));

    [Fact]
    public void RankingIsCultureIndependentDeterministicAndExactTitlesWin()
    {
        string[] expected = Ids("installation");
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal(expected, Ids("INSTALLATION"));
            for (int i = 0; i < 30; i++) Assert.Equal(expected, Ids("installation"));
            Assert.Equal("save-settings", SettingsSearchCatalog.Search("Save settings")[0].Id);
            Assert.Equal("profile-balanced", SettingsSearchCatalog.Search("Balanced")[0].Id);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void EveryTokenMustMatchAndPrefixesDoNotMatchInsideAnUnrelatedWord()
    {
        Assert.Empty(SettingsSearchCatalog.Search("memory WebView2"));
        Assert.Empty(SettingsSearchCatalog.Search("bview"));
        Assert.Contains(SettingsSearchCatalog.Search("dash inter"), entry => entry.Id == "dashboard-timing");
    }

    [Fact]
    public void QueryLengthAndTokenCountHaveExactBounds()
    {
        string atLimit = "memory".PadRight(SettingsSearchCatalog.MaximumQueryLength);
        Assert.NotEmpty(SettingsSearchCatalog.Search(atLimit));
        Assert.Empty(SettingsSearchCatalog.Search(atLimit + " "));
        Assert.NotEmpty(SettingsSearchCatalog.Search(string.Join(" ", Enumerable.Repeat("memory", 8))));
        Assert.Empty(SettingsSearchCatalog.Search(string.Join(" ", Enumerable.Repeat("memory", 9))));
        Assert.Empty(SettingsSearchCatalog.Search(new string('x', 100_000)));
    }

    [Theory]
    [InlineData("memory\0")]
    [InlineData("memory\n")]
    [InlineData("memory\t")]
    [InlineData("memory\u007f")]
    [InlineData("memory\u0085")]
    [InlineData("memory\u202e")]
    [InlineData("memory\u200b")]
    [InlineData("memory\u2028")]
    [InlineData("memory\u2029")]
    [InlineData("\u0301")]
    [InlineData("!!!")]
    [InlineData("😀")]
    public void MalformedControlAndNonsearchInputReturnsEmptyWithoutThrowingOrEchoing(string query)
        => Assert.Empty(SettingsSearchCatalog.Search(query));

    [Fact]
    public void InvalidUtf16IsRejectedWithoutDependingOnAttributeStringSerialization()
    {
        Assert.Empty(SettingsSearchCatalog.Search(new string((char)0xd800, 1)));
        Assert.Empty(SettingsSearchCatalog.Search(new string((char)0xdc00, 1)));
        Assert.Empty(SettingsSearchCatalog.Search("memory" + new string((char)0xd800, 1) + "x"));
    }

    [Fact]
    public void ResultsAndKeywordsCannotMutateTheFixedRegistry()
    {
        var defaults = SettingsSearchCatalog.Search(null);
        var result = SettingsSearchCatalog.Search("memory");
        Assert.Throws<NotSupportedException>(() => { ((IList<SettingsSearchEntry>)defaults)[0] = result[0]; });
        Assert.Throws<NotSupportedException>(() => ((IList<SettingsSearchEntry>)result).Clear());
        Assert.Throws<NotSupportedException>(() => { ((IList<string>)result[0].Keywords)[0] = "user-secret-marker"; });
        Assert.Equal("appearance", SettingsSearchCatalog.Search(null)[0].Id);
        Assert.Empty(SettingsSearchCatalog.Search("user-secret-marker"));
    }

    [Fact]
    public void ResultBoundAndAllReturnedMetadataStayFixedAcrossUntrustedQueries()
    {
        Assert.Equal(10, SettingsSearchCatalog.Search("p").Count);
        foreach (string query in new[] { "p", "settings", "memory", "account", "runtime", "monit", "client", "profile", "color" })
        {
            var results = SettingsSearchCatalog.Search(query);
            Assert.InRange(results.Count, 0, SettingsSearchCatalog.MaximumResults);
            Assert.Equal(results.Count, results.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count());
            foreach (var entry in results)
            {
                Assert.InRange(entry.Id.Length, 1, 64);
                Assert.InRange(entry.Title.Length, 1, 96);
                Assert.InRange(entry.Description.Length, 1, 260);
                Assert.True(Enum.IsDefined(entry.Target));
                Assert.All(entry.Keywords, keyword => Assert.InRange(keyword.Length, 1, 80));
            }
        }
        Assert.Empty(SettingsSearchCatalog.Search("uniquerawquerymarker"));
        Assert.DoesNotContain(SettingsSearchCatalog.Search(null), entry => entry.Description.Contains("uniquerawquerymarker", StringComparison.Ordinal));
    }

    private static string[] Ids(string? query) => SettingsSearchCatalog.Search(query).Select(entry => entry.Id).ToArray();
}
