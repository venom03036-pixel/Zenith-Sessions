using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class RobloxFriendshipBatchTests
{
    private sealed class Service(Func<RobloxAccountContext,RobloxAccountRead,RobloxAccountContext?,int,RobloxAccountResult> reply) : IRobloxAccountDataService
    {
        public readonly List<(RobloxAccountContext Account,RobloxAccountRead Kind,RobloxAccountContext? Target)> Calls=[];
        public Task<RobloxAccountResult> ReadAccountAsync(RobloxAccountContext account,RobloxAccountRead kind,RobloxAccountContext? target,CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Calls.Add((account,kind,target)); return Task.FromResult(reply(account,kind,target,Calls.Count)); }
    }
    private static RobloxAccountContext Account(long id)=>new(Guid.NewGuid(),id,1);
    [Fact]
    public async Task ConfirmationPinsAllPairsAndCannotBeReplayed()
    {
        var a=Account(11); var b=Account(22);var c=Account(33);
        var review=new RobloxFriendshipReview([a,b,c]); Assert.Equal(3,review.Pairs.Count);
        var service=new Service((actor,_,_,_)=>new(RobloxAccountOutcome.Success,actor with{Generation=actor.Generation+2},new(Friendship:RobloxFriendshipStatus.Friends)));
        var results=await new RobloxFriendshipBatch(service).RunConfirmedAsync(review,default);
        Assert.All(results,r=>Assert.Equal("Already friends",r.Status)); Assert.All(service.Calls,call=>Assert.Equal(RobloxAccountRead.FriendshipStatus,call.Kind));
        Assert.Equal(a.Generation+2,service.Calls[1].Account.Generation);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new RobloxFriendshipBatch(service).RunConfirmedAsync(review,default)); Assert.Equal(3,service.Calls.Count);
    }
    [Theory]
    [InlineData(RobloxFriendshipStatus.NotFriends,4)]
    [InlineData(RobloxFriendshipStatus.RequestSent,3)]
    [InlineData(RobloxFriendshipStatus.RequestReceived,3)]
    public async Task OnlyReviewedAccountsSendAcceptAndConfirmWithTheirOwnUpdatedContext(RobloxFriendshipStatus initial,int count)
    {
        var a=Account(11);var b=Account(22);var review=new RobloxFriendshipReview([a,b]);
        var service=new Service((actor,kind,_,call)=>new(RobloxAccountOutcome.Success,actor with{Generation=actor.Generation+2},
            kind==RobloxAccountRead.FriendshipStatus?new(Friendship:call==1?initial:RobloxFriendshipStatus.Friends):new()));
        var results=await new RobloxFriendshipBatch(service).RunConfirmedAsync(review,default);
        Assert.Equal("Friends confirmed",Assert.Single(results).Status);Assert.Equal(count,service.Calls.Count);
        var accept=Assert.Single(service.Calls.Where(call=>call.Kind==RobloxAccountRead.AcceptFriendship));
        Assert.Equal(initial==RobloxFriendshipStatus.RequestReceived?a.ProfileId:b.ProfileId,accept.Account.ProfileId);
        if(initial==RobloxFriendshipStatus.NotFriends) Assert.Single(service.Calls.Where(call=>call.Kind==RobloxAccountRead.RequestFriendship));
        else Assert.DoesNotContain(service.Calls,call=>call.Kind==RobloxAccountRead.RequestFriendship);
    }
    [Theory]
    [InlineData(RobloxAccountOutcome.Challenge)]
    [InlineData(RobloxAccountOutcome.RateLimited)]
    [InlineData(RobloxAccountOutcome.Unavailable)]
    public async Task FirstRefusalStopsEveryRemainingPair(RobloxAccountOutcome outcome)
    {
        var review=new RobloxFriendshipReview([Account(11),Account(22),Account(33)]);
        var service=new Service((actor,_,_,_)=>new(outcome,actor));
        var results=await new RobloxFriendshipBatch(service).RunConfirmedAsync(review,default);
        Assert.Single(service.Calls); Assert.StartsWith("Stopped",results[0].Status);Assert.All(results.Skip(1),r=>Assert.Equal("Not attempted",r.Status));
    }
    [Fact]
    public async Task UnknownStatusNeverBecomesNotFriends()
    {
        var service=new Service((actor,_,_,_)=>new(RobloxAccountOutcome.Success,actor,new()));
        var results=await new RobloxFriendshipBatch(service).RunConfirmedAsync(new([Account(11),Account(22)]),default);
        Assert.Single(service.Calls);Assert.Contains("unavailable",Assert.Single(results).Status);
    }
    [Fact]
    public async Task CancelDuringGapStopsBeforeTheNextMutation()
    {
        using var stop=new CancellationTokenSource();
        var service=new Service((actor,_,_,_)=>{ stop.Cancel();return new(RobloxAccountOutcome.Success,actor,new(Friendship:RobloxFriendshipStatus.NotFriends));});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new RobloxFriendshipBatch(service).RunConfirmedAsync(new([Account(11),Account(22)]),stop.Token));
        Assert.Single(service.Calls);
    }
    [Fact]
    public void ReviewsRefuseDuplicatePlayersAndUnboundedTargets()
    {
        Assert.Throws<ArgumentException>(()=>new RobloxFriendshipReview([Account(11),Account(11)]));
        Assert.Throws<ArgumentException>(()=>new RobloxFriendshipReview([Account(11)]));
        Assert.Throws<ArgumentException>(()=>new RobloxFriendshipReview(Enumerable.Range(1,21).Select(i=>Account(i)).ToArray()));
        Assert.Equal(190,new RobloxFriendshipReview(Enumerable.Range(1,20).Select(i=>Account(i)).ToArray()).Pairs.Count);
    }
}
