using System.IO;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Only fake helper completions. No process, administrator prompt or QoS mutation.
public sealed class UploadLimitUnresolvedOwnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-upload-owner-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private WindowsUploadLimitControl Control(UploadLimitControlTests.FakeRunner runner) => new(
        Path.Combine(_root, WindowsUploadLimitControl.RecordFileName), runner, new Clock(), () => TimeSpan.FromHours(1));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnresolvedOldHelperCannotOverlapANewerMutation(bool timeout)
    {
        var runner = new UploadLimitControlTests.FakeRunner { Hold = !timeout, Outcome = new(ElevatedRunKind.TimedOut) };
        await using var control = Control(runner);
        var first = control.ApplyAsync(1000, default);
        var old = await runner.NextAsync();
        if (!timeout) control.CancelOperations();
        await first;
        runner.Hold = false; runner.Outcome = new(ElevatedRunKind.Exited, 0);
        var next = await control.RemoveAsync(default);
        Assert.NotEqual(UploadLimitStatus.Confirmed, next.Status);
        Assert.Single(runner.Runs);
        // Actual exit, not approval or a released handle, is what permits another mutation.
        old.AbandonedStartCompleted?.Invoke(new(ElevatedRunKind.Exited, 0));
        Assert.Equal(UploadLimitStatus.Confirmed, (await control.RemoveAsync(default)).Status);
        Assert.Equal(2, runner.Runs.Count);
    }

    [Fact]
    public async Task AnUnresolvedOwnerIsDurableBeforeTheHelperStartsAndBlocksAReopenedControl()
    {
        var runner = new UploadLimitControlTests.FakeRunner { Hold = true };
        await using var first = Control(runner);
        var pending = first.ApplyAsync(1000, default);
        await runner.NextAsync();
        var replacementRunner = new UploadLimitControlTests.FakeRunner();
        await using var replacement = Control(replacementRunner);
        await replacement.LoadRecordAsync(default);
        var next = await replacement.RemoveAsync(default);
        first.CancelOperations(); await pending;
        Assert.NotEqual(UploadLimitStatus.Confirmed, next.Status);
        Assert.Empty(replacementRunner.Runs);
    }

    [Fact]
    public async Task FailureToPersistTheExternalOwnerPreventsTheHelperFromStarting()
    {
        Directory.CreateDirectory(Path.Combine(_root, WindowsUploadLimitControl.RecordFileName));
        var runner = new UploadLimitControlTests.FakeRunner();
        await using var control = Control(runner);
        var result = await control.ApplyAsync(1000, default);
        Assert.NotEqual(UploadLimitStatus.Confirmed, result.Status);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task AFailedInitialMarkerWriteNeverInvokesTheRunner()
    {
        Directory.CreateDirectory(Path.Combine(_root, WindowsUploadLimitControl.RecordFileName + ".tmp"));
        var runner = new UploadLimitControlTests.FakeRunner();
        await using var control = Control(runner);
        var result = await control.ApplyAsync(1000, default);
        Assert.Equal(UploadLimitStatus.OwnershipUnavailable, result.Status);
        Assert.False(result.RecordSaved); Assert.Empty(runner.Runs); Assert.Null(control.Current);
    }

    [Fact]
    public async Task AWaitFailureKeepsTheHoldUntilAnActualExitIsObserved()
    {
        var runner = new UploadLimitControlTests.FakeRunner { BeforeReturn = () => throw new IOException("synthetic wait failure") };
        await using var control = Control(runner);
        await Assert.ThrowsAsync<IOException>(() => control.ApplyAsync(1000, default));
        Assert.Equal(UploadLimitStatus.HelperUnresolved, (await control.RemoveAsync(default)).Status);
        var old = Assert.Single(runner.Runs);
        old.AbandonedStartCompleted?.Invoke(new(ElevatedRunKind.Exited, 3));
        Assert.False(control.Current!.HelperUnresolved); Assert.False(control.Current.Confirmed);
        runner.BeforeReturn = null;
        Assert.Equal(UploadLimitStatus.Confirmed, (await control.RemoveAsync(default)).Status);
    }

    [Fact]
    public async Task AWindowsRestartClearsALostUnresolvedOwner()
    {
        var runner = new UploadLimitControlTests.FakeRunner { Outcome = new(ElevatedRunKind.TimedOut) };
        await using var original = Control(runner);
        await original.ApplyAsync(1000, default);
        var nextRunner = new UploadLimitControlTests.FakeRunner();
        await using var restarted = new WindowsUploadLimitControl(Path.Combine(_root, WindowsUploadLimitControl.RecordFileName), nextRunner, new Clock(), () => TimeSpan.FromMinutes(5));
        Assert.Null(await restarted.LoadRecordAsync(default));
        Assert.Equal(UploadLimitStatus.Confirmed, (await restarted.RemoveAsync(default)).Status);
        Assert.Single(nextRunner.Runs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALegacyUncertainRecordCannotProveThatItsHelperExited(bool confirmed)
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, WindowsUploadLimitControl.RecordFileName);
        await File.WriteAllTextAsync(file, System.Text.Json.JsonSerializer.Serialize(new
        {
            schema=1,lastAction="Apply",kilobitsPerSecond=1000,atUtc=Now,bootUtc=Now-TimeSpan.FromHours(1),confirmed
        }));
        var runner = new UploadLimitControlTests.FakeRunner();
        await using var control = Control(runner);
        Assert.Equal(!confirmed, (await control.LoadRecordAsync(default))!.HelperUnresolved);
        var result = await control.RemoveAsync(default);
        Assert.Equal(confirmed ? UploadLimitStatus.Confirmed : UploadLimitStatus.HelperUnresolved, result.Status);
        Assert.Equal(confirmed ? 1 : 0, runner.Runs.Count);
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
