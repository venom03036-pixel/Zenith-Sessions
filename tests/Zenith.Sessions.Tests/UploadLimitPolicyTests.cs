using System.Text.RegularExpressions;
using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// R-instances-18: the upload limit is a Windows QoS policy on the Roblox client executables, applied and removed only by the
// user's click through Windows PowerShell run as administrator. The pure parts live in Core: the accepted rate, the script
// text (only the checked integer goes into it), what each exit code means, and Zenith's own record of what it applied.
public sealed class UploadLimitPolicyTests
{
    [Theory]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(1000, true)]
    [InlineData(10_000_000, true)]
    [InlineData(10_000_001, false)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    public void OnlyWholeKilobitRatesInTheRangeAreAccepted(int rate, bool valid)
    {
        Assert.Equal(valid, UploadLimitPolicy.IsValidRate(rate));
        if (!valid) Assert.Throws<ArgumentOutOfRangeException>(() => UploadLimitPolicy.ApplyScript(rate));
    }

    [Theory]
    [InlineData("1000", 1000)]
    [InlineData(" 2,500 ", 2500)]
    [InlineData("10 000 000", 10_000_000)]
    [InlineData("100", 100)]
    public void TypedRatesAreReadAsWholeKilobitsWithGroupSeparatorsAllowed(string text, int expected)
    {
        Assert.True(UploadLimitPolicy.TryParseRate(text, out int rate));
        Assert.Equal(expected, rate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("99")]
    [InlineData("10000001")]
    [InlineData("1.5")]
    [InlineData("-100")]
    [InlineData("1e3")]
    [InlineData("fast")]
    [InlineData("99999999999")]
    public void OtherTextIsRefusedWithoutARate(string text)
    {
        Assert.False(UploadLimitPolicy.TryParseRate(text, out int rate));
        Assert.Equal(0, rate);
        Assert.Equal("Enter a whole number of kilobits per second from 100 to 10,000,000.", UploadLimitPolicy.RateRequirement);
    }

    [Fact]
    public void TheApplyScriptCarriesOnlyTheCheckedRateAndNamesEveryClientExecutableInTheActiveStore()
    {
        string script = UploadLimitPolicy.ApplyScript(2500);
        // 2,500 kbit/s is 2,500,000 bit/s, the unit New-NetQosPolicy takes.
        Assert.Contains("$rate = 2500000;", script, StringComparison.Ordinal);
        Assert.StartsWith("$ErrorActionPreference = 'Stop';", script, StringComparison.Ordinal);
        Assert.Contains("-PolicyStore ActiveStore", script, StringComparison.Ordinal);
        Assert.Contains("New-NetQosPolicy -Name $name -AppPathNameMatchCondition $app -ThrottleRateActionBitsPerSecond $rate -PolicyStore ActiveStore", script, StringComparison.Ordinal);
        Assert.Contains("Remove-NetQosPolicy -Name $name -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue", script, StringComparison.Ordinal);
        // The same run reads the policy back and reports a mismatch through its own exit code.
        Assert.Contains("Get-NetQosPolicy -Name $name -PolicyStore ActiveStore", script, StringComparison.Ordinal);
        Assert.Contains("exit " + UploadLimitPolicy.ReadbackMismatchExitCode, script, StringComparison.Ordinal);
        Assert.EndsWith("exit 0", script, StringComparison.Ordinal);
        Assert.Equal(["RobloxPlayerBeta.exe", "RobloxPlayer.exe", "Roblox.exe"], UploadLimitPolicy.ClientExecutables);
        foreach (string executable in UploadLimitPolicy.ClientExecutables) Assert.Contains("'" + executable + "'", script, StringComparison.Ordinal);
        Assert.Contains("'" + UploadLimitPolicy.PolicyNamePrefix + "'", script, StringComparison.Ordinal);
        Assert.Equal("ZenithSessionsUploadLimit-", UploadLimitPolicy.PolicyNamePrefix);
        // Nothing but the rate is variable: no double quote, no newline, no character outside the script's own alphabet.
        Assert.DoesNotContain('"', script);
        Assert.DoesNotContain('\n', script);
        Assert.Matches(new Regex(@"^[A-Za-z0-9 $_'.@(),;:|=+{}\-]+$"), script);
        Assert.Equal(script, UploadLimitPolicy.ApplyScript(2500));
        Assert.NotEqual(script, UploadLimitPolicy.ApplyScript(2501));
    }

    [Fact]
    public void TheRemoveScriptDeletesEveryZenithPolicyAndConfirmsTheyAreGoneWithoutCreatingAny()
    {
        string script = UploadLimitPolicy.RemoveScript();
        Assert.StartsWith("$ErrorActionPreference = 'Stop';", script, StringComparison.Ordinal);
        Assert.Contains("Remove-NetQosPolicy -Name $name -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue", script, StringComparison.Ordinal);
        Assert.DoesNotContain("New-NetQosPolicy", script, StringComparison.Ordinal);
        // The listing is read with Stop, so a denied read ends the run with exit 1 instead of passing for an absent policy.
        Assert.Contains("Get-NetQosPolicy -PolicyStore ActiveStore | Where-Object { $_.Name -eq $name }", script, StringComparison.Ordinal);
        Assert.Contains("exit " + UploadLimitPolicy.ReadbackMismatchExitCode, script, StringComparison.Ordinal);
        Assert.EndsWith("exit 0", script, StringComparison.Ordinal);
        Assert.DoesNotContain('"', script);
        foreach (string executable in UploadLimitPolicy.ClientExecutables) Assert.Contains("'" + executable + "'", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, UploadLimitStatus.Confirmed)]
    [InlineData(3, UploadLimitStatus.NotConfirmed)]
    [InlineData(1, UploadLimitStatus.Refused)]
    [InlineData(-1073741510, UploadLimitStatus.Refused)]
    [InlineData(2, UploadLimitStatus.Refused)]
    public void EachExitCodeHasOneMeaning(int exitCode, UploadLimitStatus expected)
        => Assert.Equal(expected, UploadLimitPolicy.StatusForExit(exitCode));

    [Fact]
    public void ExplanationsAreSpecificAndSayWhenTheRecordIsUncertain()
    {
        var applied = new UploadLimitResult(UploadLimitAction.Apply, UploadLimitStatus.Confirmed, 1000, 0, new(UploadLimitAction.Apply, 1000, DateTimeOffset.UtcNow, true));
        Assert.Equal("Windows confirmed the upload limit: all Roblox clients together may send at most 1,000 kbit/s until Windows restarts or you choose Remove.", applied.Explanation);
        var removed = new UploadLimitResult(UploadLimitAction.Remove, UploadLimitStatus.Confirmed, 0, 0, null);
        Assert.Equal("Windows confirmed that Zenith's upload limit policies are gone. Zenith no longer limits what Roblox clients send.", removed.Explanation);
        var declined = new UploadLimitResult(UploadLimitAction.Apply, UploadLimitStatus.Declined, 1000, 0, null);
        Assert.Equal("The administrator prompt was declined, so nothing was changed. Applying or removing an upload limit needs administrator approval.", declined.Explanation);
        var refused = new UploadLimitResult(UploadLimitAction.Apply, UploadLimitStatus.Refused, 1000, 1, new(UploadLimitAction.Apply, 1000, DateTimeOffset.UtcNow, false));
        Assert.Equal("Windows PowerShell could not change the upload limit (exit code 1). Whether a limit is in force is now uncertain; choose Apply upload limit or Remove again to be sure.", refused.Explanation);
        var mismatch = new UploadLimitResult(UploadLimitAction.Remove, UploadLimitStatus.NotConfirmed, 0, 3, new(UploadLimitAction.Remove, 1000, DateTimeOffset.UtcNow, false));
        Assert.Equal("Windows did not confirm the change: a policy Zenith read back afterwards did not match. Whether a limit is in force is now uncertain; choose Apply upload limit or Remove again to be sure.", mismatch.Explanation);
        var timedOut = new UploadLimitResult(UploadLimitAction.Apply, UploadLimitStatus.TimedOut, 1000, 0, null);
        Assert.Equal("Windows did not answer within 60 seconds. " + UploadLimitResult.PendingHelper, timedOut.Explanation);
        var cancelled = new UploadLimitResult(UploadLimitAction.Apply, UploadLimitStatus.Cancelled, 1000, 0, null);
        Assert.Equal("The upload limit wait was stopped. No unresolved helper remains; review the current result before retrying.", cancelled.Explanation);
        Assert.Equal("Windows PowerShell could not be started, so nothing was changed.", new UploadLimitResult(UploadLimitAction.Apply, UploadLimitStatus.Unavailable, 1000, 0, null).Explanation);
        Assert.Equal("Another upload limit operation is still running. Wait for it or choose Cancel.", new UploadLimitResult(UploadLimitAction.Remove, UploadLimitStatus.Busy, 0, 0, null).Explanation);
        Assert.Equal("Upload limit controls are closed.", new UploadLimitResult(UploadLimitAction.Remove, UploadLimitStatus.Closed, 0, 0, null).Explanation);
        Assert.Equal(UploadLimitPolicy.RateRequirement, new UploadLimitResult(UploadLimitAction.Apply, UploadLimitStatus.InvalidRate, 7, 0, null).Explanation);
        // A confirmed change whose record could not be saved says so, because the next run of Zenith will not show it.
        var unsaved = new UploadLimitResult(UploadLimitAction.Apply, UploadLimitStatus.Confirmed, 1000, 0, new(UploadLimitAction.Apply, 1000, DateTimeOffset.UtcNow, true), RecordSaved: false);
        Assert.EndsWith(" Zenith could not save its record of this change (upload-limit.json in the data folder), so the next run of Zenith will not show it.", unsaved.Explanation);
        Assert.StartsWith(applied.Explanation, unsaved.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void ARecordBelongsToOneWindowsSessionJudgedByBootTimeWithinTwoMinutes()
    {
        var boot = new DateTimeOffset(2026, 11, 8, 6, 0, 0, TimeSpan.Zero);
        Assert.True(UploadLimitPolicy.SameWindowsSession(boot, boot.AddSeconds(90)));
        Assert.True(UploadLimitPolicy.SameWindowsSession(boot.AddSeconds(90), boot));
        Assert.False(UploadLimitPolicy.SameWindowsSession(boot, boot.AddMinutes(3)));
        Assert.False(UploadLimitPolicy.SameWindowsSession(boot, boot.AddHours(-5)));
    }
}
