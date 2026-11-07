namespace Zenith.Sessions.Tests;

// Entirely SYNTHETIC. These lines test mechanics against publicly described marker shapes; they are
// not captured/sanitized real Roblox logs and do not establish support for any client version.
internal static class SyntheticClientLogFixtures
{
    public static readonly DateTimeOffset Time = new(2026, 11, 5, 14, 0, 0, TimeSpan.Zero);
    public static readonly Guid Job = Guid.Parse("2f81cfe1-981e-4527-9e5d-c12edbb62f70");
    public const long Place = 110829983956014, User = 5_000_000_123;
    public static string Line(string tag, string body) => $"{Time:O},0.1,abcd,6,Info [{tag}] {body}\n";
    public static string Joining => Line("FLog::Output", $"! Joining game '{Job:D}' place {Place} at synthetic-address");
    public static string Connected => Line("FLog::Network", "Replicator created: synthetic");
    public static string Identity => Line("FLog::GameJoinLoadTime", $"Report game_join_loadtime: userid:{User}");
    public static string Teleporting => Line("FLog::UgcExperienceController", "UgcExperienceController: doTeleport");
    public static string Disconnected => Line("FLog::Network", "Lost connection with reason: synthetic");
}
