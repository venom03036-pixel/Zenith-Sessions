using System.IO;
using System.Text;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

/// <summary>
/// Exercises the real cleanup against synthetic files in a temporary trusted anchor. It never touches the current user's
/// Roblox folders, and the only "running client" is a file handle this test host holds open itself.
/// </summary>
public sealed class RobloxClientLogCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ZenithClientLogCleanup-" + Guid.NewGuid().ToString("N"));
    private string Anchor => Path.Combine(_root, "anchor");
    private string Win32 => Path.Combine(Anchor, "Roblox", "logs");
    private string Uwp => Path.Combine(Anchor, "Packages", "synthetic", "LocalState", "logs");
    private readonly List<string> _watched = [];

    public RobloxClientLogCleanupTests() => Directory.CreateDirectory(Anchor);
    public void Dispose() => Directory.Delete(_root, true);

    private static string Synthetic(string folder, string name, TimeSpan age, int bytes = 8)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, new string('x', bytes), new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    private RobloxClientLogCleanup Cleanup(params (string Path, ClientLogVariant Variant)[] roots)
        => new(roots.Length == 0 ? [(Win32, ClientLogVariant.Win32), (Uwp, ClientLogVariant.Uwp)] : roots, Anchor, () => _watched.ToArray());

    [Fact]
    public async Task SurveyCountsOnlyPlayerLogsOlderThanTheCutoffAndKeepsWatchedHeldAndNewerOnes()
    {
        string a = Synthetic(Win32, "a_Player_1_last.log", TimeSpan.FromDays(10), 1000);
        string b = Synthetic(Uwp, "b_Player_2_last.log", TimeSpan.FromDays(30), 500);
        string watched = Synthetic(Win32, "c_Player_3_last.log", TimeSpan.FromDays(10));
        string held = Synthetic(Win32, "d_Player_4_last.log", TimeSpan.FromDays(10));
        Synthetic(Win32, "e_Player_5_last.log", TimeSpan.FromHours(20)); // Newer than the cutoff.
        Synthetic(Win32, "f_Player_6_last.log", TimeSpan.FromDays(6.5)); // Still newer than seven days.
        Synthetic(Win32, "g_Studio_7_last.log", TimeSpan.FromDays(10)); // Not a Player log: not counted at all.
        Synthetic(Win32, "h_Player_8_last.txt", TimeSpan.FromDays(10)); // Not a .log file.
        _watched.Add(watched);
        // A running client keeps its log open; the CRT shares reads, so an exclusive-share open must be refused.
        using var holder = new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

        var survey = await Cleanup().SurveyAsync(TimeSpan.FromDays(7), CancellationToken.None);

        Assert.Equal(new[] { a, b }.OrderBy(path => path, StringComparer.Ordinal), survey.Removable.Select(file => file.Path).OrderBy(path => path, StringComparer.Ordinal));
        Assert.Equal(1500, survey.RemovableBytes);
        Assert.Equal(2, survey.KeptRecent);
        Assert.Equal(2, survey.KeptInUse);
        Assert.Equal(0, survey.KeptUnchecked);
        Assert.Equal(2, survey.FoldersChecked);
        Assert.False(survey.BudgetLimited);
        Assert.Equal(TimeSpan.FromDays(7), survey.OlderThan);
    }

    [Fact]
    public async Task DeleteRemovesOnlyTheSurveyedFilesThatStillQualifyAndKeepsChangedHeldOrNewlyWatchedOnes()
    {
        string gone = Synthetic(Win32, "a_Player_1_last.log", TimeSpan.FromDays(10), 300);
        string grew = Synthetic(Win32, "b_Player_2_last.log", TimeSpan.FromDays(10));
        string held = Synthetic(Win32, "c_Player_3_last.log", TimeSpan.FromDays(10));
        string watchedLater = Synthetic(Win32, "d_Player_4_last.log", TimeSpan.FromDays(10));
        string vanished = Synthetic(Win32, "e_Player_5_last.log", TimeSpan.FromDays(10));
        var cleanup = Cleanup();
        var survey = await cleanup.SurveyAsync(TimeSpan.FromDays(7), CancellationToken.None);
        Assert.Equal(5, survey.Removable.Count);

        // Between the check and the confirmation: one log was written again, one is held by a client, one became
        // evidence the correlator watches, and one is already gone.
        File.AppendAllText(grew, "more");
        using var holder = new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        _watched.Add(watchedLater);
        File.Delete(vanished);

        var result = await cleanup.DeleteAsync(survey, CancellationToken.None);

        Assert.Equal(1, result.Deleted);
        Assert.Equal(300, result.DeletedBytes);
        Assert.Equal(2, result.KeptInUse);
        Assert.Equal(2, result.KeptChanged);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.NotAttempted);
        Assert.False(result.Stopped);
        Assert.False(File.Exists(gone));
        Assert.True(File.Exists(grew)); Assert.True(File.Exists(held)); Assert.True(File.Exists(watchedLater));
    }

    [Fact]
    public async Task ACancelledDeleteStopsAtOnceAndReportsWhatItDidNotTouch()
    {
        for (int index = 0; index < 4; index++) Synthetic(Win32, $"{index}_Player_{index}_last.log", TimeSpan.FromDays(10));
        var cleanup = Cleanup();
        var survey = await cleanup.SurveyAsync(TimeSpan.FromDays(7), CancellationToken.None);
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();

        var result = await cleanup.DeleteAsync(survey, stopped.Token);

        Assert.True(result.Stopped);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(4, result.NotAttempted);
        Assert.Equal(4, Directory.GetFiles(Win32).Length);
    }

    [Fact]
    public async Task RootsOutsideTheAnchorOrMissingAreNeverReadAndADayIsTheShortestCutoff()
    {
        string outside = Path.Combine(_root, "elsewhere", "logs");
        Synthetic(outside, "a_Player_1_last.log", TimeSpan.FromDays(10));
        var cleanup = Cleanup((outside, ClientLogVariant.Win32), (Win32, ClientLogVariant.Win32)); // Win32 does not exist yet.

        var survey = await cleanup.SurveyAsync(TimeSpan.FromDays(7), CancellationToken.None);

        Assert.Empty(survey.Removable);
        Assert.Equal(0, survey.FoldersChecked);
        Assert.True(File.Exists(Path.Combine(outside, "a_Player_1_last.log")));
        // Shorter than a day would reach the logs the correlator still discovers (its one-day window).
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cleanup.SurveyAsync(TimeSpan.FromHours(23), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cleanup.SurveyAsync(TimeSpan.FromDays(366), CancellationToken.None));
    }

    [Fact]
    public async Task ASurveyOverMoreFilesThanOneRunMayInspectReportsItsLimit()
    {
        for (int index = 0; index < RobloxClientLogCleanup.MaximumRemovable + 5; index++)
            Synthetic(Win32, $"{index:D5}_Player_{index}_last.log", TimeSpan.FromDays(10), 1);

        var survey = await Cleanup().SurveyAsync(TimeSpan.FromDays(7), CancellationToken.None);

        Assert.Equal(RobloxClientLogCleanup.MaximumRemovable, survey.Removable.Count);
        Assert.True(survey.BudgetLimited);
    }
}
