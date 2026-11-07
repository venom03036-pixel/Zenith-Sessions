using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

/// <summary>Injected policy, synthetic ordinary files and fake process objects only; no executable is started.</summary>
public sealed class WindowsNativeHandoffStartTests : IDisposable
{
    private readonly string _name = "zenith-held-native-fixture-" + Guid.NewGuid().ToString("N");
    private string Root => Path.Combine(Path.GetTempPath(), _name);
    private string Executable => Path.Combine(Root, "RobloxPlayerBeta.exe");
    private const string Handoff = "roblox-player:1+launchmode:play+gameinfo:synthetic-opaque-one-use-value";
    private static readonly ClientExecutableSigner Roblox = new("Roblox Corporation", "Roblox Corporation", new string('A', 64),
        new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private ClientInstallationCandidate Candidate => new(ClientInstallationProduct.Roblox, Executable, "Synthetic metadata", "1", ClientInstallationFileState.PresentUnverified, []);
    private byte[] CreateFile(string text = "Synthetic file bytes, never an executable")
    { Directory.CreateDirectory(Root); byte[] bytes = Encoding.ASCII.GetBytes(text); File.WriteAllBytes(Executable, bytes); return bytes; }

    [Fact]
    public async Task PreparationSharesVerificationPolicyAndRetainsItsFileAndSingleSlotUntilOneStart()
    {
        byte[] bytes = CreateFile(); var policy = new Policy(); var starter = new Starter(start =>
        {
            Assert.Equal(Executable, start.FileName); Assert.False(start.UseShellExecute); Assert.False(start.ErrorDialog);
            Assert.Equal(Root, start.WorkingDirectory); Assert.Equal(Handoff, Assert.Single(start.ArgumentList));
            Assert.Empty(start.Arguments); Assert.Empty(start.UserName); Assert.Null(start.Password); Assert.Null(start.PasswordInClearText);
            Assert.False(start.RedirectStandardInput); Assert.False(start.RedirectStandardOutput); Assert.False(start.RedirectStandardError);
            AssertFilePinned(); return new FakeProcess();
        });
        await using var verifier = new WindowsClientExecutableTrust(policy, starter: starter);
        using var preparation = await verifier.PrepareAsync(Candidate, default);
        Assert.Equal(ClientExecutableTrustStatus.VerifiedRobloxPublisher, preparation.Trust.Status);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), preparation.Trust.Sha256);
        Assert.NotNull(preparation.Prepared); Assert.False(preparation.Trust.NativeLaunchAuthorized); AssertFilePinned();
        using var busy = await verifier.PrepareAsync(Candidate, default);
        Assert.Null(busy.Prepared); Assert.Equal(ClientExecutableTrustReason.Busy, busy.Trust.Reason);
        Assert.Equal(ClientExecutableTrustReason.Busy, (await verifier.VerifyAsync(Candidate, default)).Reason);
        Assert.Equal(1, policy.Calls);
        var result = preparation.Prepared!.Start(Handoff);
        Assert.Equal(NativeHandoffDispatchOutcome.StartedUnverified, result.Outcome); Assert.True(result.ProcessReceipt!.IsValid);
        Assert.Equal(NativeHandoffDispatchOutcome.NotStarted, preparation.Prepared.Start(Handoff).Outcome);
        Assert.Equal(1, starter.Calls); Assert.Empty(starter.LastStart!.ArgumentList); AssertFileReleased();
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Executable));
        Assert.DoesNotContain(Handoff, preparation.ToString()); Assert.DoesNotContain(Root, preparation.Prepared.ToString());
        Assert.DoesNotContain(Handoff, JsonSerializer.Serialize(result));
        Assert.Equal(ClientExecutableTrustStatus.VerifiedRobloxPublisher, (await verifier.VerifyAsync(Candidate, default)).Status);
        Assert.Equal(2, policy.Calls);
    }

    [Theory]
    [InlineData((int)NativeClientTrustOutcome.Unsigned)]
    [InlineData((int)NativeClientTrustOutcome.InvalidSignature)]
    [InlineData((int)NativeClientTrustOutcome.RevocationUnavailable)]
    [InlineData((int)NativeClientTrustOutcome.UntrustedChain)]
    [InlineData((int)NativeClientTrustOutcome.AccessDenied)]
    [InlineData((int)NativeClientTrustOutcome.MetadataUnavailable)]
    [InlineData((int)NativeClientTrustOutcome.Unavailable)]
    public async Task NonpositiveTrustNeverProducesAPreparedStart(int outcome)
    {
        CreateFile(); var starter = new Starter(); var policy = new Policy(new((NativeClientTrustOutcome)outcome, Roblox));
        await using var verifier = new WindowsClientExecutableTrust(policy, starter: starter);
        using var result = await verifier.PrepareAsync(Candidate, default);
        Assert.Null(result.Prepared); Assert.NotEqual(ClientExecutableTrustStatus.VerifiedRobloxPublisher, result.Trust.Status);
        if ((NativeClientTrustOutcome)outcome == NativeClientTrustOutcome.RevocationUnavailable)
        { Assert.Equal(ClientExecutableTrustStatus.Unknown, result.Trust.Status); Assert.Equal(ClientExecutableTrustReason.RevocationUnavailable, result.Trust.Reason); }
        Assert.Equal(0, starter.Calls); AssertFileReleased();
    }

    [Theory]
    [InlineData("Other Corporation")]
    [InlineData("Roblox Corporation ")]
    [InlineData("Roblox Corporatiоn")]
    public async Task PublisherMismatchCannotProduceAHeldStart(string organization)
    {
        CreateFile(); var starter = new Starter();
        await using var verifier = new WindowsClientExecutableTrust(new Policy(new(NativeClientTrustOutcome.VerifiedCached,
            Roblox with { Organization = organization })), starter: starter);
        using var result = await verifier.PrepareAsync(Candidate, default);
        Assert.Null(result.Prepared); Assert.Equal(ClientExecutableTrustReason.PublisherMismatch, result.Trust.Reason);
        Assert.Equal(0, starter.Calls); AssertFileReleased();
    }

    [Fact]
    public async Task PreparationAlwaysRehashesInsteadOfReusingEarlierDiagnosticEvidence()
    {
        CreateFile(); var policy = new Policy(); var starter = new Starter();
        await using var verifier = new WindowsClientExecutableTrust(policy, starter: starter);
        var before = await verifier.VerifyAsync(Candidate, default);
        byte[] changed = CreateFile("Different synthetic bytes after diagnostic verification");
        using var prepared = await verifier.PrepareAsync(Candidate, default);
        Assert.NotNull(prepared.Prepared); Assert.NotEqual(before.Sha256, prepared.Trust.Sha256);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(changed)), prepared.Trust.Sha256);
        Assert.Equal(2, policy.Calls); Assert.Equal(0, starter.Calls);
    }

    [Fact]
    public async Task ChangedSnapshotDuringPreparationCannotEscapeAsAPreparedStart()
    {
        CreateFile(); var expected = await Baseline(); int reads = 0; var starter = new Starter();
        await using var verifier = new WindowsClientExecutableTrust(new Policy(), snapshot: _ =>
            Interlocked.Increment(ref reads) == 1 ? expected : expected with { FileId = new string('F', 32) }, starter: starter);
        using var result = await verifier.PrepareAsync(Candidate, default);
        Assert.Null(result.Prepared); Assert.Equal(ClientExecutableTrustReason.FileChanged, result.Trust.Reason);
        Assert.Equal(0, starter.Calls); AssertFileReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedOrUnreadableIdentityImmediatelyBeforeStartPreventsInvocation(bool unreadable)
    {
        CreateFile(); var expected = await Baseline(); bool changed = false; var starter = new Starter();
        await using var verifier = new WindowsClientExecutableTrust(new Policy(), snapshot: _ =>
        {
            if (!changed) return expected;
            if (unreadable) throw new IOException("Synthetic metadata access failed");
            return expected with { LastWriteUtc = expected.LastWriteUtc.AddSeconds(1) };
        }, starter: starter);
        using var preparation = await verifier.PrepareAsync(Candidate, default); Assert.NotNull(preparation.Prepared);
        changed = true;
        Assert.Equal(NativeHandoffDispatchOutcome.NotStarted, preparation.Prepared!.Start(Handoff).Outcome);
        Assert.Equal(0, starter.Calls); AssertFileReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrDisposalDuringTheFinalFileCheckStillPreventsNativeCreation(bool dispose)
    {
        CreateFile(); var expected = await Baseline(); bool finalCheck = false;
        IPreparedNativeHandoffStart? prepared = null; using var stop = new CancellationTokenSource(); var starter = new Starter();
        await using var verifier = new WindowsClientExecutableTrust(new Policy(), snapshot: _ =>
        {
            if (finalCheck) { if (dispose) prepared!.Dispose(); else stop.Cancel(); }
            return expected;
        }, starter: starter);
        using var preparation = await verifier.PrepareAsync(Candidate, stop.Token); prepared = preparation.Prepared;
        Assert.NotNull(prepared); finalCheck = true;
        Assert.Equal(NativeHandoffDispatchOutcome.NotStarted, prepared!.Start(Handoff).Outcome);
        Assert.Equal(0, starter.Calls); AssertFileReleased();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://www.roblox.com/games/123")]
    [InlineData("ROBLOX-PLAYER:1+gameinfo:test")]
    [InlineData("roblox-player:1+gameinfo:two words")]
    [InlineData("roblox-player:1+gameinfo:line\nnext")]
    [InlineData("roblox-player:1+gameinfo:bad\0tail")]
    [InlineData("roblox-player:1+gameinfo:bad\ttail")]
    [InlineData("roblox-player:1+gameinfo:bad\\tail")]
    [InlineData("roblox-player:1+gameinfo:bad\"tail")]
    [InlineData("roblox-player:1+gameinfo:nonasciié")]
    public async Task DefensiveArgumentBoundsRejectUnsafeShapesWithoutCallingTheStarter(string? argument)
    {
        CreateFile(); var starter = new Starter(); await using var verifier = new WindowsClientExecutableTrust(new Policy(), starter: starter);
        using var preparation = await verifier.PrepareAsync(Candidate, default);
        var result = preparation.Prepared!.Start(argument!);
        Assert.Equal(NativeHandoffDispatchOutcome.NotStarted, result.Outcome); Assert.Null(result.ProcessReceipt);
        Assert.Equal(0, starter.Calls); AssertFileReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullExecutableAndArgumentCapacityIsBoundedBeforeNativeInvocation(bool exceeds)
    {
        CreateFile(); var starter = new Starter(); await using var verifier = new WindowsClientExecutableTrust(new Policy(), starter: starter);
        using var preparation = await verifier.PrepareAsync(Candidate, default);
        const string prefix = "roblox-player:1+gameinfo:";
        int limit = 32767 - Executable.Length - 6;
        string argument = prefix + new string('a', limit - prefix.Length + (exceeds ? 1 : 0));
        var result = preparation.Prepared!.Start(argument);
        Assert.Equal(exceeds ? NativeHandoffDispatchOutcome.NotStarted : NativeHandoffDispatchOutcome.StartedUnverified, result.Outcome);
        Assert.Equal(exceeds ? 0 : 1, starter.Calls); AssertFileReleased();
    }

    [Fact]
    public async Task PreCancelledPreparationDoesNotReadAFileOrTakeAWorker()
    {
        var policy = new Policy(); var starter = new Starter(); await using var verifier = new WindowsClientExecutableTrust(policy, starter: starter);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.PrepareAsync(Candidate, stop.Token));
        Assert.False(Directory.Exists(Root)); Assert.Equal(0, policy.Calls); Assert.Equal(0, starter.Calls);
    }

    [Fact]
    public async Task CancellationAfterPreparationAndDisposalBeforeStartBothPreventCreation()
    {
        CreateFile(); var starter = new Starter(); await using var verifier = new WindowsClientExecutableTrust(new Policy(), starter: starter);
        using var stop = new CancellationTokenSource(); using var preparation = await verifier.PrepareAsync(Candidate, stop.Token);
        stop.Cancel(); Assert.Equal(NativeHandoffDispatchOutcome.NotStarted, preparation.Prepared!.Start(Handoff).Outcome); AssertFileReleased();
        using var second = await verifier.PrepareAsync(Candidate, default); AssertFilePinned();
        second.Dispose(); second.Dispose();
        Assert.Equal(NativeHandoffDispatchOutcome.NotStarted, second.Prepared!.Start(Handoff).Outcome);
        Assert.Equal(0, starter.Calls); AssertFileReleased();
    }

    [Fact]
    public async Task CancellationAndDisposeJoinActualVerificationBeforeAFileCanBeReleased()
    {
        CreateFile(); using var policy = new GatedPolicy(); var starter = new Starter();
        var verifier = new WindowsClientExecutableTrust(policy, starter: starter); using var stop = new CancellationTokenSource();
        var work = verifier.PrepareAsync(Candidate, stop.Token); Task? disposing = null;
        try
        {
            await policy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel();
            disposing = verifier.DisposeAsync().AsTask(); Assert.False(disposing.IsCompleted); Assert.False(work.IsCompleted); AssertFilePinned();
        }
        finally
        {
            policy.Release(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(3)));
            await (disposing ?? verifier.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Equal(0, starter.Calls); AssertFileReleased();
    }

    [Fact]
    public async Task ActualStartRemainsOwnedThroughCancellationDisposalAndConcurrentAttempts()
    {
        CreateFile(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var atStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeProcess(); var starter = new Starter(_ => { entered.Set(); atStart.TrySetResult(); release.Wait(); return fake; });
        var verifier = new WindowsClientExecutableTrust(new Policy(), starter: starter); using var stop = new CancellationTokenSource();
        using var preparation = await verifier.PrepareAsync(Candidate, stop.Token);
        var work = Task.Run(() => preparation.Prepared!.Start(Handoff)); Task? disposing = null;
        try
        {
            await atStart.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.True(entered.IsSet);
            Assert.Equal(NativeHandoffDispatchOutcome.NotStarted, preparation.Prepared!.Start(Handoff).Outcome);
            stop.Cancel(); preparation.Dispose(); AssertFilePinned();
            disposing = verifier.DisposeAsync().AsTask(); Assert.False(disposing.IsCompleted); Assert.False(work.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => { _ = verifier.PrepareAsync(Candidate, default); });
        }
        finally
        {
            release.Set(); await work.WaitAsync(TimeSpan.FromSeconds(3));
            await (disposing ?? verifier.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(3));
        }
        var result = await work;
        Assert.Equal(NativeHandoffDispatchOutcome.StartedUnverified, result.Outcome); Assert.True(result.ProcessReceipt!.IsValid);
        Assert.Equal(1, starter.Calls); Assert.True(fake.Disposed); Assert.Empty(starter.LastStart!.ArgumentList); AssertFileReleased();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("start-throws")]
    [InlineData("capture-throws")]
    [InlineData("invalid-receipt")]
    public async Task AnInvokedAttemptWithoutACertainOriginalReceiptIsUncertainAndNeverExposesItsArgument(string failure)
    {
        CreateFile(); var process = new FakeProcess
        {
            Capture = at => failure == "capture-throws" ? throw new IOException(Handoff)
                : failure == "invalid-receipt" ? new(0, at.AddMinutes(-1).UtcTicks, at) : new(12345, at.AddMinutes(-1).UtcTicks, at)
        };
        var starter = new Starter(_ => failure switch
        { "null" => null, "start-throws" => throw new IOException(Handoff), _ => process });
        await using var verifier = new WindowsClientExecutableTrust(new Policy(), starter: starter);
        using var preparation = await verifier.PrepareAsync(Candidate, default);
        var result = preparation.Prepared!.Start(Handoff);
        Assert.Equal(NativeHandoffDispatchOutcome.Uncertain, result.Outcome); Assert.Null(result.ProcessReceipt);
        Assert.Equal(1, starter.Calls); Assert.Empty(starter.LastStart!.ArgumentList); AssertFileReleased();
        Assert.DoesNotContain(Handoff, result.ToString()); Assert.DoesNotContain(Handoff, JsonSerializer.Serialize(result));
        if (failure is "capture-throws" or "invalid-receipt") Assert.True(process.Disposed);
    }

    [Fact]
    public async Task AValidOriginalReceiptSurvivesAnUncertainHandleCleanupFailure()
    {
        CreateFile(); var process = new FakeProcess { ThrowOnDispose = true }; var starter = new Starter(_ => process);
        await using var verifier = new WindowsClientExecutableTrust(new Policy(), starter: starter);
        using var preparation = await verifier.PrepareAsync(Candidate, default);
        var result = preparation.Prepared!.Start(Handoff);
        Assert.Equal(NativeHandoffDispatchOutcome.Uncertain, result.Outcome); Assert.True(result.ProcessReceipt!.IsValid);
        Assert.Equal(12345, result.ProcessReceipt.ProcessId); Assert.True(process.Disposed); AssertFileReleased();
    }

    [Fact]
    public async Task DisposingAnUnconsumedPreparationReleasesItsSlotWithoutStartingAnything()
    {
        CreateFile(); var starter = new Starter(); var verifier = new WindowsClientExecutableTrust(new Policy(), starter: starter);
        using var preparation = await verifier.PrepareAsync(Candidate, default); AssertFilePinned();
        await verifier.DisposeAsync(); await verifier.DisposeAsync();
        Assert.Equal(NativeHandoffDispatchOutcome.NotStarted, preparation.Prepared!.Start(Handoff).Outcome);
        Assert.Equal(0, starter.Calls); AssertFileReleased();
    }

    private async Task<ClientExecutableFileSnapshot> Baseline()
    {
        await using var verifier = new WindowsClientExecutableTrust(new Policy(), starter: new Starter());
        return (await verifier.VerifyAsync(Candidate, default)).File!;
    }
    private void AssertFilePinned()
    {
        Assert.Throws<IOException>(() => File.Open(Executable, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
        Assert.Throws<IOException>(() => File.Delete(Executable));
    }
    private void AssertFileReleased()
    { using var file = File.Open(Executable, FileMode.Open, FileAccess.Write, FileShare.None); }

    private sealed class Policy(NativeClientTrustEvidence? evidence = null) : IClientAuthenticodePolicy
    {
        private int _calls; public int Calls => Volatile.Read(ref _calls);
        public NativeClientTrustEvidence Verify(SafeFileHandle handle, string path, DateTimeOffset now, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Assert.False(handle.IsInvalid); Interlocked.Increment(ref _calls); return evidence ?? new(NativeClientTrustOutcome.VerifiedCached, Roblox); }
    }
    private sealed class GatedPolicy : IClientAuthenticodePolicy, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public NativeClientTrustEvidence Verify(SafeFileHandle handle, string path, DateTimeOffset now, CancellationToken ct)
        { Entered.TrySetResult(); _release.Wait(); return new(NativeClientTrustOutcome.VerifiedCached, Roblox); }
        public void Release() => _release.Set(); public void Dispose() => _release.Dispose();
    }
    private sealed class Starter(Func<ProcessStartInfo, INativeHandoffStartedProcess?>? action = null) : INativeHandoffProcessStarter
    {
        private int _calls; public int Calls => Volatile.Read(ref _calls); public ProcessStartInfo? LastStart;
        public INativeHandoffStartedProcess? Start(ProcessStartInfo start)
        { LastStart = start; Interlocked.Increment(ref _calls); return action is null ? new FakeProcess() : action(start); }
    }
    private sealed class FakeProcess : INativeHandoffStartedProcess
    {
        public Func<DateTimeOffset, LaunchProcessReceipt?> Capture = at => new(12345, at.AddMinutes(-1).UtcTicks, at);
        public bool Disposed, ThrowOnDispose;
        public LaunchProcessReceipt? CaptureReceipt(DateTimeOffset at) => Capture(at);
        public void Dispose() { Disposed = true; if (ThrowOnDispose) throw new IOException(Handoff); }
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(Root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || Path.GetFileName(full) != _name)
            throw new InvalidOperationException("Unexpected held-executable fixture path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
