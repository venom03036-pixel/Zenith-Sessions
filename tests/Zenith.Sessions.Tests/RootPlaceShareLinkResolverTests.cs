using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// A share link whose payload names only a universe opens that universe's root place, like Roblox's own web page.
// The root place comes from the anonymous public games listing; a restricted, missing or failed lookup is Unavailable.
public sealed class RootPlaceShareLinkResolverTests
{
    private static readonly Guid Profile=Guid.Parse("22222222-2222-4222-8222-222222222222");
    private const string ShareCode="4b9c7e0ae7b2f84bb7a7d24f6d0f6c2e",LinkCode="98765432109876543210";

    [Fact]
    public async Task UniverseOnlyExperienceUsesTheRootPlace()
    {
        var inner=new Inner(()=>new(RobloxShareLinkOutcome.Experience,null,6));var games=new Games(_=>Listed(6,100));
        using var link=Link(RobloxShareLinkType.ExperienceDetails);
        using var resolution=await new RootPlaceShareLinkResolver(inner,games).ResolveShareLinkAsync(Profile,link,default);
        Assert.Equal(RobloxShareLinkOutcome.Experience,resolution.Outcome);Assert.Equal(100,resolution.PlaceId);Assert.Equal(6,resolution.UniverseId);
        Assert.Equal(new long[]{6},Assert.Single(games.Requests));Assert.Equal(Profile,inner.LastProfile);Assert.Same(link,inner.LastLink);
    }

    [Fact]
    public async Task UniverseOnlyPrivateServerKeepsItsCodeAndUsesTheRootPlace()
    {
        var code=new ProtectedPrivateServerCode(LinkCode);
        var inner=new Inner(()=>new(RobloxShareLinkOutcome.PrivateServer,null,6,null,code));var games=new Games(_=>Listed(6,100));
        using var link=Link(RobloxShareLinkType.Server);
        using var resolution=await new RootPlaceShareLinkResolver(inner,games).ResolveShareLinkAsync(Profile,link,default);
        Assert.Equal(RobloxShareLinkOutcome.PrivateServer,resolution.Outcome);Assert.Equal(100,resolution.PlaceId);Assert.Equal(6,resolution.UniverseId);
        Assert.Null(resolution.OwnerUserId);Assert.Same(code,resolution.PrivateServerCode);
        string revealed="";code.Reveal(span=>revealed=new string(span));Assert.Equal(LinkCode,revealed); // Moved, not disposed.
        Assert.DoesNotContain("9876543210987",resolution.ToString(),StringComparison.Ordinal);
        resolution.Dispose();Assert.Throws<ObjectDisposedException>(()=>code.Reveal(_=>{}));
    }

    public static TheoryData<string> FailedLookups=>new(){"restricted","other universe","unknown","deferred","failure","no directory"};

    [Theory]
    [MemberData(nameof(FailedLookups))]
    public async Task ARestrictedMissingOrFailedLookupIsUnavailableAndDisposesTheCode(string lookup)
    {
        var code=new ProtectedPrivateServerCode(LinkCode);
        var inner=new Inner(()=>new(RobloxShareLinkOutcome.PrivateServer,null,6,7,code));
        Games? games=lookup switch
        {
            "restricted"=>new(_=>new(PublicDirectoryState.Success,Array.Empty<PublicRobloxGame>(),"Synthetic public information")), // Restricted universes are left out.
            "other universe"=>new(_=>Listed(9,100)),
            "unknown"=>new(_=>new(PublicDirectoryState.Unknown,null,"Synthetic unknown")),
            "deferred"=>new(_=>new(PublicDirectoryState.Deferred,null,"Synthetic timeout",TimeSpan.FromSeconds(5))),
            "failure"=>new(_=>new(PublicDirectoryState.Failure,null,"Synthetic failure")),
            _=>null
        };
        using var link=Link(RobloxShareLinkType.Server);
        using var resolution=await new RootPlaceShareLinkResolver(inner,games).ResolveShareLinkAsync(Profile,link,default);
        Assert.Equal(RobloxShareLinkOutcome.Unavailable,resolution.Outcome);Assert.Null(resolution.PlaceId);Assert.Null(resolution.PrivateServerCode);
        Assert.Throws<ObjectDisposedException>(()=>code.Reveal(_=>{})); // Never guessed, and the unused code is zeroed.
    }

