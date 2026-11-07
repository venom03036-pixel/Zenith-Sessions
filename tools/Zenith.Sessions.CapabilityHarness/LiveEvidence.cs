using System.Security.Cryptography;

namespace Zenith.Sessions.Capabilities;

public sealed record VerifiedClientInstance(string HostId, int ProcessId, DateTimeOffset ProcessStartedUtc,
    string WindowIdentity, long VerifiedUserId, string IdentityBindingEvidence);

/// <summary>Metadata for an artifact verified by a collector. This is not an arbitrary JSON gate override.</summary>
public sealed record LiveExerciseEvidence(string SuiteFingerprint, string InputAdapterVersion,
    string CaptureAdapterVersion, string LaunchAdapterVersion, DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc, int SuccessfulTrials, IReadOnlyList<VerifiedClientInstance> Clients,
    IReadOnlyList<VerifiedArtifact> Artifacts, bool ObservedOutcomeVerified, int AttemptedTrials = 0);

public sealed class VerifiedArtifact
{
    private readonly string _path;
    private VerifiedArtifact(string path, string hash) { _path = path; Name = Path.GetFileName(path); Sha256 = hash; }
    public string Name { get; }
    public string Sha256 { get; }

    // Only collector-produced bytes are accepted, never a caller's claimed hash or arbitrary path.
    public static async Task<VerifiedArtifact> PersistAsync(string evidenceDirectory, string name,
        ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || Path.GetFileName(name) != name
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            throw new ArgumentException("Evidence requires a simple file name.", nameof(name));
        if (bytes.IsEmpty || bytes.Length > 4 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(bytes), "Evidence is limited to 4 MiB per artifact.");
        ct.ThrowIfCancellationRequested();
        string hash = Convert.ToHexString(SHA256.HashData(bytes.Span));
        string root = Path.GetFullPath(evidenceDirectory);
        Directory.CreateDirectory(root);
        // Content-address the artifact so a later collector cannot overwrite another trial's evidence.
        string path = Path.Combine(root, hash + "." + name);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        ct.ThrowIfCancellationRequested();
        byte[] saved = await File.ReadAllBytesAsync(path, ct);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(saved), Convert.FromHexString(hash)))
            throw new IOException("Evidence read-back hash mismatch.");
        return new(path, hash);
    }

    internal bool StillMatches()
    {
        try
        {
            using var stream = File.OpenRead(_path);
            return stream.Length is > 0 and <= 4 * 1024 * 1024
                && CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(Sha256));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

public static class LiveEvidencePolicy
{
    public static IEnumerable<string> Validate(CapabilityAssessment fact, DateTimeOffset now)
    {
        var e = fact.Evidence;
        if (e is null) { yield return "Collector evidence is missing; status labels cannot approve a live exercise."; yield break; }
        if (e.SuiteFingerprint is not { Length: 64 } || !e.SuiteFingerprint.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(e.InputAdapterVersion) || string.IsNullOrWhiteSpace(e.CaptureAdapterVersion)
            || string.IsNullOrWhiteSpace(e.LaunchAdapterVersion))
            yield return "A complete versioned input/capture/launch configuration fingerprint is required.";
        if (e.StartedUtc > e.CompletedUtc || e.CompletedUtc > now.AddMinutes(1)
            || e.CompletedUtc < now.AddHours(-24) || e.CompletedUtc - e.StartedUtc > TimeSpan.FromHours(4)
            || fact.ObservedAtUtc != e.CompletedUtc)
            yield return "Evidence timestamps are stale, inconsistent or outside the observation window.";
        if (e.SuccessfulTrials < 3 || e.SuccessfulTrials > 1000 || e.AttemptedTrials != e.SuccessfulTrials || !e.ObservedOutcomeVerified)
            yield return "At least three independently observed successful trials are required.";
        if (e.Clients.Count < 2 || e.Clients.Count > 128
            || e.Clients.Any(c => string.IsNullOrWhiteSpace(c.HostId) || c.ProcessId <= 0 || c.VerifiedUserId <= 0
                || c.ProcessStartedUtc > e.StartedUtc || string.IsNullOrWhiteSpace(c.WindowIdentity)
                || string.IsNullOrWhiteSpace(c.IdentityBindingEvidence))
            || e.Clients.Select(c => c.VerifiedUserId).Distinct().Count() < 2
            || e.Clients.Select(c => (c.HostId, c.ProcessId, c.ProcessStartedUtc)).Distinct().Count() != e.Clients.Count)
            yield return "Two distinct verified account/process instances and their identity-binding evidence are required.";
        if (e.Artifacts.Count is < 1 or > 32 || e.Artifacts.Any(a => a is null || !a.StillMatches()))
            yield return "Bounded, persisted collector artifacts are required.";
    }
}
