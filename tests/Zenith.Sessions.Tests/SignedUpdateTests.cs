using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class SignedUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-update-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private const string Certificate = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private UpdateSigningKey Trust => new("test-key", Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()), Certificate);
    private UpdateManifest Manifest => new(1, SignedUpdateVerifier.Product, "2.0.0.0", true, DateTimeOffset.UtcNow.AddDays(1), "https://updates.example.test/package.zip", 1, new string('B', 64));
    private byte[] Sign(UpdateManifest manifest, string keyId = "test-key")
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        return JsonSerializer.SerializeToUtf8Bytes(new SignedUpdateEnvelope(keyId, Convert.ToBase64String(bytes),
            Convert.ToBase64String(_key.SignData(SignedUpdateVerifier.SigningBytes(bytes), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))));
    }
    [Fact]
    public void AValidSignedManifestIsBoundToVersionProductAndTrustedPublisher()
    {
        var update = new SignedUpdateVerifier([Trust]).Verify(Sign(Manifest), new(1, 0));
        Assert.Equal("2.0.0.0", update.Manifest.Version);
        Assert.Throws<CryptographicException>(() => new SignedUpdateVerifier([]).Verify(Sign(Manifest), new(1, 0)));
        Assert.Throws<CryptographicException>(() => new SignedUpdateVerifier([Trust]).Verify(Sign(Manifest, "retired"), new(1, 0)));
        Assert.Throws<CryptographicException>(() => new SignedUpdateVerifier([Trust with { PublisherCertificateSha256 = "" }]).Verify(Sign(Manifest), new(1, 0)));
    }
    [Fact]
    public void ModifiedSignedBytesAndWrongKeysAreRejected()
    {
        var envelope = JsonSerializer.Deserialize<SignedUpdateEnvelope>(Sign(Manifest))!;
        byte[] changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Convert.FromBase64String(envelope.ManifestBase64)).Replace("2.0.0.0", "3.0.0.0"));
        Assert.Throws<CryptographicException>(() => new SignedUpdateVerifier([Trust]).Verify(JsonSerializer.SerializeToUtf8Bytes(envelope with { ManifestBase64 = Convert.ToBase64String(changed) }), new(1, 0)));
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<CryptographicException>(() => new SignedUpdateVerifier([Trust with { PublicKeySpki = Convert.ToBase64String(wrongKey.ExportSubjectPublicKeyInfo()) }]).Verify(Sign(Manifest), new(1, 0)));
    }
    [Theory]
    [InlineData("preview")][InlineData("expired")][InlineData("old")][InlineData("product")][InlineData("format")][InlineData("size")][InlineData("scheme")][InlineData("userinfo")]
    public void IncompatibleOrUnapprovedManifestsNeverAuthorizeStaging(string kind)
    {
        var manifest = kind switch
        {
            "preview" => Manifest with { ReleaseReady = false }, "expired" => Manifest with { ExpiresUtc = DateTimeOffset.UtcNow.AddDays(-1) },
            "old" => Manifest with { Version = "1.0" }, "product" => Manifest with { Product = "Other" }, "format" => Manifest with { FormatVersion = 2 },
            "size" => Manifest with { PackageBytes = long.MaxValue }, "scheme" => Manifest with { PackageUri = "http://example.test/a" },
            _ => Manifest with { PackageUri = "https://password@example.test/a" }
        };
        Assert.Throws<InvalidDataException>(() => new SignedUpdateVerifier([Trust]).Verify(Sign(manifest), new(1, 0)));
    }
    [Fact]
    public async Task ALocalFileFeedStagesOnlyVerifiedBytesWithoutExecutingThem()
    {
        var setup = await Fixture(); var publisher = new Publisher();
        var result = await new SignedUpdateStager(new LocalUpdateFeed(), new([Trust]), publisher).StageAsync(setup.Feed, new(1, 0), Path.Combine(_root, "staging"), default);
        Assert.Equal(1, publisher.Calls);
        Assert.Equal("synthetic executable", await File.ReadAllTextAsync(Path.Combine(result.Directory, "content", "Zenith.Sessions.App.exe")));
        Assert.True(File.Exists(Path.Combine(result.Directory, "signed-update.json")));
        Assert.Equal("2.0.0.0", result.Version);
    }
    [Theory]
    [InlineData("../outside.exe")][InlineData("C:/outside.exe")][InlineData("a/../outside.exe")][InlineData("app.exe:stream")]
    [InlineData("CON.txt")][InlineData("folder./bad.exe")][InlineData("same.exe|same.exe")]
    public async Task UnsafeArchiveEntriesAreRejectedAndOwnedStagingIsRemoved(string name)
    {
        var setup = await Fixture(name); var publisher = new Publisher(); string stage = Path.Combine(_root, "staging");
        await Assert.ThrowsAsync<InvalidDataException>(() => new SignedUpdateStager(new LocalUpdateFeed(), new([Trust]), publisher).StageAsync(setup.Feed, new(1, 0), stage, default));
        Assert.Empty(Directory.GetDirectories(stage)); Assert.Equal(0, publisher.Calls);
        Assert.False(File.Exists(Path.Combine(_root, "outside.exe")));
    }
    [Fact]
    public async Task ApplyBoundaryRevalidatesTheSignedArchiveInsteadOfTrustingRewrittenLocalInventory()
    {
        var setup=await Fixture();var stager=new SignedUpdateStager(new LocalUpdateFeed(),new([Trust]),new Publisher());
        var stage=await stager.StageAsync(setup.Feed,new(1,0),Path.Combine(_root,"staging"),default);
        var proof=await stager.VerifyStageAsync(stage.Directory,new(1,0),default);
        string inventory=Path.Combine(stage.Directory,"content","release-manifest.json");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(inventory))),proof.ManifestSha256);
        await File.WriteAllTextAsync(Path.Combine(stage.Directory,"content","Zenith.Sessions.App.exe"),"replacement executable");
        await File.WriteAllTextAsync(inventory,"{\"ReleaseReady\":true,\"Version\":\"2.0.0.0\",\"Files\":[]}");
        await Assert.ThrowsAsync<CryptographicException>(()=>stager.VerifyStageAsync(stage.Directory,new(1,0),default));
    }
    [Fact]
    public async Task HashMismatchOrPublisherFailureLeavesNoInstallableStage()
    {
        var setup = await Fixture(); string stage = Path.Combine(_root, "staging");
        await File.AppendAllTextAsync(setup.Package, "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => new SignedUpdateStager(new LocalUpdateFeed(), new([Trust]), new Publisher()).StageAsync(setup.Feed, new(1, 0), stage, default));
        Assert.Empty(Directory.GetDirectories(stage));
        setup = await Fixture();
        await Assert.ThrowsAsync<CryptographicException>(() => new SignedUpdateStager(new LocalUpdateFeed(), new([Trust]), new Publisher { Reject = true }).StageAsync(setup.Feed, new(1, 0), stage, default));
        Assert.Empty(Directory.GetDirectories(stage));
    }
    [Fact]
    public async Task AFeedCannotChangeOriginAndCancellationIsObservedBeforeWriting()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "feed.json"); await File.WriteAllBytesAsync(path, Sign(Manifest));
        await Assert.ThrowsAsync<InvalidDataException>(() => new SignedUpdateStager(new LocalUpdateFeed(), new([Trust]), new Publisher()).StageAsync(new Uri(path), new(1, 0), Path.Combine(_root, "staging"), default));
        Assert.False(Directory.Exists(Path.Combine(_root, "staging")));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SignedUpdateStager(new LocalUpdateFeed(), new([Trust]), new Publisher()).StageAsync(new Uri(path), new(1, 0), Path.Combine(_root, "staging"), cancelled.Token));
    }
    [Fact]
    public async Task OversizedEnvelopeIsBoundedBeforeVerification()
    {
        Directory.CreateDirectory(_root); string path = Path.Combine(_root, "feed.json");
        await File.WriteAllBytesAsync(path, new byte[SignedUpdateVerifier.MaximumEnvelopeBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => new SignedUpdateStager(new LocalUpdateFeed(), new([Trust]), new Publisher()).StageAsync(new Uri(path), new(1, 0), Path.Combine(_root, "staging"), default));
        Assert.False(Directory.Exists(Path.Combine(_root, "staging")));
    }
    private async Task<(Uri Feed, string Package)> Fixture(string? extra = null)
    {
        Directory.CreateDirectory(_root); string package = Path.Combine(_root, "package.zip");
        using (var output = File.Create(package))
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
        {
            foreach (var pair in new[] { ("Zenith.Sessions.App.exe", "synthetic executable"), ("release-manifest.json", "{\"ReleaseReady\":true,\"Version\":\"2.0.0.0\"}") })
            { using var writer = new StreamWriter(zip.CreateEntry(pair.Item1).Open()); writer.Write(pair.Item2); }
            if (extra is not null) foreach (string name in extra.Split('|')) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("extra"); }
        }
        byte[] zipBytes = await File.ReadAllBytesAsync(package);
        var manifest = Manifest with { PackageUri = new Uri(package).AbsoluteUri, PackageBytes = zipBytes.Length, PackageSha256 = Convert.ToHexString(SHA256.HashData(zipBytes)) };
        string feed = Path.Combine(_root, "feed.json"); await File.WriteAllBytesAsync(feed, Sign(manifest)); return (new(feed), package);
    }
    private sealed class Publisher : IUpdatePublisherVerifier
    {
        public int Calls; public bool Reject;
        public void Verify(FileStream lockedExecutable, string certificateSha256, CancellationToken cancellationToken)
        { Calls++; Assert.Equal(Certificate, certificateSha256); Assert.True(lockedExecutable.CanRead); if (Reject) throw new CryptographicException("synthetic rejection"); }
    }
    public void Dispose() { _key.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
