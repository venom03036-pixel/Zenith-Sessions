using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// R-instances-18: the production runner starts Windows PowerShell 5.1 (the only PowerShell on a stock Windows PC) and reads
// its exit code. Nothing here is run as administrator and nothing on this PC changes: the scripts are parsed without running,
// and the unelevated run is refused by Windows, which is exactly the exit-code path the control reports as Refused.
public sealed class UploadLimitNativeTests
{
    [Fact]
    public void WindowsPowerShellIsPresentAndTheArgumentsCarryTheScriptEncodedWithoutQuotesOrAnExecutionPolicyOverride()
    {
        Assert.True(File.Exists(ElevatedPowerShellRunner.PowerShellPath), ElevatedPowerShellRunner.PowerShellPath);
        Assert.EndsWith(Path.Combine("System32", "WindowsPowerShell", "v1.0", "powershell.exe"), ElevatedPowerShellRunner.PowerShellPath, StringComparison.OrdinalIgnoreCase);
        string script = UploadLimitPolicy.ApplyScript(1000);
        string arguments = ElevatedPowerShellRunner.Arguments(script);
        const string prefix = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand ";
        Assert.StartsWith(prefix, arguments, StringComparison.Ordinal);
        Assert.Equal(script, Encoding.Unicode.GetString(Convert.FromBase64String(arguments[prefix.Length..])));
        Assert.DoesNotContain('"', arguments);
        Assert.DoesNotContain("ExecutionPolicy", arguments, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BothScriptsParseUnderWindowsPowerShell51WithoutRunningAnything()
    {
        foreach (string script in new[] { UploadLimitPolicy.ApplyScript(100), UploadLimitPolicy.ApplyScript(10_000_000), UploadLimitPolicy.RemoveScript() })
        {
            Assert.DoesNotContain("'@", script, StringComparison.Ordinal);
            // A here-string keeps the script literal; [scriptblock]::Create parses it and runs none of it.
            string check = "$ErrorActionPreference = 'Stop'; $null = [scriptblock]::Create(@'\n" + script + "\n'@); exit 0";
            var start = new ProcessStartInfo(ElevatedPowerShellRunner.PowerShellPath)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(check)));
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows PowerShell did not start.");
            string errors = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
            Assert.True(process.ExitCode == 0, "Exit " + process.ExitCode + ": " + errors);
        }
    }

    [Fact]
    public async Task UnelevatedBothScriptsAreRefusedByWindowsAndTheRunnerReportsExitOne()
    {
        // Only without administrator rights: with them the same run would change this PC's QoS policies, which no test may do.
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return;
        var runner = new ElevatedPowerShellRunner(elevate: false);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        foreach (string script in new[] { UploadLimitPolicy.ApplyScript(100), UploadLimitPolicy.RemoveScript() })
        {
            var outcome = await runner.RunAsync(script, TimeSpan.FromSeconds(60), budget.Token);
            Assert.Equal(ElevatedRunKind.Exited, outcome.Kind);
            // Remove-NetQosPolicy's refusal is silenced, so it is the readback that is denied; either way the run ends with 1,
            // never with the 0 that would claim a change, and the control reports it as Refused.
            Assert.Equal(1, outcome.ExitCode);
            Assert.Equal(UploadLimitStatus.Refused, UploadLimitPolicy.StatusForExit(outcome.ExitCode));
        }
        await runner.ReleaseAsync(budget.Token);
    }
}
