using System.IO;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class NativeLaunchExecutableQualificationTests
{
    private static readonly NativeLaunchExecutableIdentity Original = new(new('A', 64), new('B', 64), new('C', 64));
    private static ClientInstallationCandidate Candidate(string path = @"C:\Synthetic\RobloxPlayerBeta.exe") => new(
        ClientInstallationProduct.Roblox, path, "Roblox", "1", ClientInstallationFileState.PresentUnverified,
        [new(ClientInstallationHintSource.StandardInstallPath, ClientInstallationProduct.Roblox)]);
    private static NativeLaunchVerification Entry(NativeLaunchExecutableIdentity? identity) => new("official-page", "Roblox",
        DateTimeOffset.UtcNow, Guid.NewGuid(), 730001, DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks, 456, 42, identity);
    private static LaunchPreparationContext Context() => new(new(Guid.NewGuid(), Guid.NewGuid(), 1, 456, 42, 0,
        DateTimeOffset.UtcNow, 7, null, RobloxServerKind.Unknown, true), requiresVerifiedExecutable: true);

    [Fact]
    public async Task AReadableLegacyVerificationNeverEnablesOrdinaryLaunching()
    {
        var source = new Source { Saved = Entry(null).Validated() };
        var child = new Child();
        await using var provider = new WindowsConfiguredNativeLaunchProvider(_ => child, new Trust(), source, (_, _) => true);
        provider.Configure(Candidate());
        Assert.Equal(CapabilityState.Unverified, provider.AccountLaunch.State);
        var result = await provider.PrepareAsync(Context(), default);
        Assert.Equal(LaunchPreparationFailure.Rejected, result.Failure);
        Assert.Equal(0, child.Preparations);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("file")]
    [InlineData("hash")]
    [InlineData("missing")]
    public async Task FreshHeldExecutableMustMatchTheAcceptedTrialBeforeOrdinaryDispatch(string change)
    {
        var source = new Source { Saved = Entry(Original) };
        var child = new Child { Identity = change switch
        {
            "path" => Original with { PathSha256 = new('D', 64) },
            "file" => Original with { FileIdentitySha256 = new('D', 64) },
            "hash" => Original with { FileSha256 = new('D', 64) },
            _ => null
        }};
        // Even if a metadata projection cannot observe same-length/timestamp-restored byte changes, the fresh held hash must.
        await using var provider = new WindowsConfiguredNativeLaunchProvider(_ => child, new Trust(), source, (_, _) => true);
        provider.Configure(Candidate());
        Assert.Equal(CapabilityState.Supported, provider.AccountLaunch.State);
        var result = await provider.PrepareAsync(Context(), default);
        Assert.Equal(LaunchPreparationFailure.Rejected, result.Failure);
        Assert.Null(result.Prepared); Assert.Equal(1, child.Preparations); Assert.Equal(0, child.Starts);
        Assert.True(child.Closed);
        Assert.Equal(CapabilityState.Unverified, provider.AccountLaunch.State);
    }

    [Theory]
    [InlineData("forget")]
    [InlineData("mode")]
    [InlineData("replace")]
    public async Task APreparedOrdinaryLaunchRechecksAcceptanceAndExecutableAtDispatch(string change)
    {
        var source = new Source { Saved = Entry(Original) }; bool current = true;
        var child = new Child();
        await using var provider = new WindowsConfiguredNativeLaunchProvider(_ => child, new Trust(), source, (_, _) => current);
        provider.Configure(Candidate());
        var context = Context();
        var result = await provider.PrepareAsync(context, default);
        Assert.Equal(LaunchPreparationFailure.None, result.Failure);
        await using var prepared = Assert.IsAssignableFrom<IPreparedAccountLaunch>(result.Prepared);
        if (change == "forget") source.Saved = null;
        else if (change == "mode") source.Saved = source.Saved! with { Mode = "direct-ticket" };
        else current = false;
        using var boundary = new LaunchDispatchBoundary(context.OriginalIntent, (_, _) => Task.FromResult(true), () => true, () => new(true, "Synthetic"));
        var started = await prepared.DispatchAsync(boundary, default);
        Assert.Equal(ProviderLaunchStatus.RejectedBeforeStart, started.Status);
        Assert.Equal(0, child.Starts); Assert.True(child.Closed);
    }

    [Fact]
    public async Task OnlyTheTrialHeldIdentityIsRetainedForRecording()
    {
        var child = new Child();
        await using var provider = new WindowsConfiguredNativeLaunchProvider(_ => child, new Trust(), matchesExecutable: (_, _) => true);
        provider.Configure(Candidate());
        var context = new LaunchPreparationContext(Context().OriginalIntent); // Explicit trial, not ordinary permission.
        var result = await provider.PrepareAsync(context, default);
        await using var prepared = Assert.IsAssignableFrom<IPreparedAccountLaunch>(result.Prepared);
        Assert.Null(provider.GetStartedExecutableIdentity(context.OriginalIntent.OperationId));
        child.Identity = Original with { FileSha256 = new('D', 64) }; // A later observation must not replace preparation's identity.
        child.PreparedMode = NativeLaunchMode.DirectTicket; // The mode is also frozen with this preparation.
        using var boundary = new LaunchDispatchBoundary(context.OriginalIntent, (_, _) => Task.FromResult(true), () => true, () => new(true, "Synthetic"));
        Assert.Equal(ProviderLaunchStatus.ClientStartedUnverified, (await prepared.DispatchAsync(boundary, default)).Status);
        Assert.Equal(Original, provider.GetStartedExecutableIdentity(context.OriginalIntent.OperationId));
        Assert.Equal(NativeLaunchMode.OfficialPage, provider.GetStartedLaunchMode(context.OriginalIntent.OperationId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(NativeLaunchMode.DirectTicket)]
    public async Task OrdinaryQualificationRequiresTheActuallyPreparedMode(NativeLaunchMode? mode)
    {
        var child = new Child { PreparedMode = mode };
        await using var provider = new WindowsConfiguredNativeLaunchProvider(_ => child, new Trust(), new Source { Saved = Entry(Original) }, (_, _) => true);
        provider.Configure(Candidate());
        var result = await provider.PrepareAsync(Context(), default);
        if (result.Prepared is { } prepared) await prepared.DisposeAsync();
        Assert.Equal(LaunchPreparationFailure.Rejected, result.Failure);
        Assert.Equal(0, child.Starts);
    }

    [Fact]
    public async Task AStandardPlayerRecordCannotQualifyABootstrapper()
    {
        var source = new Source { Saved = Entry(Original) with { Product = "Bloxstrap" } };
        await using var provider = new WindowsConfiguredNativeLaunchProvider(_ => new Child(), new Trust(), source, (_, _) => true);
        provider.Configure(new(ClientInstallationProduct.Bloxstrap, @"C:\Synthetic\Bloxstrap.exe", "Bloxstrap", "1",
            ClientInstallationFileState.PresentUnverified, [new(ClientInstallationHintSource.StandardInstallPath, ClientInstallationProduct.Bloxstrap)]));
        Assert.Equal(CapabilityState.Unverified, provider.AccountLaunch.State);
        Assert.Equal(LaunchPreparationFailure.Rejected, (await provider.PrepareAsync(Context(), default)).Failure);
    }

    [Fact]
    public async Task AnOwnedFileReplacementAtTheSamePathChangesTheIdentityEvenWithIdenticalBytes()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "zenith-executable-qualification-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "RobloxPlayerBeta.exe");
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
            await using var verifier = new WindowsClientExecutableTrust(new FakeSignature());
            var result = await verifier.VerifyAsync(Candidate(path), default);
            var identity = Assert.IsType<NativeLaunchExecutableIdentity>(WindowsClientExecutableTrust.LaunchIdentity(result));
            Assert.True(WindowsClientExecutableTrust.MatchesLaunchIdentity(Candidate(path), identity));
            File.Move(path, Path.Combine(root, "old.bin"));
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
            Assert.False(WindowsClientExecutableTrust.MatchesLaunchIdentity(Candidate(path), identity));
            var replacement = Assert.IsType<NativeLaunchExecutableIdentity>(WindowsClientExecutableTrust.LaunchIdentity(await verifier.VerifyAsync(Candidate(path), default)));
            Assert.Equal(identity.PathSha256, replacement.PathSha256); Assert.Equal(identity.FileSha256, replacement.FileSha256);
            Assert.NotEqual(identity.FileIdentitySha256, replacement.FileIdentitySha256);
            File.Delete(path);
            Assert.False(WindowsClientExecutableTrust.MatchesLaunchIdentity(Candidate(path), replacement));
        }
        finally
        {
            if (Path.GetDirectoryName(root) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
                || !Path.GetFileName(root).StartsWith("zenith-executable-qualification-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected fixture directory.");
            Directory.Delete(root, true);
        }
    }

    private sealed class Source : INativeLaunchVerificationSource
    { public NativeLaunchVerification? Saved; public NativeLaunchVerification? Find(ClientInstallationProduct product) => Saved; }
    private sealed class Trust : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class Child : IConfiguredNativeLaunchChild
    {
        public NativeLaunchExecutableIdentity? Identity = Original;
        public NativeLaunchExecutableIdentity? ExecutableIdentity => Identity;
        public NativeLaunchMode? PreparedMode { get; set; } = NativeLaunchMode.OfficialPage;
        public bool CleanupHealthy => true;
        public int Preparations, Starts; public bool Closed;
        public Task<LaunchPreparationResult> PrepareAsync(LaunchPreparationContext context, CancellationToken ct)
        { Preparations++; return Task.FromResult(new LaunchPreparationResult(LaunchPreparationFailure.None, new Handle(this, context))); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Handle(Child child, LaunchPreparationContext context) : IPreparedAccountLaunch
    {
        public LaunchPreparationContext Context => context;
        public LaunchIntent DispatchIntent => context.OriginalIntent;
        public bool IsExpired => child.Closed;
        public Task<ProviderLaunchResult> DispatchAsync(LaunchDispatchBoundary boundary, CancellationToken ct)
        { child.Starts++; return Task.FromResult(new ProviderLaunchResult(ProviderLaunchStatus.ClientStartedUnverified, ProcessId: 730001, ProcessStartUtcTicks: DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks)); }
        public ValueTask DisposeAsync() { child.Closed = true; return ValueTask.CompletedTask; }
    }
    private sealed class FakeSignature : IClientAuthenticodePolicy
    {
        public NativeClientTrustEvidence Verify(SafeFileHandle handle, string path, DateTimeOffset now, CancellationToken ct) =>
            new(NativeClientTrustOutcome.VerifiedCached, new("Synthetic", "Roblox Corporation", new('A', 64), now.AddDays(-1), now.AddDays(1)));
    }
}
