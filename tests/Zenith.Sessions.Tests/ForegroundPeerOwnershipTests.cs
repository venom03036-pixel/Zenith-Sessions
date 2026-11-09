using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Xunit;
using Zenith.MacroHost.Contracts;
using Zenith.MacroHost.Windows;

namespace Zenith.Sessions.Tests;

public sealed class ForegroundPeerOwnershipTests
{
    [Fact]
    public async Task RequiresTheExactPeerImageLiveChildAndWindowIncarnation()
    {
        string worker = TestWorkerLocator.Find("Zenith.MacroHost.ProbeWorker", "net8.0-windows10.0.19041.0");
        string root = Path.Combine(Path.GetTempPath(), "zenith-foreground-ownership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(worker)!))
                if (Path.GetExtension(file) is ".dll" or ".json")
                    File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
            string executable = Path.Combine(root, "RobloxPlayerBeta.exe");
            File.Copy(worker, executable);
            using var process = Process.Start(new ProcessStartInfo(executable, "--ownership-window-fixture")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = root
            })!;
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                string? ready = await process.StandardOutput.ReadLineAsync(deadline.Token);
                Assert.True(long.TryParse(ready, NumberStyles.Integer, CultureInfo.InvariantCulture, out long window));
                long birth = process.StartTime.ToFileTimeUtc();
                var request = new ForegroundControlRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                    window, process.Id, birth);
                using var image = File.OpenRead(Environment.ProcessPath!);
                string digest = Convert.ToHexString(SHA256.HashData(image));
                var trusted = new ForegroundPeerOwnership(digest);
                Assert.True(trusted.Authorizes(Environment.ProcessId, request, deadline.Token));
                Assert.False(trusted.Authorizes(process.Id, request, deadline.Token));
                Assert.False(trusted.Authorizes(Environment.ProcessId, request with { CreationFileTime = birth + 1 }, deadline.Token));
                Assert.False(trusted.Authorizes(Environment.ProcessId, request with { Window = window + 1 }, deadline.Token));
                Assert.False(new ForegroundPeerOwnership(new string('0', 64)).Authorizes(Environment.ProcessId, request, deadline.Token));
                await process.StandardInput.WriteLineAsync("EXIT");
                await process.WaitForExitAsync(deadline.Token);
                Assert.False(trusted.Authorizes(Environment.ProcessId, request, deadline.Token));
            }
            finally
            {
                if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            }
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(root)) File.Delete(file);
            Directory.Delete(root);
        }
    }
}
