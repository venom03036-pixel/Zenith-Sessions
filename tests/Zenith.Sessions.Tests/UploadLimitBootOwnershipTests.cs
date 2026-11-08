using System.IO;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Fixed wall clocks and monotonic uptime only: no clock changes, reboot, UAC or QoS operation.
public sealed class UploadLimitBootOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-upload-boot-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private string PathName => Path.Combine(_root, WindowsUploadLimitControl.RecordFileName);
    private WindowsUploadLimitControl Control(UploadLimitControlTests.FakeRunner runner, DateTimeOffset now, TimeSpan uptime)
        => new(PathName, runner, new Clock(now), () => uptime);

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task AWallClockCorrectionCannotReleaseAnUnresolvedHelper(int hours)
    {
        var runner = new UploadLimitControlTests.FakeRunner { Outcome = new(ElevatedRunKind.TimedOut) };
        await using var first = Control(runner, Now, TimeSpan.FromHours(3));
        await first.ApplyAsync(1000, default);
        var otherRunner = new UploadLimitControlTests.FakeRunner();
        await using var reopened = Control(otherRunner, Now.AddHours(hours).AddMinutes(1), TimeSpan.FromHours(3).Add(TimeSpan.FromMinutes(1)));
        Assert.True((await reopened.LoadRecordAsync(default))?.HelperUnresolved);
        Assert.Equal(UploadLimitStatus.HelperUnresolved, (await reopened.RemoveAsync(default)).Status);
        Assert.Empty(otherRunner.Runs);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task AnAmbiguousEqualOrLongerUptimeDoesNotProveTheOldHelperCannotMutate(int hours)
    {
        var runner = new UploadLimitControlTests.FakeRunner { Outcome = new(ElevatedRunKind.TimedOut) };
        await using var first = Control(runner, Now, TimeSpan.FromHours(3));
        await first.ApplyAsync(1000, default);
        var otherRunner = new UploadLimitControlTests.FakeRunner();
        // This could be a later boot or just a wall-clock correction; neither is authority to forget a helper.
        await using var reopened = Control(otherRunner, Now.AddDays(1), TimeSpan.FromHours(hours));
        Assert.True((await reopened.LoadRecordAsync(default))?.HelperUnresolved);
        Assert.Equal(UploadLimitStatus.HelperUnresolved, (await reopened.RemoveAsync(default)).Status);
        Assert.Empty(otherRunner.Runs);
    }

    [Fact]
    public async Task ALegacyUnresolvedRecordWithoutMonotonicEvidenceCannotBeClearedByItsUtcStamp()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(PathName, JsonSerializer.Serialize(new
        {
            schema=1,lastAction="Apply",kilobitsPerSecond=1000,atUtc=Now.AddDays(-1),
            bootUtc=Now.AddDays(-1).AddHours(-3),confirmed=false,helperUnresolved=true
        }));
        var runner = new UploadLimitControlTests.FakeRunner();
        await using var reopened = Control(runner, Now, TimeSpan.FromMinutes(5));
        Assert.True((await reopened.LoadRecordAsync(default))?.HelperUnresolved);
        Assert.Equal(UploadLimitStatus.HelperUnresolved, (await reopened.RemoveAsync(default)).Status);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task ALegacyHoldSeedsMonotonicEvidenceWithoutReleasingItAndRecoversAfterAProvenRestart()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(PathName, JsonSerializer.Serialize(new
        {
            schema=1,lastAction="Apply",kilobitsPerSecond=1000,atUtc=Now.AddDays(-1),
            bootUtc=Now.AddDays(-1).AddHours(-3),confirmed=false,helperUnresolved=true
        }));
        var runner = new UploadLimitControlTests.FakeRunner();
        await using (var observed = Control(runner, Now, TimeSpan.FromHours(3)))
        {
            Assert.True((await observed.LoadRecordAsync(default))?.HelperUnresolved);
            Assert.Equal(UploadLimitStatus.HelperUnresolved, (await observed.RemoveAsync(default)).Status);
        }
        using (var persisted = JsonDocument.Parse(await File.ReadAllBytesAsync(PathName)))
            Assert.Equal(TimeSpan.FromHours(3).Ticks, persisted.RootElement.GetProperty("uptimeTicks").GetInt64());
        await using var restarted = Control(runner, Now.AddHours(1), TimeSpan.FromMinutes(5));
        Assert.Null(await restarted.LoadRecordAsync(default));
        Assert.Equal(UploadLimitStatus.Confirmed, (await restarted.RemoveAsync(default)).Status);
        Assert.Single(runner.Runs);
    }

    [Fact]
    public async Task AMalformedUptimeCannotProveRestart()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(PathName, JsonSerializer.Serialize(new
        {
            schema=1,lastAction="Apply",kilobitsPerSecond=1000,atUtc=Now,
            bootUtc=Now.AddHours(-3),uptimeTicks=-1,confirmed=false,helperUnresolved=true
        }));
        var runner = new UploadLimitControlTests.FakeRunner();
        await using var reopened = Control(runner, Now.AddDays(1), TimeSpan.FromMinutes(5));
        Assert.Null(await reopened.LoadRecordAsync(default));
        Assert.True(reopened.RecordUnreadable);
        Assert.Equal(UploadLimitStatus.OwnershipUnavailable, (await reopened.RemoveAsync(default)).Status);
        Assert.Empty(runner.Runs);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
