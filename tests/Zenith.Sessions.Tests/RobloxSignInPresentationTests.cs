using System.IO;
using System.Windows.Threading;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class RobloxSignInPresentationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-owned-signin-" + Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData("https://www.roblox.com/login", true)]
    [InlineData("https://roblox.com/home", true)]
    [InlineData("https://apis.roblox.com/", true)]
    [InlineData("https://WWW.ROBLOX.COM:443/", true)]
    [InlineData("https://roblox.com.evil.invalid/", false)]
    [InlineData("https://evilroblox.com/", false)]
    [InlineData("https://www.roblox.com:1234/login", false)]
    [InlineData("https://roblox.com@evil.invalid/", false)]
    [InlineData("https://user@www.roblox.com/", false)]
    [InlineData("http://www.roblox.com/login", false)]
    [InlineData("file:///C:/Windows/test.html", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("roblox-player:launch", false)]
    [InlineData("https://accounts.google.com/", false)]
    [InlineData(null, false)]
    public void TopLevelSignInNavigationCannotDispatchExternalPages(string? value, bool expected)
        => Assert.Equal(expected, RobloxSignInNavigation.IsAllowed(value));

    [Fact]
    public void OwnedPrivateDirectoryRejectsAnActiveOwnerAndCanBeReusedAfterCleanup()
    {
        var id = Guid.NewGuid(); var first = SignInProfileDirectory.Create(_root, id, default);
        Assert.Throws<IOException>(() => SignInProfileDirectory.Create(_root, id, default));
        Assert.StartsWith(Path.GetFullPath(_root), first.Path);
        Directory.CreateDirectory(Path.Combine(first.Path, "cache")); File.WriteAllText(Path.Combine(first.Path, "cache", "synthetic"), "test only");
        first.Remove(default); Assert.False(Directory.Exists(first.Path));
        var second = SignInProfileDirectory.Create(_root, id, default); Assert.NotEqual(first.Path, second.Path);
        second.Remove(default);
    }
    [Fact]
    public void RecoverableMarkedOldFolderIsCleanedBeforeAnotherIsAllocated()
    {
        var old = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default); old.ReleaseOwnership();
        var next = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        Assert.False(Directory.Exists(old.Path)); Assert.True(Directory.Exists(next.Path)); next.Remove(default);
    }
    [Fact]
    public void UnknownOldDirectoryBlocksCleanupAndIsPreserved()
    {
        var old = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default); old.Remove(default);
        string unknown=Path.Combine(_root,"signin-browser-data","unknown"); Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown,"keep.txt"),"keep");
        Assert.Throws<IOException>(()=>SignInProfileDirectory.Create(_root,Guid.NewGuid(),default)); Assert.True(File.Exists(Path.Combine(unknown,"keep.txt")));
    }
    [Fact]
    public void FailedCleanupKeepsOwnershipMarkersAndRecoversAfterRelease()
    {
        var d = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        string nested = Path.Combine(d.Path, "EBWebView", "Default"); Directory.CreateDirectory(nested);
        string locked = Path.Combine(nested, "locked"); File.WriteAllText(locked, "test only");
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => d.Remove(default));
            Assert.True(File.Exists(Path.Combine(d.Path, ".zenith-private-signin")), "A failed cleanup deleted the ownership marker.");
            Assert.True(File.Exists(Path.Combine(d.Path, "owner.lock")), "A failed cleanup deleted the owner lock.");
            Assert.Throws<IOException>(() => SignInProfileDirectory.Create(_root, Guid.NewGuid(), default));
        }
        d.ReleaseOwnership();
        var next = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        Assert.False(Directory.Exists(d.Path)); next.Remove(default);
    }
    [Fact]
    public void RecoveryAcceptsMarkedFolderWhoseLockIsMissing()
    {
        var old = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default); old.ReleaseOwnership();
        File.Delete(Path.Combine(old.Path, "owner.lock"));
        var next = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        Assert.False(Directory.Exists(old.Path)); next.Remove(default);
    }
    [Fact]
    public void RecoveryRemovesOwnedFoldersThatHoldNoBrowserDataButPreservesUnmarkedData()
    {
        string browserRoot = Path.Combine(_root, "signin-browser-data");
        string OwnedName() => Path.Combine(browserRoot, Guid.NewGuid().ToString("N") + "-" + Guid.NewGuid().ToString("N"));
        string empty = OwnedName(); Directory.CreateDirectory(empty);
        string shortMarker = OwnedName(); Directory.CreateDirectory(shortMarker); File.WriteAllText(Path.Combine(shortMarker, ".zenith-private-signin"), "");
        var next = SignInProfileDirectory.Create(_root, Guid.NewGuid(), default);
        Assert.False(Directory.Exists(empty)); Assert.False(Directory.Exists(shortMarker)); next.Remove(default);
        string unmarked = OwnedName(); Directory.CreateDirectory(unmarked); File.WriteAllText(Path.Combine(unmarked, "keep.txt"), "keep");
        Assert.Throws<IOException>(() => SignInProfileDirectory.Create(_root, Guid.NewGuid(), default));
        Assert.True(File.Exists(Path.Combine(unmarked, "keep.txt")));
    }
    [Fact]
    public void CancelledDirectoryAdmissionDoesNotCreatePrivateData()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => SignInProfileDirectory.Create(_root, Guid.NewGuid(), stop.Token));
        Assert.False(Directory.Exists(_root));
    }
    [Fact]
    public Task ShellSavedSignInUsesReturnedRobloxIdentityWithoutOAuthSetup() => OnUi(async () =>
    {
        var signin = new SignIn(); var client = new Client();
        await using var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), signIn: signin, sessionClient: client);
        await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice);
        await shell.SaveAccountAsync(AccountProfile.Create("My test alias") with { Notes = "Keep this note" });
        shell.SelectedAccount = Assert.Single(shell.Accounts);
        Assert.Equal("", shell.OAuthClientId);
        shell.LinkAccount.Execute(null); await shell.LinkAccount.Execution;
        var linked = Assert.Single(shell.Accounts);
        Assert.Equal("verified_player", linked.Label); Assert.Equal("@verified_player", linked.Username);
        Assert.Equal(456, linked.KnownUserId); Assert.Equal("My test alias", linked.Alias);
        Assert.Equal(1, signin.Calls); Assert.Equal(1, client.Calls); Assert.Contains("Saved sign-in verified", shell.Notice);
        Assert.DoesNotContain("synthetic-private-session", shell.Notice); Assert.DoesNotContain("synthetic-private-session", linked.LinkedAccount);
        using var vault = new ProtectedCredentialVault(_root);
        var saved = (await vault.ReadAsync(linked.Profile.Id, default))!;
        Assert.Equal(RobloxSessionIdentity.Provider, saved.Provider); Assert.Equal("synthetic-private-session", saved.SessionCookie);
    });
    [Fact]
    public void ProductionSettingsNoLongerRequestsOAuthRegistration()
    {
        using var stream = typeof(RobloxSignInPresentationTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        using var reader = new StreamReader(stream); string xaml = reader.ReadToEnd();
        Assert.DoesNotContain("Binding OAuthClientId", xaml); Assert.DoesNotContain("Binding CallbackPort", xaml);
        Assert.Contains("Saved Roblox sign-ins", xaml); Assert.Contains("Check saved sign-in", xaml);
    }
    private sealed class SignIn : IRobloxSessionSignIn
    {
        public int Calls;
        public Task<RobloxSessionCredential> SignInAsync(Guid id, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(new RobloxSessionCredential("synthetic-private-session")); }
    }
    private sealed class Client : IRobloxSessionClient
    {
        public int Calls;
        public Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(new RobloxSessionValidation(new(456, "verified_player", RobloxSessionIdentity.Issuer, "456"))); }
    }
    private sealed class Monitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct) => Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow, 32*CapacityPolicy.GiB, 20*CapacityPolicy.GiB, 64*CapacityPolicy.GiB, 20*CapacityPolicy.GiB, 10), []));
        public Task<bool> FocusAsync(SessionObservation s, CancellationToken ct) => throw new InvalidOperationException("No native action expected.");
        public Task<bool> RequestCloseAsync(SessionObservation s, CancellationToken ct) => throw new InvalidOperationException("No native action expected.");
    }
    private static Task OnUi(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () => { try { await action(); done.TrySetResult(); } catch (Exception error) { done.TrySetException(error); } finally { dispatcher.InvokeShutdown(); } });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
