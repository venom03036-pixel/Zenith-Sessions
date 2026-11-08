using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Roblox's installer download, verification and run, over a fake HTTP handler, a synthetic Authenticode policy and a fake process
// starter. Nothing reaches the network and nothing is run; the "installer" is random bytes written into a temporary folder this
// test owns, so the real file handling (fresh folder, exclusive write, read-only hold, cleanup) is exercised.
public sealed class ClientUpdateInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-client-update-" + Guid.NewGuid().ToString("N"));
    private static readonly PublicRobloxClientRelease Release = new("0.744.0.7441080", "version-9ac8595712b447a7", DateTimeOffset.UnixEpoch);
    private const string InstallerUri = "https://setup.rbxcdn.com/version-9ac8595712b447a7-RobloxPlayerInstaller.exe";
    private static readonly ClientExecutableSigner Roblox = new("Roblox Corporation", "Roblox Corporation", new string('A', 64),
        new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public ClientUpdateInstallerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void TheInstallerAddressComesOnlyFromAValidUploadName()
    {
        Assert.True(RobloxClientRelease.TryInstallerUri(Release, out var uri));
        Assert.Equal(InstallerUri, uri!.AbsoluteUri);
        Assert.Equal(RobloxClientUpdate.SetupHost, uri.Host);
        Assert.False(RobloxClientRelease.TryInstallerUri(null, out _));
        // Only "version-" and 16 lowercase hex digits reach the address; anything else is refused, never escaped.
        Assert.False(RobloxClientRelease.TryInstallerUri(Release with { ClientVersionUpload = "version-9AC8595712B447A7" }, out _));
        Assert.False(RobloxClientRelease.TryInstallerUri(Release with { ClientVersionUpload = "../RobloxPlayerInstaller" }, out _));
        Assert.False(RobloxClientRelease.TryInstallerUri(Release with { ClientVersionUpload = "version-9ac8595712b447a7/" }, out _));
        Assert.False(RobloxClientRelease.TryInstallerUri(Release with { Version = "latest" }, out _));
    }

    [Fact]
    public async Task ADownloadIsOneAnonymousGetWrittenToAFreshFolderVerifiedAndHeld()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(4096);
        using var handler = new FakeHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(InstallerUri, request.RequestUri!.AbsoluteUri);
            // Honest and anonymous: no cookie, no credential, and no Referer, Origin or User-Agent pretending to be a browser or roblox.com.
            Assert.Null(request.Headers.Authorization); Assert.Null(request.Headers.Referrer);
            Assert.False(request.Headers.Contains("Cookie")); Assert.False(request.Headers.Contains("Origin")); Assert.Empty(request.Headers.UserAgent);
            Assert.Null(request.Content);
            return Task.FromResult(Binary(bytes));
        });
        var starter = new Starter(); var policy = new Policy();
        await using var installer = Installer(handler, policy, starter);
        Assert.False(installer.HasVerifiedInstaller);
        var progress = new List<ClientUpdateDownloadProgress>();
        var result = await installer.DownloadAsync(Release, new SynchronousProgress(progress), default);
        Assert.Equal(ClientUpdateDownloadOutcome.Verified, result.Outcome);
        Assert.Equal(bytes.Length, result.Bytes); Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), result.Sha256); Assert.Equal(Roblox, result.Signer);
        Assert.Null(result.TrustReason); Assert.Null(result.Failure);
        Assert.True(installer.HasVerifiedInstaller); Assert.Equal(1, handler.Calls); Assert.Equal(1, policy.Calls); Assert.Equal(0, starter.Calls);
        Assert.NotEmpty(progress); Assert.Equal(bytes.Length, progress[^1].Bytes); Assert.Equal(bytes.Length, progress[^1].Total);
        string folder = Assert.Single(Directory.GetDirectories(_root));
        Assert.StartsWith("ZenithSessions-RobloxUpdate-", Path.GetFileName(folder), StringComparison.Ordinal);
        string file = Assert.Single(Directory.GetFiles(folder));
        Assert.Equal(RobloxClientUpdate.InstallerFileName, Path.GetFileName(file));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
        // Held with read sharing only: nothing can replace or append to the verified bytes before the run.
        Assert.ThrowsAny<IOException>(() => new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
        Assert.DoesNotContain(_root, result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_root, result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Roblox Corporation", result.Summary, StringComparison.Ordinal);
        installer.Discard();
        Assert.False(installer.HasVerifiedInstaller); Assert.Empty(Directory.GetDirectories(_root));
    }

    [Theory]
    [InlineData((int)NativeClientTrustOutcome.Unsigned, ClientExecutableTrustReason.Unsigned)]
    [InlineData((int)NativeClientTrustOutcome.InvalidSignature, ClientExecutableTrustReason.InvalidSignature)]
    [InlineData((int)NativeClientTrustOutcome.UntrustedChain, ClientExecutableTrustReason.UntrustedChain)]
    [InlineData((int)NativeClientTrustOutcome.RevocationUnavailable, ClientExecutableTrustReason.RevocationUnavailable)]
    public async Task AnUnverifiedInstallerIsDeletedAndNothingIsRun(int outcome, ClientExecutableTrustReason reason)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Binary(RandomNumberGenerator.GetBytes(512))));
        var starter = new Starter();
        await using var installer = Installer(handler, new Policy(new((NativeClientTrustOutcome)outcome, Roblox)), starter);
        var result = await installer.DownloadAsync(Release, null, default);
        Assert.Equal(ClientUpdateDownloadOutcome.NotVerified, result.Outcome); Assert.Equal(reason, result.TrustReason);
        Assert.Null(result.Sha256); Assert.Null(result.Signer);
        Assert.False(installer.HasVerifiedInstaller); Assert.Empty(Directory.GetDirectories(_root));
        var install = await installer.InstallAsync(default);
        Assert.Equal(ClientUpdateInstallOutcome.NotStarted, install.Outcome); Assert.Equal(0, starter.Calls);
    }

    [Fact]
    public async Task ASignatureFromAnotherPublisherIsNotVerified()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Binary(RandomNumberGenerator.GetBytes(512))));
        var other = Roblox with { DisplayName = "Someone Else", Organization = "Someone Else Ltd" };
        await using var installer = Installer(handler, new Policy(new(NativeClientTrustOutcome.VerifiedCached, other)), new Starter());
        var result = await installer.DownloadAsync(Release, null, default);
        Assert.Equal(ClientUpdateDownloadOutcome.NotVerified, result.Outcome); Assert.Equal(ClientExecutableTrustReason.PublisherMismatch, result.TrustReason);
        Assert.False(installer.HasVerifiedInstaller); Assert.Empty(Directory.GetDirectories(_root));
    }

    [Theory]
    [InlineData("oversized-declared")]
    [InlineData("oversized-undeclared")]
    [InlineData("redirect")]
    [InlineData("not-found")]
    [InlineData("network")]
    [InlineData("short")]
    public async Task RefusedOrFailedDownloadsLeaveNothingBehind(string shape)
    {
        using var handler = new FakeHandler((_, _) => shape switch
        {
            "oversized-declared" => Task.FromResult(Binary(new byte[16], declaredLength: RobloxClientUpdate.MaximumInstallerBytes + 1)),
            "oversized-undeclared" => Task.FromResult(Streamed(new ZeroStream(RobloxClientUpdate.MaximumInstallerBytes + 1))),
            // Redirects are refused by the handler settings, so a 3xx is what the client sees; its target is never followed or shown.
            "redirect" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://example.invalid/other.exe") } }),
            "not-found" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)),
            "short" => Task.FromResult(Binary(new byte[16], declaredLength: 32)),
            _ => throw new HttpRequestException("synthetic network fault")
        });
        var starter = new Starter(); var policy = new Policy();
        await using var installer = Installer(handler, policy, starter);
        var result = await installer.DownloadAsync(Release, null, default);
        var expected = shape is "oversized-declared" or "oversized-undeclared" ? ClientUpdateDownloadOutcome.Refused : ClientUpdateDownloadOutcome.Failed;
        Assert.Equal(expected, result.Outcome);
        if (shape == "network") Assert.IsType<HttpRequestException>(result.Failure);
        Assert.Equal(0, policy.Calls); Assert.Equal(0, starter.Calls);
        Assert.False(installer.HasVerifiedInstaller); Assert.Empty(Directory.GetDirectories(_root));
        Assert.DoesNotContain("example.invalid", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("synthetic", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_root, result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnInvalidReleaseIsRefusedBeforeAnyRequest()
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("No request expected."));
        await using var installer = Installer(handler, new Policy(), new Starter());
        var result = await installer.DownloadAsync(Release with { ClientVersionUpload = "version-XYZ" }, null, default);
        Assert.Equal(ClientUpdateDownloadOutcome.Refused, result.Outcome); Assert.Equal(0, handler.Calls);
        Assert.Empty(Directory.GetDirectories(_root));
        await Assert.ThrowsAsync<ArgumentNullException>(() => installer.DownloadAsync(null!, null, default));
    }

    [Fact]
    public async Task CancellingADownloadDeletesThePartialFile()
    {
        var blocked = new BlockingStream(new byte[1024]);
        using var handler = new FakeHandler((_, _) => Task.FromResult(Streamed(blocked, declaredLength: 4096)));
        await using var installer = Installer(handler, new Policy(), new Starter());
        using var stop = new CancellationTokenSource();
        var download = installer.DownloadAsync(Release, null, stop.Token);
        await blocked.FirstChunkRead.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(Directory.GetDirectories(_root)); // The fresh folder exists while the download runs.
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Empty(Directory.GetDirectories(_root)); Assert.False(installer.HasVerifiedInstaller);
    }

    [Fact]
    public async Task ASecondDownloadWhileOneRunsIsRefusedAndANewOneReplacesAHeldInstaller()
    {
        var blocked = new BlockingStream(new byte[64]);
        int calls = 0;
        using var handler = new FakeHandler((_, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1 ? Streamed(blocked, declaredLength: 128) : Binary(new byte[256])));
        await using var installer = Installer(handler, new Policy(), new Starter());
        var first = installer.DownloadAsync(Release, null, default);
        await blocked.FirstChunkRead.WaitAsync(TimeSpan.FromSeconds(30));
        var refused = await installer.DownloadAsync(Release, null, default);
        Assert.Equal(ClientUpdateDownloadOutcome.Refused, refused.Outcome); Assert.Equal(1, handler.Calls);
        blocked.Release(new byte[64]);
        Assert.Equal(ClientUpdateDownloadOutcome.Verified, (await first).Outcome);
        string held = Assert.Single(Directory.GetDirectories(_root));
        var second = await installer.DownloadAsync(Release, null, default);
        Assert.Equal(ClientUpdateDownloadOutcome.Verified, second.Outcome); Assert.Equal(256, second.Bytes);
        string replaced = Assert.Single(Directory.GetDirectories(_root));
        Assert.NotEqual(held, replaced); Assert.False(Directory.Exists(held));
    }

    [Fact]
    public async Task InstallRunsTheHeldFileWithNoArgumentsWaitsForItAndCleansUp()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Binary(RandomNumberGenerator.GetBytes(256))));
        var starter = new Starter { ExitCode = 0 };
        await using var installer = Installer(handler, new Policy(), starter);
        Assert.Equal(ClientUpdateDownloadOutcome.Verified, (await installer.DownloadAsync(Release, null, default)).Outcome);
        string folder = Assert.Single(Directory.GetDirectories(_root)); string file = Path.Combine(folder, RobloxClientUpdate.InstallerFileName);
        var result = await installer.InstallAsync(default);
        Assert.Equal(ClientUpdateInstallOutcome.Exited, result.Outcome); Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, starter.Calls); var start = starter.StartInfo!;
        // The verified file itself, with no arguments: the installer decides what to do; Zenith adds no channel, flag or URI.
        Assert.Equal(file, start.FileName, StringComparer.OrdinalIgnoreCase); Assert.Empty(start.ArgumentList); Assert.Equal("", start.Arguments);
        Assert.Equal(folder, start.WorkingDirectory, StringComparer.OrdinalIgnoreCase); Assert.False(start.UseShellExecute); Assert.False(start.ErrorDialog);
        Assert.True(starter.Disposed);
        Assert.False(installer.HasVerifiedInstaller); Assert.Empty(Directory.GetDirectories(_root));
        Assert.Equal(ClientUpdateInstallOutcome.NotStarted, (await installer.InstallAsync(default)).Outcome); Assert.Equal(1, starter.Calls);
        Assert.DoesNotContain(_root, result.Summary, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain(_root, result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheHeldInstallerCannotBeReplacedRenamedOrDeletedBeforeTheRun()
    {
        // The verifier keeps the file open sharing read access only, so between verification and the run nothing can rewrite,
        // rename or delete it: the bytes Windows verified are the bytes that run. (The run still re-reads the entry first.)
        using var handler = new FakeHandler((_, _) => Task.FromResult(Binary(RandomNumberGenerator.GetBytes(256))));
        var starter = new Starter { ExitCode = 0 };
        await using var installer = Installer(handler, new Policy(), starter);
        Assert.Equal(ClientUpdateDownloadOutcome.Verified, (await installer.DownloadAsync(Release, null, default)).Outcome);
        string folder = Assert.Single(Directory.GetDirectories(_root)); string file = Path.Combine(folder, RobloxClientUpdate.InstallerFileName);
        Assert.ThrowsAny<IOException>(() => File.Move(file, Path.Combine(folder, "aside.bin")));
        Assert.ThrowsAny<IOException>(() => File.Delete(file));
        Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(256)));
        Assert.ThrowsAny<IOException>(() => File.OpenWrite(file).Dispose());
        // Reading is still allowed, which is what CreateProcess needs to map the image.
        new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete).Dispose();
        Assert.True(File.Exists(file)); Assert.True(installer.HasVerifiedInstaller);
        var result = await installer.InstallAsync(default);
        Assert.Equal(ClientUpdateInstallOutcome.Exited, result.Outcome); Assert.Equal(1, starter.Calls);
        Assert.Equal(file, starter.StartInfo!.FileName, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StoppingTheWaitLeavesTheInstallerRunning()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Binary(RandomNumberGenerator.GetBytes(256))));
        var starter = new Starter { ExitCode = null }; // Never exits.
        await using var installer = Installer(handler, new Policy(), starter);
        Assert.Equal(ClientUpdateDownloadOutcome.Verified, (await installer.DownloadAsync(Release, null, default)).Outcome);
        using var stop = new CancellationTokenSource();
        var install = installer.InstallAsync(stop.Token);
        await starter.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        stop.Cancel();
        var result = await install;
        // The seam has no way to end a process: Zenith only stops waiting. The real installer keeps its folder while it runs
        // (the delete is refused and the folder is left); this fake holds nothing, so the folder goes.
        Assert.Equal(ClientUpdateInstallOutcome.Stopped, result.Outcome); Assert.Null(result.ExitCode); Assert.True(starter.Disposed);
        Assert.False(installer.HasVerifiedInstaller); Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task AnInstallerThatOutlivesTheWaitLimitIsReportedStillRunning()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Binary(RandomNumberGenerator.GetBytes(256))));
        var starter = new Starter { ExitCode = null };
        await using var installer = Installer(handler, new Policy(), starter, waitLimit: TimeSpan.FromMilliseconds(100));
        Assert.Equal(ClientUpdateDownloadOutcome.Verified, (await installer.DownloadAsync(Release, null, default)).Outcome);
        var result = await installer.InstallAsync(default);
        Assert.Equal(ClientUpdateInstallOutcome.StillRunning, result.Outcome); Assert.True(starter.Disposed); Assert.False(installer.HasVerifiedInstaller);
    }

    [Fact]
    public async Task DisposeDiscardsAHeldInstallerAndRefusesLaterWork()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Binary(RandomNumberGenerator.GetBytes(256))));
        var installer = Installer(handler, new Policy(), new Starter());
        Assert.Equal(ClientUpdateDownloadOutcome.Verified, (await installer.DownloadAsync(Release, null, default)).Outcome);
        await installer.DisposeAsync();
        Assert.False(installer.HasVerifiedInstaller); Assert.Empty(Directory.GetDirectories(_root));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => installer.DownloadAsync(Release, null, default));
        Assert.Equal(ClientUpdateInstallOutcome.NotStarted, (await installer.InstallAsync(default)).Outcome);
    }

    private WindowsRobloxClientUpdateInstaller Installer(HttpMessageHandler handler, IClientAuthenticodePolicy policy, Starter starter, TimeSpan? waitLimit = null)
        => new(new HttpClient(handler, disposeHandler: false), policy, starter, _root, waitLimit: waitLimit);

    private static HttpResponseMessage Binary(byte[] bytes, long? declaredLength = null)
    {
        var content = new ByteArrayContent(bytes);
        if (declaredLength is { } length) content.Headers.ContentLength = length;
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
    private static HttpResponseMessage Streamed(Stream stream, long? declaredLength = null)
    {
        var content = new StreamContent(stream);
        if (declaredLength is { } length) content.Headers.ContentLength = length;
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class SynchronousProgress(List<ClientUpdateDownloadProgress> seen) : IProgress<ClientUpdateDownloadProgress>
    {
        public void Report(ClientUpdateDownloadProgress value) { lock (seen) seen.Add(value); }
    }
    private sealed class Policy(NativeClientTrustEvidence? evidence = null) : IClientAuthenticodePolicy
    {
        private int _calls; public int Calls => Volatile.Read(ref _calls);
        public NativeClientTrustEvidence Verify(SafeFileHandle handle, string path, DateTimeOffset now, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Assert.False(handle.IsInvalid); Interlocked.Increment(ref _calls); return evidence ?? new(NativeClientTrustOutcome.VerifiedCached, Roblox); }
    }
    private sealed class Starter : IInstallerProcessStarter
    {
        public int Calls; public ProcessStartInfo? StartInfo; public int? ExitCode = 0; public volatile bool Disposed;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IStartedInstallerProcess? Start(ProcessStartInfo start) { Calls++; StartInfo = start; Started.TrySetResult(); return new Process(this); }
        private sealed class Process(Starter owner) : IStartedInstallerProcess
        {
            public async Task<int?> WaitForExitAsync(CancellationToken ct)
            {
                if (owner.ExitCode is { } code) return code;
                await Task.Delay(Timeout.Infinite, ct); return null;
            }
            public void Dispose() => owner.Disposed = true;
        }
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref _calls); return handle(request, ct); }
    }
    /// <summary>A non-seekable stream of zeros with no known length.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        { int n = (int)Math.Min(count, length - _position); Array.Clear(buffer, offset, n); _position += n; return n; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    /// <summary>Serves one chunk, then blocks until released or the read is cancelled.</summary>
    private sealed class BlockingStream(byte[] first) : Stream
    {
        private byte[]? _pending = first; private bool _restServed;
        private readonly TaskCompletionSource<byte[]?> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task FirstChunkRead => _firstRead.Task;
        public void Release(byte[]? rest) => _release.TrySetResult(rest);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count), default).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pending is { } chunk) { _pending = null; chunk.AsSpan(0, Math.Min(chunk.Length, buffer.Length)).CopyTo(buffer.Span); _firstRead.TrySetResult(); return Math.Min(chunk.Length, buffer.Length); }
            if (_restServed) return 0;
            var rest = await _release.Task.WaitAsync(ct);
            _restServed = true;
            if (rest is null || rest.Length == 0) return 0;
            rest.AsSpan(0, Math.Min(rest.Length, buffer.Length)).CopyTo(buffer.Span);
            return Math.Min(rest.Length, buffer.Length);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-client-update-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
