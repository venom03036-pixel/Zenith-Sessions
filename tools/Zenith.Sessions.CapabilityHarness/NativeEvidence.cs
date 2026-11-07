using System.Text.Json;

namespace Zenith.Sessions.Capabilities;

public sealed record NativeCheck(string Name, bool Passed, string Detail);
public sealed record NativeProbeReport(int SchemaVersion, string CollectorVersion, EvidenceOrigin Origin,
    DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, bool OwnWindowsOnly,
    IReadOnlyList<NativeCheck> Checks, string Limitation);

public static class NativeEvidence
{
    public static async Task<NativeProbeReport> ReadAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 65536) throw new InvalidDataException("Native report exceeds its bound.");
        var options = new JsonSerializerOptions(Reports.JsonOptions)
        { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, MaxDepth = 12 };
        byte[] bytes = new byte[65537];
        int read = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, ct);
        if (read > 65536) throw new InvalidDataException("Native report exceeds its bound.");
        ct.ThrowIfCancellationRequested();
        var report = JsonSerializer.Deserialize<NativeProbeReport>(bytes.AsSpan(0, read), options)
            ?? throw new InvalidDataException("Native report is empty.");
        if (report.SchemaVersion != 1 || report.CollectorVersion is not ("zenith-native-probe/0.1.0" or "zenith-native-probe/0.2.0" or "zenith-native-probe/0.3.0")
            || report.Origin != EvidenceOrigin.NativeControlledExercise || !report.OwnWindowsOnly
            || string.IsNullOrWhiteSpace(report.Limitation) || report.Limitation.Length > 2000
            || report.Checks is null || report.Checks.Count is < 1 or > 32
            || report.Checks.Any(c => c is null || c.Name is null || c.Detail is null || c.Name.Length > 100 || c.Detail.Length > 600)
            || report.CompletedUtc < report.StartedUtc || report.CompletedUtc - report.StartedUtc > TimeSpan.FromMinutes(2)
            || report.CompletedUtc > DateTimeOffset.UtcNow.AddMinutes(1)
            || report.CompletedUtc < DateTimeOffset.UtcNow.AddHours(-24))
            throw new InvalidDataException("Native report provenance, bounds or timestamps are invalid.");
        return report;
    }
}
