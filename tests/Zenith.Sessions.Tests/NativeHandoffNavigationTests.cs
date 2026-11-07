using Zenith.Sessions.Core;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class NativeHandoffNavigationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PinnedPageAndExactWebStartAreAllowedButDiagnosticPolicyRemainsUnchanged(bool job)
    {
        using var context=SyntheticHandoff.Context(job);
        string expected=context.Destination.ToWebStartUri().AbsoluteUri;
        Assert.Equal(RobloxHandoffProbeNavigation.AllowGamePage,NativeHandoffNavigation.Classify(context,expected,true));
        Assert.Equal(RobloxHandoffProbeNavigation.AllowGamePage,NativeHandoffNavigation.Classify(context,"https://www.roblox.com/games/1818/Exact-Game",false));
        Assert.Equal(job?expected:"https://www.roblox.com/games/1818",NativeHandoffNavigation.InitialPage(context));
        var diagnostic=new RobloxHandoffProbeContext(context.ProfileId,123,1,1818);
        Assert.Equal(RobloxHandoffProbeNavigation.Block,RobloxHandoffProbePolicy.ClassifyNavigation(diagnostic,expected,true));
        Assert.Equal(RobloxHandoffProbeNavigation.Block,NativeHandoffNavigation.Classify(context,expected+"&placeId=999",true));
    }

    [Theory]
    [InlineData("https://www.roblox.com.evil.example/games/1818")]
    [InlineData("http://www.roblox.com/games/1818")]
    [InlineData("https://www.roblox.com/games/1819")]
    [InlineData("https://www.roblox.com/games/1818?placeId=1819")]
    [InlineData("https://www.roblox.com/games/1818#x")]
    [InlineData("file:///C:/test")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:")]
    [InlineData("roblox-player:\r\nunsafe")]
    [InlineData(null)]
    public void OtherTopLevelNavigationNeverGetsAPreparationException(string? address)
    {
        using var context=SyntheticHandoff.Context();
        Assert.Equal(RobloxHandoffProbeNavigation.Block,NativeHandoffNavigation.Classify(context,address,true));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NativeSchemeExceptionExistsOnlyForAnAlreadyInstalledUnconditionalCancellingHandler(bool installed)
    {
        using var context=SyntheticHandoff.Context();string original=SyntheticHandoff.Uri(context,new HandoffClock());
        Assert.Equal(installed?RobloxHandoffProbeNavigation.AllowCancelledRobloxPlayer:RobloxHandoffProbeNavigation.Block,
            NativeHandoffNavigation.Classify(context,original,installed));
        Assert.Equal(RobloxHandoffProbeNavigation.Block,NativeHandoffNavigation.Classify(context,new string('x',NativeRobloxHandoff.MaximumCharacters+1),true));
    }
}
