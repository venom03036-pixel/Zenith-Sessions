using System.Net;
using System.Net.Http;
using System.Text;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class SignedInServerClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ++Calls, ct);
    }
    private static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static readonly string Job = "8c782b5d-2121-4bfa-9dca-435b63316b05";
    private static string Public(int? ping = null, string extra = "") => $$"""{"id":"{{Job}}","playing":2,"maxPlayers":10{{(ping is null ? "" : ",\"ping\":" + ping)}}{{extra}}} """;
    private static string Private(string owner = "456", string name = "\"Owned server\"", string playing = "2")
        => $$"""{"vipServerId":99,"owner":{"id":{{owner}}},"name":{{name}},"playing":{{playing}},"maxPlayers":10,"accessCode":"do-not-save-access-code"} """;
    private static string Page(string row, string cursor = "null") => $$"""{"data":[{{row}}],"nextPageCursor":{{cursor}}} """;
    [Theory]
    [InlineData("\"not-a-number\"", "\"Name\"", "2")]
    [InlineData("456", "null", "2")]
    [InlineData("456", "\"Name\"", "\"secret-number\"")]
    public async Task WrongPrimitiveTypesBecomeUnavailableWithoutThrowing(string owner, string name, string playing)
    {
        using var client = new RobloxSignedInServerClient(new Handler((_,_,_) => Task.FromResult(Reply(Page(Private(owner,name,playing))))));
        using var cookie = new RobloxSessionCredential("synthetic-server-cookie");
        using var result = await client.BrowseAsync(new(123,SignedInServerOrder.OwnedPrivate),456,cookie,default);
        Assert.Equal(RobloxAccountOutcome.Unavailable,result.Outcome); Assert.Null(result.Page);
    }
    [Theory]
    [InlineData(SignedInServerOrder.Recommended)]
    [InlineData(SignedInServerOrder.BestLatency)]
    [InlineData(SignedInServerOrder.OwnedPrivate)]
    public async Task OnlyExplicitFixedSignedInEndpointsReceiveAnHonestCookieRequest(SignedInServerOrder order)
    {
        var handler = new Handler((request,_,_) =>
        {
            Assert.Equal("games.roblox.com",request.RequestUri!.Host); Assert.Equal(HttpMethod.Get,request.Method);
            Assert.Contains("cursor=a%2Bb%2Fc",request.RequestUri.AbsoluteUri); Assert.Contains("limit=25",request.RequestUri.Query);
            Assert.Equal(".ROBLOSECURITY=synthetic-server-cookie",request.Headers.GetValues("Cookie").Single());
            Assert.Equal("ZenithSessions/1.0",request.Headers.UserAgent.ToString()); Assert.False(request.Headers.Contains("Origin")); Assert.Null(request.Headers.Referrer);
            if(order!=SignedInServerOrder.OwnedPrivate) Assert.Contains("orderBy="+order,request.RequestUri.Query);
            return Task.FromResult(Reply(Page(order==SignedInServerOrder.OwnedPrivate?Private():Public(24,",\"friends\":2"))));
        });
        using var client = new RobloxSignedInServerClient(handler); using var cookie = new RobloxSessionCredential("synthetic-server-cookie");
        using var result = await client.BrowseAsync(new(123,order,Cursor:"a+b/c"),456,cookie,default);
        Assert.Equal(RobloxAccountOutcome.Success,result.Outcome); Assert.Equal(1,handler.Calls);
        Assert.DoesNotContain("do-not-save",result.Page!.ToString());
        if(order!=SignedInServerOrder.OwnedPrivate) Assert.Equal(2,result.Page.PublicServers.Single().FriendCount);
    }
    [Fact]
    public async Task PrivateListShowsOnlyOwnedRowsAndNeverTurnsAccessCodeIntoPublicJobOrLinkCode()
    {
        using var client = new RobloxSignedInServerClient(new Handler((_,_,_) => Task.FromResult(Reply(Page(Private("777"))))));
        using var cookie = new RobloxSessionCredential("synthetic-server-cookie");
        using var result = await client.BrowseAsync(new(123,SignedInServerOrder.OwnedPrivate),456,cookie,default);
        Assert.Equal(RobloxAccountOutcome.Success,result.Outcome); Assert.Empty(result.Page!.PrivateServers); Assert.Empty(result.Page.PublicServers);
    }
    [Theory]
    [InlineData(",\"accessCode\":\"not-public\"")]
    [InlineData(",\"vipServerId\":99")]
    [InlineData(",\"friends\":-1")]
    public async Task PublicRowsKeepTheAnonymousParsersStrictRejectionRules(string extra)
    {
        using var client = new RobloxSignedInServerClient(new Handler((_,_,_) => Task.FromResult(Reply(Page(Public(extra:extra))))));
        using var cookie = new RobloxSessionCredential("synthetic-server-cookie");
        using var result = await client.BrowseAsync(new(123,SignedInServerOrder.Recommended),456,cookie,default);
        Assert.Equal(RobloxAccountOutcome.Unavailable,result.Outcome);
    }
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, RobloxAccountOutcome.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, RobloxAccountOutcome.RateLimited)]
    [InlineData(HttpStatusCode.Unauthorized, RobloxAccountOutcome.SignInRequired)]
    [InlineData(HttpStatusCode.Redirect, RobloxAccountOutcome.Unavailable)]
    public async Task RefusalsNeverRetryAutomatically(HttpStatusCode status, RobloxAccountOutcome expected)
    {
        var handler = new Handler((_,_,_)=>Task.FromResult(Reply("{}",status)));
        using var client = new RobloxSignedInServerClient(handler); using var cookie = new RobloxSessionCredential("synthetic-server-cookie");
        using var result = await client.BrowseAsync(new(123,SignedInServerOrder.Recommended),456,cookie,default);
        Assert.Equal(expected,result.Outcome); Assert.Equal(1,handler.Calls);
    }
    [Fact]
    public async Task RenameSendsOnlyNameAndRetriesOneExplicitCsrfRefusal()
    {
        var handler = new Handler(async(request,call,ct)=>
        {
            Assert.Equal(HttpMethod.Patch,request.Method); Assert.Equal("https://games.roblox.com/v1/vip-servers/99",request.RequestUri!.AbsoluteUri);
            Assert.Equal("{\"name\":\"New name\"}",await request.Content!.ReadAsStringAsync(ct));
            var response = Reply("{}",call==1?HttpStatusCode.Forbidden:HttpStatusCode.OK);
            if(call==1) response.Headers.Add("x-csrf-token","synthetic-csrf"); else Assert.Equal("synthetic-csrf",request.Headers.GetValues("x-csrf-token").Single());
            return response;
        });
        using var client = new RobloxSignedInServerClient(handler); using var cookie = new RobloxSessionCredential("synthetic-server-cookie");
        using var result = await client.RenameAsync(new(99,123,456,"Old",0,10),"New name",cookie,default);
        Assert.Equal(RobloxAccountOutcome.Success,result.Outcome); Assert.Equal(2,handler.Calls);
    }
    [Fact]
    public async Task MalformedBodyStillTransfersRotationForVerification()
    {
        using var client = new RobloxSignedInServerClient(new Handler((_,_,_)=>
        { var response=Reply("{broken");response.Headers.Add("Set-Cookie",".ROBLOSECURITY=synthetic-rotation; Domain=.roblox.com");return Task.FromResult(response); }));
        using var cookie = new RobloxSessionCredential("synthetic-server-cookie");
        using var result = await client.BrowseAsync(new(123,SignedInServerOrder.Recommended),456,cookie,default);
        Assert.Equal(RobloxAccountOutcome.Unavailable,result.Outcome);Assert.Equal("synthetic-rotation",result.Rotation!.CopyValue());
    }
    [Theory]
    [InlineData("{\"data\":[],\"Data\":[],\"nextPageCursor\":null}")]
    [InlineData("{\"data\":[{}],\"nextPageCursor\":null}")]
    [InlineData("{\"data\":[],\"nextPageCursor\":false}")]
    [InlineData("{\"data\":[],\"nextPageCursor\":null,\"gameJoinRestricted\":\"false\"}")]
    public async Task PrivatePagesRejectMissingAmbiguousAndUnsupportedFields(string body)
    {
        using var client=new RobloxSignedInServerClient(new Handler((_,_,_)=>Task.FromResult(Reply(body))));using var cookie=new RobloxSessionCredential("synthetic-cookie");
        using var result=await client.BrowseAsync(new(123,SignedInServerOrder.OwnedPrivate),456,cookie,default);
        Assert.Equal(RobloxAccountOutcome.Unavailable,result.Outcome);
    }
    [Fact]
    public async Task LongProviderCooldownIsNotShortenedAndSuccessfulLastRequestCarriesItsWindow()
    {
        using var client=new RobloxSignedInServerClient(new Handler((_,_,_)=>
        {var response=Reply(Page(Public()));response.Headers.Add("x-ratelimit-remaining","0");response.Headers.Add("x-ratelimit-reset","172800");return Task.FromResult(response);}));
        using var cookie=new RobloxSessionCredential("synthetic-cookie");using var result=await client.BrowseAsync(new(123,SignedInServerOrder.Recommended),456,cookie,default);
        Assert.Equal(TimeSpan.FromDays(2),result.RetryAfter);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenameNeverRetriesChallengeOrUnverifiedCookieRotation(bool challenge)
    {
        var handler=new Handler((_,_,_)=>
        {var response=Reply("{}",HttpStatusCode.Forbidden);response.Headers.Add("x-csrf-token","csrf");if(challenge)response.Headers.Add("rblx-challenge-id","opaque");else response.Headers.Add("Set-Cookie",".ROBLOSECURITY=synthetic-rotation; Domain=.roblox.com");return Task.FromResult(response);});
        using var client=new RobloxSignedInServerClient(handler);using var cookie=new RobloxSessionCredential("synthetic-cookie");using var result=await client.RenameAsync(new(99,123,456,"Old",1,10),"New",cookie,default);
        Assert.Equal(1,handler.Calls);Assert.Equal(challenge?RobloxAccountOutcome.Challenge:RobloxAccountOutcome.Unavailable,result.Outcome);
        Assert.Equal(!challenge,result.Rotation is not null);
    }
}
