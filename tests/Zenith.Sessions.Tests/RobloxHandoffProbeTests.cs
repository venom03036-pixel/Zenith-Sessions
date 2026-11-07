using System.Globalization;
using System.Text.Json;
using Zenith.Sessions.Core;
using Xunit;

namespace Zenith.Sessions.Tests;

/// <summary>Synthetic strings and disposable synthetic credentials only. No browser, network or native dispatch.</summary>
public sealed class RobloxHandoffProbeTests
{
    private static readonly RobloxHandoffProbeContext Context=new(Guid.Parse("12345678-1234-4234-8234-123456789abc"),456789123,19,42);
    private const string Page="https://www.roblox.com/games/42";
    private const string Origin="https://www.roblox.com";
    private const string Candidate="roblox-player:1+launchmode:play+gameinfo:synthetic-ticket";
    private static RobloxHandoffProbeResult Inspect(string? uri,string? source=Page,string? origin=Origin,RobloxHandoffProbeContext? context=null)
        =>RobloxHandoffProbePolicy.InspectExternal(context??Context,source,origin,uri);

    [Fact]
    public void ContextSupportsPositiveInt64IdsAndRedactsItsDisplay()
    {
        Assert.Same(Context,Context.Validated());
        var maximum=new RobloxHandoffProbeContext(Guid.NewGuid(),long.MaxValue,long.MaxValue,long.MaxValue);
        Assert.Same(maximum,maximum.Validated());
        Assert.Equal("https://www.roblox.com/games/9223372036854775807",RobloxHandoffProbePolicy.CreateGamePageUri(maximum));
        string display=Context.ToString();Assert.DoesNotContain(Context.ProfileId.ToString(),display);
        Assert.DoesNotContain(Context.ExpectedUserId.ToString(CultureInfo.InvariantCulture),display);
        Assert.DoesNotContain("42",display);Assert.DoesNotContain("19",display);
        foreach(var invalid in new[]{Context with {ProfileId=Guid.Empty},Context with {ExpectedUserId=0},Context with {ExpectedUserId=-1},
            Context with {AuthorizationGeneration=0},Context with {AuthorizationGeneration=-1},Context with {PlaceId=0},Context with {PlaceId=-1}})
        {
            Assert.Throws<ArgumentException>(()=>invalid.Validated());
            Assert.False(RobloxHandoffProbePolicy.IsAllowedGamePage(invalid,Page));
            Assert.Equal(RobloxHandoffProbeNavigation.Block,RobloxHandoffProbePolicy.ClassifyNavigation(invalid,Candidate,true));
            Assert.Equal(RobloxHandoffProbeOutcome.ContextRejected,RobloxHandoffProbePolicy.InspectExternal(invalid,Page,Origin,Candidate).Outcome);
        }
    }

    [Theory]
    [InlineData("https://www.roblox.com/games/42")]
    [InlineData("HTTPS://WWW.ROBLOX.COM/games/42")]
    [InlineData("https://www.roblox.com/games/42/A-game_1.0~")]
    [InlineData("https://www.roblox.com/games/42/Game-%C3%A9")]
    public void OnlyExactTargetPageAndOneBoundedSlugAreAllowed(string page)
    {
        Assert.True(RobloxHandoffProbePolicy.IsAllowedGamePage(Context,page));
        Assert.Equal(RobloxHandoffProbeNavigation.AllowGamePage,RobloxHandoffProbePolicy.ClassifyNavigation(Context,page,false));
    }

    [Theory]
    [InlineData("http://www.roblox.com/games/42")]
    [InlineData("https://roblox.com/games/42")]
    [InlineData("https://www.roblox.com.evil.example/games/42")]
    [InlineData("https://evil.example@www.roblox.com/games/42")]
    [InlineData("https://www.roblox.com:443/games/42")]
    [InlineData("https://www.roblox.com:8080/games/42")]
    [InlineData("https://localhost/games/42")]
    [InlineData("file:///games/42")]
    [InlineData("https://www.roblox.com/games/43")]
    [InlineData("https://www.roblox.com/games/042")]
    [InlineData("https://www.roblox.com/games/-42")]
    [InlineData("https://www.roblox.com/games/9223372036854775808")]
    [InlineData("https://www.roblox.com/games/%34%32")]
    [InlineData("https://www.roblox.com/games/42?placeId=43")]
    [InlineData("https://www.roblox.com/games/42#game")]
    [InlineData("https://www.roblox.com/games/42/")]
    [InlineData("https://www.roblox.com/games/42/../43")]
    [InlineData("https://www.roblox.com/games/42/%2e%2e")]
    [InlineData("https://www.roblox.com/games/42/a%2fb")]
    [InlineData("https://www.roblox.com/games/42/a%5cb")]
    [InlineData("https://www.roblox.com/games/42/a/b")]
    [InlineData("https://www.roblox.com/games/42/a?tracking=value")]
    [InlineData("https://www.roblox.com/Games/42")]
    [InlineData(" https://www.roblox.com/games/42")]
    public void ForeignOrAmbiguousPageCannotSupplyAProbeContext(string page)
    {
        Assert.False(RobloxHandoffProbePolicy.IsAllowedGamePage(Context,page));
        var result=Inspect(Candidate,page);Assert.Equal(RobloxHandoffProbeOutcome.ContextRejected,result.Outcome);Assert.Null(result.Shape);
    }

