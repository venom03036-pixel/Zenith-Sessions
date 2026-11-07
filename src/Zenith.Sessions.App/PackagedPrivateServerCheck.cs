using System.IO;
using System.Text;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

// Only --self-check uses this. It runs the shipped PrivateServerStore's own protected file and JSON path with one
// synthetic code under the owned fixture folder. Nothing is resolved, joined or sent anywhere, and failures report
// only the stage's exception type (never the code).
internal static class PackagedPrivateServerCheck
{
    internal const string FixtureName = "Synthetic private server";
    internal const long FixturePlaceId = 6564161046;
    private const long FixtureUniverseId = 1;
    private const string FixtureCode = "SyntheticCode0123";

    public static async Task VerifyAsync(string root, CancellationToken ct)
    {
        using var servers = new PrivateServerStore(Path.Combine(root, "private-server-fixture"));
        // The fixture folder is reused across runs: clear what an aborted earlier run left, but never another record.
        var prior = await servers.ReadAsync(ct);
        if (prior.Any(item => item.Name != FixtureName || item.PlaceId != FixturePlaceId))
            throw new InvalidDataException("The private-server fixture contains records it did not create; they were preserved.");
        foreach (var item in prior) await servers.DeleteAsync(item.Id, item.Revision, ct);

        SavedPrivateServer saved;
        using (var code = new ProtectedPrivateServerCode(FixtureCode))
            saved = await servers.AddAsync(FixtureName, FixturePlaceId, FixtureUniverseId, null, code, ct);
        var listed = await servers.ReadAsync(ct);
        if (listed.Count != 1 || listed[0] != saved || saved.Last4 != FixtureCode[^4..])
            throw new InvalidDataException("Saved private-server details did not round trip.");
        using (var revealed = await servers.ReadCodeAsync(saved.Id, ct))
        {
            bool same = false;
            revealed.Reveal(span => same = span.SequenceEqual(FixtureCode));
            if (!same) throw new InvalidDataException("The protected private-server code did not round trip.");
        }
        byte[] stored = await File.ReadAllBytesAsync(servers.Path, ct);
        if (stored.AsSpan().IndexOf(Encoding.UTF8.GetBytes(FixtureCode)) >= 0 || stored.AsSpan().IndexOf(Encoding.Unicode.GetBytes(FixtureCode)) >= 0)
            throw new InvalidDataException("The private-server file holds the code in clear text.");

        await servers.DeleteAsync(saved.Id, saved.Revision, ct);
        if ((await servers.ReadAsync(ct)).Count != 0)
            throw new InvalidDataException("The synthetic private server was not removed.");
    }
}
