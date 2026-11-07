using System.IO;
using System.Text.RegularExpressions;
using Xunit;
using Zenith.Sessions.App;

namespace Zenith.Sessions.Tests;

// The packaged self-check stops at the first failing stage. Its console line must count against every planned stage
// and name the one that failed, so "3/4" can never hide that the remaining stages never ran.
public sealed class DistributionSelfCheckSummaryTests
{
    [Fact]
    public void SummaryUsesPlannedStageCountAndNamesTheFailedStage()
    {
        Assert.Equal("Distribution self-check: 3/43 (failed at Packaged native SQLite and profile round trip); release readiness remains false.",
            DistributionSelfCheck.Summary(3,DistributionSelfCheck.PlannedStageCount,"Packaged native SQLite and profile round trip"));
        // The success line keeps the stored "N/N" evidence format; Z1051 added the protected private-server stage (43).
        Assert.Equal("Distribution self-check: 43/43; release readiness remains false.",
            DistributionSelfCheck.Summary(43,DistributionSelfCheck.PlannedStageCount,null));
    }

    [Fact]
    public void PlannedStageCountMatchesTheManagerScriptExpectedCases()
    {
        // The manager script requires exactly these named, unique, passing cases; a stage added to one must be added to both.
        using var stream=typeof(DistributionSelfCheckSummaryTests).Assembly.GetManifestResourceStream("Check-ZenithSessionsManager.ps1")!;
        string script=new StreamReader(stream).ReadToEnd();
        var list=Regex.Match(script,@"\$expectedCases=@\(([^)]*)\)");
        Assert.True(list.Success,"The manager script has no $expectedCases list.");
        var names=Regex.Matches(list.Groups[1].Value,"'([^']+)'").Select(match=>match.Groups[1].Value).ToArray();
        Assert.Equal(names.Length,names.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(DistributionSelfCheck.PlannedStageCount,names.Length);
    }
}