    [Fact]
    public void GamePageFormattingIsInvariantAndSlugBudgetIsExplicit()
    {
        var previous=CultureInfo.CurrentCulture;
        try {CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("ar-SA");Assert.Equal(Page,RobloxHandoffProbePolicy.CreateGamePageUri(Context));}
        finally {CultureInfo.CurrentCulture=previous;}
        Assert.True(RobloxHandoffProbePolicy.IsAllowedGamePage(Context,Page+"/"+new string('a',RobloxHandoffProbePolicy.MaximumSlugCharacters)));
        Assert.False(RobloxHandoffProbePolicy.IsAllowedGamePage(Context,Page+"/"+new string('a',RobloxHandoffProbePolicy.MaximumSlugCharacters+1)));
    }

    [Theory]
    [InlineData("https://www.roblox.com",true)]
    [InlineData("https://www.roblox.com/",true)]
    [InlineData("HTTPS://WWW.ROBLOX.COM",true)]
    [InlineData("https://www.roblox.com:443",false)]
    [InlineData("https://www.roblox.com/games/42",false)]
    [InlineData("https://www.roblox.com.evil.example",false)]
    [InlineData("https://roblox.com",false)]
    [InlineData("https://user@www.roblox.com",false)]
    [InlineData("http://www.roblox.com",false)]
    [InlineData("null",false)]
    [InlineData("",false)]
    [InlineData(null,false)]
    public void OriginMustBeExactAndNeverBecomesAccountProof(string? origin,bool allowed)
    {
        Assert.Equal(allowed,RobloxHandoffProbePolicy.IsAllowedOrigin(origin));
        var result=Inspect(Candidate,origin:origin);
        Assert.Equal(allowed?RobloxHandoffProbeOutcome.CapturedAndPrevented:RobloxHandoffProbeOutcome.OriginRejected,result.Outcome);
        Assert.False(result.CanLaunch);Assert.True(result.NativeLaunchPrevented);
    }

    [Fact]
    public void NativeNavigationExceptionRequiresAnAlreadyInstalledCancellingHandler()
    {
        Assert.Equal(RobloxHandoffProbeNavigation.Block,RobloxHandoffProbePolicy.ClassifyNavigation(Context,Candidate,false));
        Assert.Equal(RobloxHandoffProbeNavigation.AllowCancelledRobloxPlayer,RobloxHandoffProbePolicy.ClassifyNavigation(Context,Candidate,true));
        foreach(string external in new[]{"roblox://placeId=42","calculator:///","file:///C:/test.exe","javascript:alert(1)","mailto:person@example.invalid","https://evil.example/"})
        {
            Assert.Equal(RobloxHandoffProbeNavigation.Block,RobloxHandoffProbePolicy.ClassifyNavigation(Context,external,true));
            Assert.Equal(RobloxHandoffProbeOutcome.ExternalSchemeBlocked,Inspect(external).Outcome);
        }
    }

    [Fact]
    public void SanitizedShapeKeepsOnlyFixedLabelsAndCountsWithoutValuesOrUnknownNames()
    {
        const string secret="synthetic-secret-ticket-marker",unknownName="privateUnknownFieldMarker",privateValue="privateUnknownValueMarker";
        string uri=$"roblox-player:1+launchmode:anything+gameinfo:{secret}+launchtime:not-a-time+placelauncherurl:not-a-url+browsertrackerid:opaque+robloxLocale:opaque+gameLocale:opaque+channel:opaque+{unknownName}:{privateValue}";
        var result=Inspect(uri);Assert.Equal(RobloxHandoffProbeOutcome.CapturedAndPrevented,result.Outcome);
        var shape=Assert.IsType<RobloxHandoffProbeShape>(result.Shape);Assert.Equal(9,shape.FieldCount);Assert.Equal(1,shape.UnknownFieldCount);
        Assert.Equal(0,shape.DuplicateFieldCount);Assert.Equal(uri.Length,shape.CharacterCount);
        Assert.Equal((RobloxHandoffKnownFields)255,shape.KnownFields);
        string[] representations=[result.ToString(),shape.ToString(),JsonSerializer.Serialize(result)];
        foreach(string text in representations)
        {
            Assert.DoesNotContain(secret,text);Assert.DoesNotContain(unknownName,text);Assert.DoesNotContain(privateValue,text);
            Assert.DoesNotContain("not-a-url",text);Assert.DoesNotContain("roblox-player:",text);Assert.DoesNotContain(Origin,text);
        }
        Assert.False(result.CanLaunch);Assert.True(result.NativeLaunchPrevented);
        // Deliberately nonsense values still produce only a shape, not a launch URI validation.
        Assert.Equal(RobloxHandoffProbeOutcome.CapturedAndPrevented,Inspect("roblox-player:2+gameinfo:").Outcome);
    }

