using System.IO;
using Zenith.Sessions.App;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class RetainedSignInBrowserTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-kept-signin-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RetentionIsOptInAndSurvivesSettingsRoundTrip()
    {
        Assert.False(new SessionsSettings().KeepSignInBrowserData);
        var old = System.Text.Json.JsonSerializer.Deserialize<SessionsSettings>("{\"Version\":1}")!.Validated();
        Assert.False(old.KeepSignInBrowserData);
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<SessionsSettings>(
            System.Text.Json.JsonSerializer.Serialize(old with { KeepSignInBrowserData = true }))!.Validated();
        Assert.True(roundTrip.KeepSignInBrowserData);
    }

    [Fact]
    public void CompletedCleanupKeepsOnlyTheOwnedFolderAndASecondSignInReusesIt()
    {
        var first = SignInProfileDirectory.Create(_root, Guid.NewGuid(), true, default);
        var cache = Path.Combine(first.Path, "synthetic-device-cache"); File.WriteAllText(cache, "synthetic cache");
        first.CompleteAfterBrowserExit(false, default);
        Assert.True(File.Exists(cache)); Assert.False(SignInProfileDirectory.HasLiveOwner(_root));
        var sweep = SignInProfileDirectory.SweepOwned(_root, default);
        Assert.Equal(0, sweep.Removed); Assert.False(sweep.NeedsReview); Assert.True(File.Exists(cache));
        var next = SignInProfileDirectory.Create(_root, Guid.NewGuid(), true, default);
        Assert.Equal(first.Path, next.Path); Assert.True(File.Exists(cache));
        Assert.False(File.Exists(Path.Combine(next.Path, ".zenith-kept-ready")));
        next.CompleteAfterBrowserExit(false, default);
        SignInProfileDirectory.ForgetKept(_root, default); Assert.False(Directory.Exists(next.Path));
    }

    [Fact]
    public void InterruptedKeptSignInIsRemovedBeforeAnotherBrowserCanReuseIt()
    {
        var first = SignInProfileDirectory.Create(_root, Guid.NewGuid(), true, default);
        string uncertain = Path.Combine(first.Path, "synthetic-uncertain-browser-data"); File.WriteAllText(uncertain, "not reusable");
        first.ReleaseOwnership(); // Simulated process crash; no completed-cleanup marker.
        var next = SignInProfileDirectory.Create(_root, Guid.NewGuid(), true, default);
        Assert.False(File.Exists(uncertain)); next.Remove(default);
    }

    [Fact]
    public void DisposableSignInLeavesACleanOptedInCacheAlone()
    {
        var kept = SignInProfileDirectory.Create(_root, Guid.NewGuid(), true, default);
        kept.CompleteAfterBrowserExit(false, default);
        var disposable = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        Assert.False(disposable.RetainAfterUse); Assert.NotEqual(kept.Path, disposable.Path);
        disposable.CompleteAfterBrowserExit(false, default);
        Assert.True(Directory.Exists(kept.Path)); Assert.False(Directory.Exists(disposable.Path));
        SignInProfileDirectory.ForgetKept(_root, default);
    }

    [Fact]
    public void ForgetAndOtherAdmissionCannotRemoveAnActiveBrowser()
    {
        var active = SignInProfileDirectory.Create(_root, Guid.NewGuid(), true, default);
        try
        {
            Assert.Throws<SignInDirectoryInUseException>(() => SignInProfileDirectory.ForgetKept(_root, default));
            Assert.Throws<SignInDirectoryInUseException>(() => SignInProfileDirectory.Create(_root, Guid.NewGuid(), default));
            Assert.True(Directory.Exists(active.Path));
        }
        finally { active.Remove(default); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProviderNavigationOrCleanupProblemDiscardsTheKeptBrowser(bool providerNavigation)
    {
        var directory = SignInProfileDirectory.Create(_root, Guid.NewGuid(), true, default);
        if (providerNavigation) directory.DiscardAfterUse();
        directory.CompleteAfterBrowserExit(!providerNavigation, default);
        Assert.False(Directory.Exists(directory.Path));
    }

    [Fact]
    public async Task CookieDeletionFailureStillRemovesTheCacheAfterBrowserExit()
    {
        var directory = SignInProfileDirectory.Create(_root, Guid.NewGuid(), true, default);
        await OwnedRobloxSignIn.TeardownAsync(() => throw new InvalidOperationException("synthetic cookie deletion failure"),
            () => { }, () => { }, true, null, Task.CompletedTask, directory, () => { });
        Assert.False(Directory.Exists(directory.Path));
    }

    [Fact]
    public void SessionCookieDeletionUsesOnlyItsNameAcrossAllDomainsAndPaths()
    {
        var calls = new List<(string, string?)>();
        OwnedRobloxSignIn.ClearRobloxSessionCookies((name, uri) => calls.Add((name, uri)));
        Assert.Equal(new[] { (".ROBLOSECURITY", (string?)null) }, calls);
    }

    [Fact]
    public void UnknownKeptFolderIsPreservedInsteadOfAdopted()
    {
        string folder = Path.Combine(_root, "signin-browser-data", "kept"); Directory.CreateDirectory(folder);
        string foreign = Path.Combine(folder, "foreign"); File.WriteAllText(foreign, "preserve");
        Assert.Throws<SignInDirectoryNeedsReviewException>(() => SignInProfileDirectory.Create(_root, Guid.NewGuid(), true, default));
        Assert.Throws<SignInDirectoryNeedsReviewException>(() => SignInProfileDirectory.ForgetKept(_root, default));
        Assert.True(File.Exists(foreign));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
