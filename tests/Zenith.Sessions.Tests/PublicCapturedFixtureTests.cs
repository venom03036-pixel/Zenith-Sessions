using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Tests only replay embedded, sanitized public responses. They never invoke the capture script or use a network.
public sealed class PublicCapturedFixtureTests
{
    [Fact]
    public async Task RecordedMappingAndGamePassTheProductionParserAndCorrelateSyntheticIds()
    {
        using var http = Client("Mapping", "Game");
        var result = await new RobloxPublicDirectory(http).LookupGameAsync(42, default);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        Assert.NotNull(result.Value);
        Assert.Equal(84, result.Value.UniverseId);
        Assert.Equal(42, result.Value.RootPlaceId);
        Assert.Equal("Synthetic name", result.Value.Name);
        Assert.True(result.Value.MaxPlayers > 0);
    }

    [Fact]
    public async Task RecordedHeadshotPassesTheProductionParserWithoutKeepingARealImageAddress()
    {
        using var http = Client("Avatar");
        var result = await new RobloxPublicDirectory(http).GetAvatarAsync(123, default);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        Assert.Equal(123, result.Value!.UserId);
        Assert.Equal("tr.rbxcdn.com", result.Value.ImageUri.Host);
        Assert.Contains("00000000000000000000000000000000", result.Value.ImageUri.AbsoluteUri);
    }

    [Fact]
    public async Task RecordedIconPassesTheProductionParserWithSyntheticUniverseCorrelation()
    {
        using var http = Client("Icon");
        var result = await new RobloxPublicDirectory(http).GetGameIconAsync(84, default);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        Assert.Equal(84, result.Value!.UniverseId);
    }

    [Fact]
    public async Task RecordedServerPageRetainsPagingAndOccupancyWithoutKeepingARealJoinTarget()
    {
        using var http = Client("Servers");
        var result = await new RobloxServerDirectory(http).BrowseAsync(new(42), default);
        Assert.Contains(result.Status, new[] { RobloxPublicServerStatus.Complete, RobloxPublicServerStatus.PageLimit });
        Assert.NotEmpty(result.Servers);
        Assert.All(result.Servers, server =>
        {
            Assert.Equal(42, server.PlaceId);
            Assert.StartsWith("11111111-1111-1111-1111-", server.JobId);
            Assert.True(server.MaxPlayers > 0);
        });
    }

    [Fact]
    public async Task RecordedReleasePassesTheProductionParserAndDoesNotDownloadAnything()
    {
        using var http = Client("Release");
        var result = await new RobloxPublicDirectory(http).GetCurrentWindowsPlayerReleaseAsync(default);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        Assert.True(RobloxClientRelease.IsValid(result.Value));
        Assert.Contains("Nothing was downloaded", result.Message);
    }

    [Fact]
    public async Task RecordedNotFoundRemainsUnknownWithoutRetriesOrAuthentication()
    {
        using var http = Client("NotFound");
        var result = await new RobloxPublicDirectory(http).LookupUserAsync(123, default);
        Assert.Equal(PublicDirectoryState.Unknown, result.State);
        Assert.Null(result.Value);
    }

    [Fact]
    public void EveryCaptureHasBoundedAnonymousProvenanceAndMatchingSanitizedBytes()
    {
        using var manifest = JsonDocument.Parse(Read("PublicCaptured.provenance.json"));
        var entries = manifest.RootElement.GetProperty("captures").EnumerateArray().ToArray();
        Assert.Equal(7, entries.Length);
        string sanitizerHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(
            PowerShellScriptCompatibilityTests.RepositoryRoot, "scripts", "Capture-ZenithPublicFixtures.ps1")))).ToLowerInvariant();
        foreach (var entry in entries)
        {
            Assert.Equal("Captured", entry.GetProperty("outcome").GetString());
            Assert.True(entry.GetProperty("anonymous").GetBoolean());
            Assert.Equal("GET", entry.GetProperty("method").GetString());
            Assert.Equal(65536, entry.GetProperty("bodyLimit").GetInt32());
            Assert.InRange(entry.GetProperty("rawBodyBytes").GetInt32(), 1, 65536);
            Assert.True(DateTimeOffset.TryParse(entry.GetProperty("utc").GetString(), out _));
            var uri = new Uri(entry.GetProperty("url").GetString()!);
            Assert.Equal("https", uri.Scheme); Assert.EndsWith(".roblox.com", uri.Host); Assert.Empty(uri.UserInfo);
            var bytes = Read(entry.GetProperty("file").GetString()!);
            Assert.Equal(entry.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            Assert.Equal(sanitizerHash, entry.GetProperty("sanitizerSha256").GetString());
        }
    }

    private static HttpClient Client(params string[] kinds) => new(new RecordedHandler(kinds));
    private static byte[] Read(string name)
    {
        using var stream = typeof(PublicCapturedFixtureTests).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
    }
    private sealed class RecordedHandler(string[] kinds) : HttpMessageHandler
    {
        private int _next;
        protected override void Dispose(bool disposing)
        {
            if (disposing) Assert.Equal(kinds.Length, _next);
            base.Dispose(disposing);
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            Assert.True(_next < kinds.Length, "Unexpected retry or extra request.");
            string kind = kinds[_next++];
            using var manifest = JsonDocument.Parse(Read("PublicCaptured.provenance.json"));
            var capture = manifest.RootElement.GetProperty("captures").EnumerateArray().Single(e => e.GetProperty("kind").GetString() == kind);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)capture.GetProperty("status").GetInt32())
            {
                Content = new StringContent(Encoding.UTF8.GetString(Read(capture.GetProperty("file").GetString()!)), Encoding.UTF8, "application/json")
            });
        }
    }
}