    [Fact]
    public void DuplicateKnownAndUnknownFieldsAreRejectedWithoutReturningUnknownNames()
    {
        const string unknown="unknownSecretNameMarker";
        var result=Inspect($"roblox-player:1+gameinfo:a+GAMEINFO:b+{unknown}:c+UNKNOWNSECRETNAMEMARKER:d");
        Assert.Equal(RobloxHandoffProbeOutcome.MalformedHandoff,result.Outcome);
        var shape=Assert.IsType<RobloxHandoffProbeShape>(result.Shape);
        Assert.Equal(4,shape.FieldCount);Assert.Equal(2,shape.DuplicateFieldCount);Assert.Equal(2,shape.UnknownFieldCount);
        Assert.Equal(RobloxHandoffKnownFields.GameInfo,shape.KnownFields);Assert.DoesNotContain(unknown,JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("roblox-player:1+gameinfo:%")]
    [InlineData("roblox-player:1+gameinfo:%0")]
    [InlineData("roblox-player:1+gameinfo:%GG")]
    [InlineData("roblox-player:1+gameinfo:%00")]
    [InlineData("roblox-player:1+gameinfo:%0A")]
    [InlineData("roblox-player:1+gameinfo:%7F")]
    [InlineData("roblox-player:1+gameinfo:%C2%85")]
    [InlineData("roblox-player:1+gameinfo:%80")]
    [InlineData("roblox-player:1+gameinfo:%FF")]
    [InlineData("roblox-player:1+gameinfo:%C0%AF")]
    [InlineData("roblox-player:1+gameinfo:%ED%A0%80")]
    [InlineData("roblox-player:1+gameinfo:%F4%90%80%80")]
    [InlineData("roblox-player:1+gameinfo:%E2%80%AE")]
    [InlineData("roblox-player:1+gameinfo:a\nb")]
    [InlineData("roblox-player:1+gameinfo:a b")]
    [InlineData("roblox-player:1++gameinfo:a")]
    [InlineData("roblox-player:1+gameinfo:a+")]
    [InlineData("roblox-player:1+gameinfo")]
    [InlineData("roblox-player:1+%67ameinfo:a")]
    [InlineData("roblox-player:abc+gameinfo:a")]
    public void UnsafeTextAndMalformedStructuralFieldsNeverProduceAUsableHandoff(string uri)
    {
        var result=Inspect(uri);Assert.Equal(RobloxHandoffProbeOutcome.MalformedHandoff,result.Outcome);
        Assert.False(result.CanLaunch);Assert.True(result.NativeLaunchPrevented);Assert.Null(result.Shape);
    }

    [Fact]
    public void MalformedUtf16IsRejectedWithoutExceptionOrEcho()
    {
        foreach(string suffix in new[]{new string((char)0xd800,1),new string((char)0xdc00,1),new string(new[]{(char)0xd800,'x'})})
        {
            var result=Inspect("roblox-player:1+gameinfo:"+suffix);Assert.Equal(RobloxHandoffProbeOutcome.MalformedHandoff,result.Outcome);
            Assert.Equal(RobloxHandoffProbeNavigation.Block,RobloxHandoffProbePolicy.ClassifyNavigation(Context,"roblox-player:1+gameinfo:"+suffix,true));
        }
        Assert.Equal(RobloxHandoffProbeOutcome.CapturedAndPrevented,Inspect("roblox-player:1+gameinfo:%F0%9F%98%80").Outcome);
        Assert.Equal(RobloxHandoffProbeOutcome.CapturedAndPrevented,Inspect("roblox-player:1+gameinfo:"+char.ConvertFromUtf32(0x1f600)).Outcome);
    }

    [Fact]
    public void CharacterByteFieldAndNameBoundsAreEnforcedAtTheirLimits()
    {
        const string prefix="roblox-player:1+gameinfo:";
        string exact=prefix+new string('a',RobloxHandoffProbePolicy.MaximumUriCharacters-prefix.Length);
        Assert.Equal(RobloxHandoffProbeOutcome.CapturedAndPrevented,Inspect(exact).Outcome);
        Assert.Equal(RobloxHandoffProbeOutcome.MalformedHandoff,Inspect(exact+"a").Outcome);
        Assert.Equal(RobloxHandoffProbeOutcome.MalformedHandoff,Inspect(prefix+new string('é',RobloxHandoffProbePolicy.MaximumUriUtf8Bytes/2)).Outcome);
        string fields="roblox-player:1+"+string.Join("+",Enumerable.Range(0,RobloxHandoffProbePolicy.MaximumFields).Select(i=>$"unknown{i}:opaque"));
        Assert.Equal(RobloxHandoffProbePolicy.MaximumFields,Inspect(fields).Shape!.UnknownFieldCount);
        Assert.Equal(RobloxHandoffProbeOutcome.MalformedHandoff,Inspect(fields+"+overflow:opaque").Outcome);
        Assert.Equal(RobloxHandoffProbeOutcome.CapturedAndPrevented,Inspect("roblox-player:1+"+new string('a',RobloxHandoffProbePolicy.MaximumFieldNameCharacters)+":opaque").Outcome);
        Assert.Equal(RobloxHandoffProbeOutcome.MalformedHandoff,Inspect("roblox-player:1+"+new string('a',RobloxHandoffProbePolicy.MaximumFieldNameCharacters+1)+":opaque").Outcome);
    }

    [Fact]
    public void AllClosedOutcomesPreventLaunchAndInvalidOutcomeCannotCarryArbitraryText()
    {
        var shape=Inspect(Candidate).Shape!;
        foreach(var outcome in Enum.GetValues<RobloxHandoffProbeOutcome>())
        {
            var result=new RobloxHandoffProbeResult(outcome,outcome==RobloxHandoffProbeOutcome.CapturedAndPrevented?shape:null);
            Assert.False(result.CanLaunch);Assert.True(result.NativeLaunchPrevented);
        }
        Assert.Throws<ArgumentException>(()=>new RobloxHandoffProbeResult((RobloxHandoffProbeOutcome)int.MaxValue));
        Assert.Throws<ArgumentException>(()=>new RobloxHandoffProbeResult(RobloxHandoffProbeOutcome.CapturedAndPrevented));
    }

    [Theory]
    [InlineData(null)] [InlineData(false)] [InlineData(true)]
    public void GestureIsOnlyAClosedFlagAndNeverChangesLaunchAuthority(bool? gesture)
    {
        var inspected=Inspect(Candidate);var result=new RobloxHandoffProbeResult(inspected.Outcome,inspected.Shape,gesture);
        Assert.Equal(gesture,result.UserInitiated);Assert.False(result.CanLaunch);Assert.True(result.NativeLaunchPrevented);
        string json=JsonSerializer.Serialize(result);Assert.Contains("UserInitiated",json);Assert.DoesNotContain("synthetic-ticket",json);
        Assert.DoesNotContain("roblox-player:",json);Assert.DoesNotContain(Origin,json);
    }

    [Fact]
    public void CaptureOwnsOnlyUpdatedCredentialAndNeverSerializesIt()
    {
        const string inputSecret="synthetic-input-session",updatedSecret="synthetic-current-session";
        using var input=new RobloxSessionCredential(inputSecret);var updated=new RobloxSessionCredential(updatedSecret);
        var capture=new RobloxHandoffProbeCapture(Inspect(Candidate),updated);
        Assert.Same(updated,capture.UpdatedCredential);Assert.DoesNotContain(updatedSecret,capture.ToString());
        string json=JsonSerializer.Serialize(capture);Assert.DoesNotContain(inputSecret,json);Assert.DoesNotContain(updatedSecret,json);
        Assert.DoesNotContain("UpdatedCredential",json);Assert.DoesNotContain("ExpiresUtc",json);
        capture.Dispose();capture.Dispose();Assert.Null(capture.UpdatedCredential);
        Assert.Throws<ObjectDisposedException>(()=>updated.CopyValue());Assert.Equal(inputSecret,input.CopyValue());
        using var failed=new RobloxHandoffProbeCapture(new(RobloxHandoffProbeOutcome.Failed));Assert.Null(failed.UpdatedCredential);
        Assert.False(failed.Result.CanLaunch);Assert.True(failed.Result.NativeLaunchPrevented);
    }
}
