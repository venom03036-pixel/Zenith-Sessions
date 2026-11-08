using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class ClientReleaseTests
{
    // Field names and values as recorded from the public endpoint (RoALT research fixture). No test reaches the network.
    private const string Recorded = "{\"version\":\"0.744.0.7441080\",\"clientVersionUpload\":\"version-9ac8595712b447a7\",\"bootstrapperVersion\":\"\"}";
    private const string ReleaseUri = "https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer";
    private static readonly PublicRobloxClientRelease Latest = new("0.744.0.7441080", "version-9ac8595712b447a7", DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task CurrentReleaseIsOneAnonymousReadOfThePublicVersionService()
    {
        using var handler = new FakeHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(ReleaseUri, request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Single(request.Headers);
            Assert.Equal("application/json", request.Headers.Accept.Single().MediaType);
            Assert.Null(request.Content);
            return Task.FromResult(Json(Recorded));
        });
        using var http = new HttpClient(handler);
        IPublicRobloxClientReleaseDirectory directory = new RobloxPublicDirectory(http);
        var result = await directory.GetCurrentWindowsPlayerReleaseAsync(default);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        var release = Assert.IsType<PublicRobloxClientRelease>(result.Value);
        Assert.Equal("0.744.0.7441080", release.Version);
        Assert.Equal("version-9ac8595712b447a7", release.ClientVersionUpload);
        Assert.True(RobloxClientRelease.IsValid(release));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("{\"version\":\"0.744.0.7441080\",\"clientVersionUpload\":\"version-9AC8595712B447A7\"}")]
    [InlineData("{\"version\":\"0.744.0.7441080\",\"clientVersionUpload\":\"version-9ac8595712b447a\"}")]
    [InlineData("{\"version\":\"0.744.0.7441080\",\"clientVersionUpload\":\"version-9ac8595712b447a70\"}")]
    [InlineData("{\"version\":\"0.744.0.7441080\",\"clientVersionUpload\":\"version-9ac8595712b447g7\"}")]
    [InlineData("{\"version\":\"0.744.0.7441080\",\"clientVersionUpload\":\"..\\\\9ac8595712b447a7\"}")]
    [InlineData("{\"version\":\"0.744.0.7441080\",\"clientVersionUpload\":\"\"}")]
    [InlineData("{\"version\":\"0.744.0\",\"clientVersionUpload\":\"version-9ac8595712b447a7\"}")]
    [InlineData("{\"version\":\"0.744.0.7441080.1\",\"clientVersionUpload\":\"version-9ac8595712b447a7\"}")]
    [InlineData("{\"version\":\"0.744.0.7441080 \",\"clientVersionUpload\":\"version-9ac8595712b447a7\"}")]
    [InlineData("{\"version\":\"0.744.-1.7441080\",\"clientVersionUpload\":\"version-9ac8595712b447a7\"}")]
    [InlineData("{\"version\":\"0.744.0.99999999999\",\"clientVersionUpload\":\"version-9ac8595712b447a7\"}")]
    [InlineData("{\"version\":744,\"clientVersionUpload\":\"version-9ac8595712b447a7\"}")]
    [InlineData("{\"clientVersionUpload\":\"version-9ac8595712b447a7\"}")]
    [InlineData("{\"version\":\"0.744.0.7441080\"}")]
    [InlineData("{\"version\":\"0.744.0.7441080\",\"clientVersionUpload\":\"version-9ac8595712b447a7\",\"clientVersionUpload\":\"version-0000000000000000\"}")]
    [InlineData("[]")]
    public async Task AMalformedReleaseIsAFailureNeverAVersion(string body)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Json(body)));
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).GetCurrentWindowsPlayerReleaseAsync(default);
        Assert.Equal(PublicDirectoryState.Failure, result.State);
        Assert.Null(result.Value);
        Assert.DoesNotContain("9ac8595712b447a7", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOversizedReleaseBodyIsRejected(bool unknownLength)
    {
        // A well-formed release padded past the small body cap is refused whether or not it declares its length.
        string padded = Recorded.Insert(1, "\"padding\":\"" + new string('a', RobloxPublicDirectory.MaximumClientReleaseBytes) + "\",");
        using var handler = new FakeHandler((_, _) => Task.FromResult(Response(Encoding.UTF8.GetBytes(padded), unknownLength)));
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).GetCurrentWindowsPlayerReleaseAsync(default);
        Assert.Equal(PublicDirectoryState.Failure, result.State);
        Assert.Null(result.Value);
    }

    [Fact]
    public void TheVersionFolderNamedByTheUploadDecidesTheMatch()
    {
        const string root = "C:\\Users\\Synthetic\\AppData\\Local\\Roblox\\Versions\\";
        Assert.Equal(ClientReleaseMatch.Current, RobloxClientRelease.Compare(Player(root + "version-9ac8595712b447a7\\RobloxPlayerBeta.exe", null), Latest));
        // Windows folder names are case-insensitive; the folder (the deployment itself) outranks the file's own version claim.
        Assert.Equal(ClientReleaseMatch.Current, RobloxClientRelease.Compare(Player(root + "VERSION-9AC8595712B447A7\\RobloxPlayer.exe", "0.700.0.1"), Latest));
        Assert.Equal(ClientReleaseMatch.NotCurrent, RobloxClientRelease.Compare(Player(root + "version-117ad38284844374\\RobloxPlayerBeta.exe", "0.744.0.35512"), Latest));
    }

    [Fact]
    public void WithoutAnUploadFolderTheFileVersionDecidesAllowingForItsSixteenBitRevision()
    {
        // Windows keeps 16 bits per fixed file-version part, so build 0.744.0.7441080 reads back as 0.744.0.35512 (7441080 mod 65536).
        const string player = "C:\\Users\\Synthetic\\AppData\\Local\\Bloxstrap\\Roblox\\Player\\RobloxPlayerBeta.exe";
        Assert.Equal(ClientReleaseMatch.Current, RobloxClientRelease.Compare(Player(player, "0.744.0.35512"), Latest));
        Assert.Equal(ClientReleaseMatch.Current, RobloxClientRelease.Compare(Player(player, "0.744.0.7441080"), Latest));
        Assert.Equal(ClientReleaseMatch.NotCurrent, RobloxClientRelease.Compare(Player(player, "0.744.0.35509"), Latest));
        Assert.Equal(ClientReleaseMatch.NotCurrent, RobloxClientRelease.Compare(Player(player, "0.744.0.7441077"), Latest));
        Assert.Equal(ClientReleaseMatch.NotCurrent, RobloxClientRelease.Compare(Player(player, "0.745.0.35512"), Latest));
        Assert.Equal(ClientReleaseMatch.NotCurrent, RobloxClientRelease.Compare(Player(player, "0.744.1.35512"), Latest));
        Assert.Equal(ClientReleaseMatch.NotCompared, RobloxClientRelease.Compare(Player(player, null), Latest));
        Assert.Equal(ClientReleaseMatch.NotCompared, RobloxClientRelease.Compare(Player(player, "0, 744, 0, 7441080"), Latest));
        // A version folder whose name is not an upload name is no evidence either way; the file version decides.
        const string fixture = "C:\\Users\\Synthetic\\AppData\\Local\\Roblox\\Versions\\version-fixture\\RobloxPlayerBeta.exe";
        Assert.Equal(ClientReleaseMatch.Current, RobloxClientRelease.Compare(Player(fixture, "0.744.0.35512"), Latest));
        // An upload-shaped folder that is not under Versions is not a deployment folder.
        const string loose = "C:\\Users\\Synthetic\\Downloads\\version-9ac8595712b447a7\\RobloxPlayerBeta.exe";
        Assert.Equal(ClientReleaseMatch.NotCurrent, RobloxClientRelease.Compare(Player(loose, "0.744.0.35509"), Latest));
    }

    [Fact]
    public void OnlyAPresentRobloxPlayerIsComparedAndOnlyWithAValidRelease()
    {
        const string current = "C:\\Users\\Synthetic\\AppData\\Local\\Roblox\\Versions\\version-9ac8595712b447a7\\";
        // The launcher installs and updates Roblox; a bootstrapper reports its own version. Neither is the player.
        Assert.Equal(ClientReleaseMatch.NotCompared, RobloxClientRelease.Compare(Player(current + "RobloxPlayerLauncher.exe", "0.744.0.35512"), Latest));
        Assert.Equal(ClientReleaseMatch.NotCompared, RobloxClientRelease.Compare(
            Player("C:\\Users\\Synthetic\\AppData\\Local\\Bloxstrap\\Bloxstrap.exe", "0.744.0.35512", ClientInstallationProduct.Bloxstrap), Latest));
        Assert.Equal(ClientReleaseMatch.NotCompared, RobloxClientRelease.Compare(Player(current + "RobloxPlayerBeta.exe", null, state: ClientInstallationFileState.Missing), Latest));
        Assert.Equal(ClientReleaseMatch.NotCompared, RobloxClientRelease.Compare(Player(current + "RobloxPlayerBeta.exe", null, state: ClientInstallationFileState.SkippedUnsafe), Latest));
        Assert.Equal(ClientReleaseMatch.Current, RobloxClientRelease.Compare(Player(current + "RobloxPlayerBeta.exe", null, state: ClientInstallationFileState.MetadataUnavailable), Latest));
        Assert.Equal(ClientReleaseMatch.Current, RobloxClientRelease.Compare(Player(current + "RobloxPlayerBeta.exe", null, state: ClientInstallationFileState.TooLarge), Latest));
        Assert.Equal(ClientReleaseMatch.NotChecked, RobloxClientRelease.Compare(Player(current + "RobloxPlayerBeta.exe", null), null));
        Assert.Equal(ClientReleaseMatch.NotChecked, RobloxClientRelease.Compare(Player(current + "RobloxPlayerBeta.exe", null), Latest with { ClientVersionUpload = "version-XYZ" }));
        Assert.Equal(ClientReleaseMatch.NotChecked, RobloxClientRelease.Compare(Player(current + "RobloxPlayerBeta.exe", null), Latest with { Version = "latest" }));
    }

    [Fact]
    public async Task TheLaunchCacheReusesAReleaseForTenMinutesThenAsksOnceAndKeepsTheLastValidAnswer()
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero)); string body = Recorded;
        using var handler = new FakeHandler((_, _) => Task.FromResult(Json(body)));
        using var http = new HttpClient(handler);
        var cache = new PublicClientReleaseCache(new RobloxPublicDirectory(http, clock), clock);
        Assert.Null(cache.Latest); Assert.False(cache.IsFresh);
        var first = await cache.ReadAsync(default);
        Assert.Equal("version-9ac8595712b447a7", first!.ClientVersionUpload); Assert.Equal(1, handler.Calls); Assert.True(cache.IsFresh);
        clock.Now += TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59);
        Assert.Same(first, await cache.ReadAsync(default)); Assert.Equal(1, handler.Calls);
        // Ten minutes old is stale: one request, and its newer answer replaces the kept one.
        clock.Now += TimeSpan.FromSeconds(1); Assert.False(cache.IsFresh);
        body = Recorded.Replace("9ac8595712b447a7", "1111111111111111", StringComparison.Ordinal);
        var second = await cache.ReadAsync(default);
        Assert.Equal("version-1111111111111111", second!.ClientVersionUpload); Assert.Equal(2, handler.Calls);
        // A malformed answer is a failure, never a release: the last valid one stays.
        clock.Now += TimeSpan.FromMinutes(11); body = "{\"version\":\"0.744.0\",\"clientVersionUpload\":\"version-0000000000000000\"}";
        Assert.Same(second, await cache.ReadAsync(default)); Assert.Equal(3, handler.Calls); Assert.False(cache.IsFresh);
    }

    [Fact]
    public async Task TheLaunchCacheKeepsOnlyValidNewerReleasesAndPassesCancellationOn()
    {
        var at = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero); var clock = new Clock(at);
        using var handler = new FakeHandler((_, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(Json(Recorded)); });
        using var http = new HttpClient(handler);
        var cache = new PublicClientReleaseCache(new RobloxPublicDirectory(http, clock), clock);
        cache.Remember(null); cache.Remember(Latest with { ClientVersionUpload = "version-XYZ", RetrievedUtc = at }); Assert.Null(cache.Latest);
        var kept = Latest with { RetrievedUtc = at }; cache.Remember(kept); Assert.Same(kept, cache.Latest); Assert.True(cache.IsFresh);
        cache.Remember(Latest with { ClientVersionUpload = "version-0000000000000000", RetrievedUtc = at.AddMinutes(-1) }); Assert.Same(kept, cache.Latest);
        // A release dated after now (the clock moved back) is never fresh, and the next answer replaces it.
        var future = Latest with { RetrievedUtc = at.AddMinutes(5) }; cache.Remember(future); Assert.Same(future, cache.Latest); Assert.False(cache.IsFresh);
        var asked = await cache.ReadAsync(default);
        Assert.Equal(1, handler.Calls); Assert.Same(asked, cache.Latest); Assert.Equal(at, asked!.RetrievedUtc); Assert.True(cache.IsFresh);
        clock.Now += TimeSpan.FromMinutes(10);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.ReadAsync(stop.Token));
        Assert.Equal(1, handler.Calls); Assert.Throws<ArgumentNullException>(() => new PublicClientReleaseCache(null!));
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ClientInstallationCandidate Player(string path, string? fileVersion, ClientInstallationProduct product = ClientInstallationProduct.Roblox,
        ClientInstallationFileState state = ClientInstallationFileState.PresentUnverified)
        => new(product, path, null, fileVersion, state, [new(ClientInstallationHintSource.VersionDirectory, product)]);

    private static HttpResponseMessage Json(string json) => Response(Encoding.UTF8.GetBytes(json), unknownLength: false);
    private static HttpResponseMessage Response(byte[] bytes, bool unknownLength)
    {
        HttpContent content = unknownLength ? new StreamContent(new NonSeekableStream(bytes)) : new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref _calls); return handle(request, ct); }
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
