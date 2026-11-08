using System.IO;

namespace Zenith.Sessions.Tests;

/// <summary>
/// The one copy of the probes that DiagnosticsSnapshotTests and DiagnosticsViewModelTests use to watch the diagnostics
/// background writer reach manager.jsonl. The disk raises no completion event to observe, so these poll, within a bound.
/// </summary>
internal static class DiagnosticsWriterProbe
{
    /// <summary>Checks <paramref name="condition"/> every 10 ms and throws <see cref="TimeoutException"/> after 10 seconds.</summary>
    internal static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The diagnostics writer did not reach the expected state.");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> for reading and denies writers, so the writer's next append fails as a sharing
    /// violation. Returns null while another handle can still write to the file. The caller owns and disposes the stream.
    /// </summary>
    internal static FileStream? TryOpenDenyingWriters(string path)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (IOException) { return null; }
    }
}
