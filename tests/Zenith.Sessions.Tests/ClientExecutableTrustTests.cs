using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class ClientExecutableTrustTests : IDisposable
{
    private readonly string _name = "zenith-executable-trust-" + Guid.NewGuid().ToString("N");
    private string Root => Path.Combine(Path.GetTempPath(), _name);
    private string Executable => Path.Combine(Root, "RobloxPlayerBeta.exe");
    private static readonly ClientExecutableSigner Roblox = new("Roblox Corporation", "Roblox Corporation", new string('A', 64),
        new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private static ClientInstallationCandidate Candidate(string path, ClientInstallationProduct product = ClientInstallationProduct.Roblox) =>
        new(product, path, "Untrusted product/version hint", "999.0", ClientInstallationFileState.PresentUnverified, []);
    private byte[] CreateFile(int length = 128)
    {
        Directory.CreateDirectory(Root); byte[] bytes = Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();
        File.WriteAllBytes(Executable, bytes); return bytes;
    }

    [Fact]
    public async Task InjectedSuccessfulPolicyBindsDigestSnapshotAndPublisherWithoutLaunchAuthority()
    {
        // A synthetic policy result exercises the boundary, not genuine live Roblox trust.
        byte[] bytes = CreateFile(); var at = DateTimeOffset.UtcNow;
        var policy = new Policy(); await using var verifier = new WindowsClientExecutableTrust(policy, new Clock(at));
        var result = await verifier.VerifyAsync(Candidate(Executable), default);
        Assert.Equal(ClientExecutableTrustStatus.VerifiedRobloxPublisher, result.Status);
        Assert.Equal(ClientExecutableTrustReason.Verified, result.Reason);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), result.Sha256);
        Assert.Equal(Roblox, result.Signer); Assert.Equal(at, result.ObservedUtc);
        Assert.NotNull(result.File); Assert.Equal(Path.GetFullPath(Executable), result.File.FullPath);
        Assert.Equal(bytes.Length, result.File.Length); Assert.Equal(32, result.File.FileId.Length);
        Assert.False(result.NativeLaunchAuthorized); Assert.True(result.UsesCachedRevocationOnly);
        Assert.Contains("signature evidence only", result.EvidenceLabel);
        Assert.DoesNotContain(Root, result.ToString()); Assert.DoesNotContain(Root, result.File.ToString());
        Assert.Equal(1, policy.Calls); Assert.Equal(bytes, await File.ReadAllBytesAsync(Executable));
    }

    [Theory]
    [InlineData((int)NativeClientTrustOutcome.Unsigned, ClientExecutableTrustStatus.Rejected, ClientExecutableTrustReason.Unsigned)]
    [InlineData((int)NativeClientTrustOutcome.InvalidSignature, ClientExecutableTrustStatus.Rejected, ClientExecutableTrustReason.InvalidSignature)]
    [InlineData((int)NativeClientTrustOutcome.RevocationUnavailable, ClientExecutableTrustStatus.Unknown, ClientExecutableTrustReason.RevocationUnavailable)]
    [InlineData((int)NativeClientTrustOutcome.UntrustedChain, ClientExecutableTrustStatus.Unknown, ClientExecutableTrustReason.UntrustedChain)]
    [InlineData((int)NativeClientTrustOutcome.AccessDenied, ClientExecutableTrustStatus.Unknown, ClientExecutableTrustReason.AccessDenied)]
    [InlineData((int)NativeClientTrustOutcome.Unavailable, ClientExecutableTrustStatus.Unknown, ClientExecutableTrustReason.VerificationUnavailable)]
    public async Task MissingOrRejectedEvidenceCannotBecomeTrustDespiteFilenameAndProductHints(
        int outcome, ClientExecutableTrustStatus status, ClientExecutableTrustReason reason)
    {
        CreateFile(); var policy = new Policy(new((NativeClientTrustOutcome)outcome, Roblox));
        await using var verifier = new WindowsClientExecutableTrust(policy);
        var result = await verifier.VerifyAsync(Candidate(Executable) with { ProductName = "Roblox Corporation" }, default);
        Assert.Equal(status, result.Status); Assert.Equal(reason, result.Reason); Assert.False(result.NativeLaunchAuthorized);
    }

    [Theory]
    [InlineData("Other Corporation", ClientExecutableTrustReason.PublisherMismatch)]
    [InlineData("Roblox Corporation ", ClientExecutableTrustReason.PublisherMismatch)]
    [InlineData("Roblox Corporatiоn", ClientExecutableTrustReason.PublisherMismatch)]
    [InlineData("Roblox\nCorporation", ClientExecutableTrustReason.MetadataUnavailable)]
    public async Task CommonNameCannotOverrideTheActualCertificateOrganization(string organization, ClientExecutableTrustReason reason)
    {
        CreateFile(); var policy = new Policy(new(NativeClientTrustOutcome.VerifiedCached, Roblox with { Organization = organization }));
        await using var verifier = new WindowsClientExecutableTrust(policy);
        var result = await verifier.VerifyAsync(Candidate(Executable), default);
        Assert.Equal(ClientExecutableTrustStatus.Unknown, result.Status); Assert.Equal(reason, result.Reason);
    }

    // Since R-launch-17 (owner decision 2026-11-08) a bootstrapper's own executable is evaluated under its per-product
    // accepted-publisher policy (BootstrapperLaunchTests); a missing file is refused like a missing player, and nothing is
    // created or started. A bootstrapper product never gets an implicit publisher.
    [Theory]
    [InlineData(ClientInstallationProduct.Bloxstrap)]
    [InlineData(ClientInstallationProduct.Fishstrap)]
    [InlineData(ClientInstallationProduct.Froststrap)]
    [InlineData(ClientInstallationProduct.Voidstrap)]
    public async Task BootstrapperRowsAreEvaluatedUnderTheirOwnPolicyAndAMissingFileIsRefused(ClientInstallationProduct product)
    {
        var policy = new Policy(); await using var verifier = new WindowsClientExecutableTrust(policy);
        var result = await verifier.VerifyAsync(Candidate(Path.Combine(Root, product + ".exe"), product), default);
        Assert.Equal(ClientExecutableTrustStatus.Rejected, result.Status);
        Assert.Equal(ClientExecutableTrustReason.MissingFile, result.Reason);
        Assert.Null(result.File); Assert.Null(result.Sha256); Assert.Equal(0, policy.Calls); Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public async Task RobloxLauncherIsNotMistakenForTheDirectNativePlayer()
    {
        var policy = new Policy(); await using var verifier = new WindowsClientExecutableTrust(policy);
        var result = await verifier.VerifyAsync(Candidate(Path.Combine(Root, "RobloxPlayerLauncher.exe")), default);
        Assert.Equal(ClientExecutableTrustStatus.Unknown, result.Status);
        Assert.Equal(ClientExecutableTrustReason.BootstrapperNotEvaluated, result.Reason); Assert.Equal(0, policy.Calls);
    }

    // One filename rule for every layer: discovery lists the direct players and the launcher; the publisher
    // check evaluates only the direct players and reports the launcher as not evaluated.
    [Theory]
    [InlineData("RobloxPlayerBeta.exe", true, false)]
    [InlineData("RobloxPlayer.exe", true, false)]
    [InlineData("robloxplayerbeta.EXE", true, false)]
    [InlineData("RobloxPlayerLauncher.exe", false, true)]
    [InlineData("Roblox.exe", false, false)]
    public async Task DiscoveryAndPublisherCheckShareOneNativePlayerFilenameRule(string name, bool direct, bool launcher)
    {
        Directory.CreateDirectory(Root); string path = Path.Combine(Root, name); File.WriteAllBytes(path, [1, 2, 3, 4]);
        Assert.Equal(direct, RobloxNativePlayerFiles.IsDirectPlayer(path)); Assert.Equal(launcher, RobloxNativePlayerFiles.IsLauncher(path));
        Assert.Equal(direct || launcher, RobloxNativePlayerFiles.IsInventoryPlayer(path));
        Assert.Equal(direct || launcher, WindowsClientInstallationDiscovery.TryNormalizeExecutablePath(path, out _, out var product));
        Assert.Equal(ClientInstallationProduct.Roblox, product);
        var policy = new Policy(); await using var verifier = new WindowsClientExecutableTrust(policy);
        var result = await verifier.VerifyAsync(Candidate(path), default);
        Assert.Equal(launcher, result.Reason == ClientExecutableTrustReason.BootstrapperNotEvaluated);
        Assert.Equal(direct ? ClientExecutableTrustStatus.VerifiedRobloxPublisher : launcher ? ClientExecutableTrustStatus.Unknown : ClientExecutableTrustStatus.Rejected, result.Status);
        Assert.Equal(direct ? 1 : 0, policy.Calls);
    }

    [Theory]
    [InlineData(@"\\server\share\RobloxPlayerBeta.exe")]
    [InlineData(@"\\?\C:\Synthetic\RobloxPlayerBeta.exe")]
    [InlineData(@"\\.\C:\Synthetic\RobloxPlayerBeta.exe")]
    [InlineData(@"C:\Synthetic\RobloxPlayerBeta.exe:alternate")]
    [InlineData(@"C:\Synthetic\..\RobloxPlayerBeta.exe")]
    [InlineData(@"%LOCALAPPDATA%\RobloxPlayerBeta.exe")]
    [InlineData("https://example.invalid/RobloxPlayerBeta.exe")]
    [InlineData("C:\\Synthetic\\RobloxPlayerBeta.exe\n")]
    [InlineData(null)]
    public async Task UnsafePathsAreRejectedWithoutConsultingTheSignatureProvider(string? path)
    {
        var policy = new Policy(); await using var verifier = new WindowsClientExecutableTrust(policy);
        var result = await verifier.VerifyAsync(Candidate(path!), default);
        Assert.Equal(ClientExecutableTrustStatus.Rejected, result.Status); Assert.Equal(ClientExecutableTrustReason.UnsafePath, result.Reason);
        Assert.Equal(0, policy.Calls); Assert.Null(result.Sha256);
    }

    [Fact]
    public async Task OversizedMissingAndWriteLockedFilesDoNotReachSignatureVerification()
    {
        CreateFile(129); var policy = new Policy(); await using var verifier = new WindowsClientExecutableTrust(policy, maximumFileBytes: 128);
        var large = await verifier.VerifyAsync(Candidate(Executable), default);
        Assert.Equal(ClientExecutableTrustReason.FileTooLarge, large.Reason); Assert.Equal(ClientExecutableTrustStatus.Rejected, large.Status);
        File.Delete(Executable);
        Assert.Equal(ClientExecutableTrustReason.MissingFile, (await verifier.VerifyAsync(Candidate(Executable), default)).Reason);
        CreateFile();
        using (var locked = new FileStream(Executable, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var denied = await verifier.VerifyAsync(Candidate(Executable), default);
            Assert.Equal(ClientExecutableTrustStatus.Unknown, denied.Status); Assert.Equal(ClientExecutableTrustReason.AccessDenied, denied.Reason);
        }
        Assert.Equal(0, policy.Calls);
    }

    [Fact]
    public async Task VerificationPinsTheFileAgainstWriteAndDeleteUntilItsActualOwnerFinishes()
    {
        CreateFile(); using var policy = new GatedPolicy(); await using var verifier = new WindowsClientExecutableTrust(policy);
        var work = verifier.VerifyAsync(Candidate(Executable), default);
        try
        {
            await policy.Entered.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Throws<IOException>(() => File.Open(Executable, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
            Assert.Throws<IOException>(() => File.Delete(Executable));
            var deferred = await verifier.VerifyAsync(Candidate(Executable), default);
            Assert.Equal(ClientExecutableTrustStatus.Deferred, deferred.Status); Assert.Equal(1, policy.Calls);
        }
        finally { policy.Release(); await work.WaitAsync(TimeSpan.FromSeconds(3)); }
        using var write = File.Open(Executable, FileMode.Open, FileAccess.Write, FileShare.None);
    }

    [Fact]
    public async Task CancellationAndRepeatedDisposeJoinAnUncooperativeNativeOwner()
    {
        CreateFile(); using var policy = new GatedPolicy(); var verifier = new WindowsClientExecutableTrust(policy);
        using var stop = new CancellationTokenSource();
        Task<ClientExecutableTrustResult> work = verifier.VerifyAsync(Candidate(Executable), stop.Token);
        Task? disposal = null;
        // The worker reaches the policy on a pool thread after its file checks; under a full parallel suite that can take
        // longer than a few seconds, so the waits are generous. A missed entry fails as itself instead of hiding behind the
        // cancellation assertion below.
        bool entered = false;
        try
        {
            await policy.Entered.WaitAsync(TimeSpan.FromSeconds(15)); entered = true; stop.Cancel();
            Assert.False(work.IsCompleted);
            disposal = verifier.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => { _ = verifier.VerifyAsync(Candidate(Executable), default); });
        }
        finally
        {
            policy.Release();
            if (entered) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(15)));
            else try { await work.WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception) { } // The TimeoutException above is the failure.
            await (disposal ?? verifier.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(15));
            await verifier.DisposeAsync();
        }
        Assert.Equal(1, policy.Calls);
        using var write = File.Open(Executable, FileMode.Open, FileAccess.Write, FileShare.None);
    }

    [Fact]
    public async Task PreCancelledRequestDoesNotReadTheFileOrOccupyAWorker()
    {
        var policy = new Policy(); await using var verifier = new WindowsClientExecutableTrust(policy);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.VerifyAsync(Candidate(Executable), stop.Token));
        Assert.Equal(0, policy.Calls); Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public async Task ChangedExactFileSnapshotCannotInheritSuccessfulSignatureEvidence()
    {
        CreateFile(); ClientExecutableFileSnapshot original;
        await using (var baseline = new WindowsClientExecutableTrust(new Policy())) original = (await baseline.VerifyAsync(Candidate(Executable), default)).File!;
        int reads = 0;
        await using var verifier = new WindowsClientExecutableTrust(new Policy(), snapshot: _ =>
            Interlocked.Increment(ref reads) == 1 ? original : original with { LastWriteUtc = original.LastWriteUtc.AddSeconds(1) });
        var result = await verifier.VerifyAsync(Candidate(Executable), default);
        Assert.Equal(ClientExecutableTrustStatus.Rejected, result.Status); Assert.Equal(ClientExecutableTrustReason.FileChanged, result.Reason);
        Assert.Null(result.Sha256); Assert.Null(result.Signer);
    }

    [Fact]
    public async Task LaterFileContentsAreRehashedInsteadOfReusingAFormerPositiveResult()
    {
        CreateFile(); var policy = new Policy(); await using var verifier = new WindowsClientExecutableTrust(policy);
        var before = await verifier.VerifyAsync(Candidate(Executable), default);
        File.WriteAllBytes(Executable, Encoding.ASCII.GetBytes("a different synthetic executable fixture"));
        var after = await verifier.VerifyAsync(Candidate(Executable), default);
        Assert.NotEqual(before.Sha256, after.Sha256); Assert.NotEqual(before.File!.Length, after.File!.Length); Assert.Equal(2, policy.Calls);
    }

    [Fact]
    public async Task ActualWindowsPolicyRejectsAnOwnedUnsignedBuildCopyWithoutExecutingIt()
    {
        Directory.CreateDirectory(Root);
        // Copy only this project's own unsigned managed PE. Never inspect an installed Roblox
        // executable, invoke the copied file, modify certificate stores, or request network data.
        File.Copy(typeof(WindowsClientExecutableTrust).Assembly.Location, Executable);
        await using var verifier = new WindowsClientExecutableTrust();
        var result = await verifier.VerifyAsync(Candidate(Executable), default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ClientExecutableTrustStatus.Rejected, result.Status); Assert.Equal(ClientExecutableTrustReason.Unsigned, result.Reason);
        Assert.NotNull(result.Sha256); Assert.False(result.NativeLaunchAuthorized);
    }

    [Theory]
    [InlineData("CN=Roblox Corporation, O=Other Corporation", "Other Corporation")]
    [InlineData("CN=Other Name, O=Roblox Corporation", "Roblox Corporation")]
    [InlineData("CN=Roblox Corporation", null)]
    [InlineData("O=Roblox Corporation, O=Other Corporation", null)]
    public void OrganizationComesFromOneCertificateAttributeRatherThanDisplayText(string subject, string? expected)
        => Assert.Equal(expected, WindowsAuthenticodePolicy.ReadOrganization(new X500DistinguishedName(subject).RawData));

    [Fact]
    public void MalformedCertificateIdentityAndAbsentRevocationAreNeverAccepted()
    {
        Assert.Null(WindowsAuthenticodePolicy.ReadOrganization([0x30, 0x02, 0x31, 0xff]));
        Assert.Equal(NativeClientTrustOutcome.RevocationUnavailable, WindowsAuthenticodePolicy.Classify(unchecked((int)0x80092013)));
        Assert.Equal(NativeClientTrustOutcome.RevocationUnavailable, WindowsAuthenticodePolicy.Classify(unchecked((int)0x800B010E)));
        Assert.Equal(NativeClientTrustOutcome.Unavailable, WindowsAuthenticodePolicy.Classify(unchecked((int)0xDEADBEEF)));
        Assert.NotEqual(0u, WindowsAuthenticodePolicy.ProviderFlags & 0x1000u);
        Assert.NotEqual(0u, WindowsAuthenticodePolicy.ProviderFlags & 0x80u);
        Assert.Equal(0u, WindowsAuthenticodePolicy.ProviderFlags & 0x10u);
    }

    private sealed class Policy(NativeClientTrustEvidence? evidence = null) : IClientAuthenticodePolicy
    {
        private int _calls; public int Calls => Volatile.Read(ref _calls);
        public NativeClientTrustEvidence Verify(SafeFileHandle handle, string path, DateTimeOffset now, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Assert.False(handle.IsInvalid); Interlocked.Increment(ref _calls); return evidence ?? new(NativeClientTrustOutcome.VerifiedCached, Roblox); }
    }
    private sealed class GatedPolicy : IClientAuthenticodePolicy, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls; public int Calls => Volatile.Read(ref _calls); public Task Entered => _entered.Task;
        public NativeClientTrustEvidence Verify(SafeFileHandle handle, string path, DateTimeOffset now, CancellationToken ct)
        { Interlocked.Increment(ref _calls); _entered.TrySetResult(); _release.Wait(); return new(NativeClientTrustOutcome.VerifiedCached, Roblox); }
        public void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    public void Dispose()
    {
        string full = Path.GetFullPath(Root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || Path.GetFileName(full) != _name)
            throw new InvalidOperationException("Unexpected owned executable fixture path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
