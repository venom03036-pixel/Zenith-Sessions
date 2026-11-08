using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zenith.Sessions.Windows;

public sealed class ReleaseToolUpdateBackend(string toolPath, string stagingRoot, Version currentVersion) : IReleaseUpdateBackend
{
    public async Task<StagedUpdate> StageAsync(ReleaseUpdateSettings settings, CancellationToken ct)
    {
        ReleaseUpdateSession.Validate(settings);
        SignedUpdateStager.AssertNoRedirects(toolPath);
        using var lockedTool = new FileStream(toolPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        new WindowsUpdatePublisherVerifier().Verify(lockedTool, settings.PublisherCertificateSha256!, ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); stop.CancelAfter(TimeSpan.FromMinutes(4));
        var start = new ProcessStartInfo(Path.GetFullPath(toolPath))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(toolPath))! };
        start.ArgumentList.Add("stage"); start.ArgumentList.Add(settings.FeedUri!); start.ArgumentList.Add(currentVersion.ToString()); start.ArgumentList.Add(Path.GetFullPath(stagingRoot));
        using var process = Process.Start(start) ?? throw new IOException("The verified update helper could not start.");
        Task<string> output = ReadBoundedAsync(process.StandardOutput, stop.Token), error = ReadBoundedAsync(process.StandardError, stop.Token);
        try
        {
            var exit = process.WaitForExitAsync(stop.Token);
            var pending = new List<Task> { output, error, exit };
            // Observe each completion: a pipe fault must stop the helper even when the other pipe completed first.
            while (pending.Count > 0) { var done = await Task.WhenAny(pending).ConfigureAwait(false); await done.ConfigureAwait(false); pending.Remove(done); }
            if (process.ExitCode != 0) throw new InvalidOperationException("The signed update helper refused this release. Installed files were unchanged.");
            var staged = JsonSerializer.Deserialize<StagedUpdate>(await output.ConfigureAwait(false)) ?? throw new InvalidDataException("Invalid helper result.");
            string root = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(staged.Directory).StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Version.TryParse(staged.Version, out _))
                throw new InvalidDataException("Update helper returned a stage outside its owned root.");
            return staged;
        }
        catch (Exception operationError)
        {
            stop.Cancel();
            Exception? shutdownError = null;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var killBudget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await process.WaitForExitAsync(killBudget.Token).ConfigureAwait(false);
                }
            }
            catch (Exception shutdownFailure) when (shutdownFailure is not OutOfMemoryException) { shutdownError = shutdownFailure; }
            // Drain both tracked pipe tasks before releasing their process/stream owner.
            try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch (Exception) { /* Original operation failure is propagated below. */ }
            if (shutdownError is not null)
                throw new AggregateException("The update helper could not be confirmed stopped; review it before retrying. Installed files were unchanged.", operationError, shutdownError);
            throw;
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var result = new StringBuilder(); char[] buffer = new char[2048];
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) return result.ToString();
            if (result.Length + read > 16384) throw new InvalidDataException("Update helper output exceeded its bound.");
            result.Append(buffer, 0, read);
        }
    }
}