    [Fact]
    public async Task AKnownPlaceOrAnyOtherOutcomeNeverLooksUp()
    {
        var games=new Games(_=>throw new InvalidOperationException("No lookup expected."));
        foreach(var made in new Func<RobloxShareLinkResolution>[]
        {
            ()=>new(RobloxShareLinkOutcome.Experience,5,6),
            ()=>new(RobloxShareLinkOutcome.Expired),()=>new(RobloxShareLinkOutcome.Invalid),()=>new(RobloxShareLinkOutcome.SignInRequired),
            ()=>new(RobloxShareLinkOutcome.Unavailable),()=>new(RobloxShareLinkOutcome.Cancelled),()=>new(RobloxShareLinkOutcome.Busy)
        })
        {
            var expected=made();var inner=new Inner(()=>expected);
            using var link=Link(RobloxShareLinkType.ExperienceDetails);
            using var resolution=await new RootPlaceShareLinkResolver(inner,games).ResolveShareLinkAsync(Profile,link,default);
            Assert.Same(expected,resolution);
        }
        Assert.Empty(games.Requests);
    }

    [Fact]
    public async Task CancellingTheLookupDisposesTheCodeAndPropagates()
    {
        var code=new ProtectedPrivateServerCode(LinkCode);using var stop=new CancellationTokenSource();
        var inner=new Inner(()=>new(RobloxShareLinkOutcome.PrivateServer,null,6,null,code));
        var games=new Games(ct=>{stop.Cancel();ct.ThrowIfCancellationRequested();return Listed(6,100);});
        using var link=Link(RobloxShareLinkType.Server);
        var cancelled=await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new RootPlaceShareLinkResolver(inner,games).ResolveShareLinkAsync(Profile,link,stop.Token));
        Assert.Throws<ObjectDisposedException>(()=>code.Reveal(_=>{}));Assert.Equal(stop.Token,games.LastToken);
        Assert.DoesNotContain(LinkCode,cancelled.ToString(),StringComparison.Ordinal);Assert.DoesNotContain(ShareCode,cancelled.ToString(),StringComparison.Ordinal);
    }

    private static RobloxShareLink Link(RobloxShareLinkType type)=>new(new ProtectedShareCode(ShareCode),type);
    private static PublicDirectoryResult<IReadOnlyList<PublicRobloxGame>> Listed(long universeId,long rootPlaceId)
        =>new(PublicDirectoryState.Success,new[]{new PublicRobloxGame(rootPlaceId,universeId,rootPlaceId,"Synthetic game",7,"Creator",PublicRobloxCreatorKind.User,3,20,DateTimeOffset.UnixEpoch)},"Synthetic public information");

    private sealed class Inner(Func<RobloxShareLinkResolution> respond):IRobloxShareLinkResolver
    {
        public Guid LastProfile;public RobloxShareLink? LastLink;
        public Task<RobloxShareLinkResolution> ResolveShareLinkAsync(Guid profileId,RobloxShareLink link,CancellationToken ct)
        {LastProfile=profileId;LastLink=link;return Task.FromResult(respond());}
    }
    private sealed class Games(Func<CancellationToken,PublicDirectoryResult<IReadOnlyList<PublicRobloxGame>>> respond):IPublicRobloxGameBatchDirectory
    {
        public readonly List<long[]> Requests=[];public CancellationToken LastToken;
        public Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxGame>>> LookupGamesAsync(IReadOnlyList<long> universeIds,CancellationToken ct)
        {Requests.Add(universeIds.ToArray());LastToken=ct;return Task.FromResult(respond(ct));}
    }
}
