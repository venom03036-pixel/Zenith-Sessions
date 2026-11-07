using MacroSuite.App.Overlays;
using MacroSuite.Core.Input;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    [Fact]
    public void EditorOwnership_PrivacyNestedScopesRestoreOnlyAfterTheLastScopeEnds()
    {
        var lifecycle = new ScreenCapturePrivacy.PrivacyLifecycle();
        int setups = 0, restores = 0;
        var first = lifecycle.Enter(() => { setups++; lifecycle.Remember(() => restores++); });
        var second = lifecycle.Enter(() => throw new InvalidOperationException("Nested setup must not run."));
        first.Dispose();
        Assert.Equal(1, setups); Assert.Equal(0, restores); Assert.Equal(1, lifecycle.Depth);
        second.Dispose(); first.Dispose(); second.Dispose();
        Assert.Equal(1, restores); Assert.Equal(0, lifecycle.Depth); Assert.Equal(0, lifecycle.PendingCount);
    }

    [Fact]
    public void EditorOwnership_PrivacySetupFailureRollsBackAndAllowsANewScope()
    {
        var lifecycle = new ScreenCapturePrivacy.PrivacyLifecycle();
        int restores = 0;
        Assert.Throws<InvalidOperationException>(() => lifecycle.Enter(() =>
        {
            lifecycle.Remember(() => restores++);
            throw new InvalidOperationException("Synthetic setup failure");
        }));
        Assert.Equal(1, restores); Assert.Equal(0, lifecycle.Depth); Assert.Equal(0, lifecycle.PendingCount);
        using (lifecycle.Enter(() => lifecycle.Remember(() => restores++))) { }
        Assert.Equal(2, restores);
    }

    [Fact]
    public void EditorOwnership_PrivacyRestoreAttemptsEveryWindowAndRetainsOnlyFailedEntries()
    {
        var lifecycle = new ScreenCapturePrivacy.PrivacyLifecycle();
        var attempts = new List<string>(); bool fail = true;
        var scope = lifecycle.Enter(() =>
        {
            lifecycle.Remember(() => attempts.Add("first"));
            lifecycle.Remember(() => { attempts.Add("failed"); if (fail) throw new InvalidOperationException("Synthetic restore failure"); });
            lifecycle.Remember(() => attempts.Add("last"));
        });
        Assert.Throws<HostInputOperationCleanupException>(scope.Dispose);
        Assert.Equal(new[] { "last", "failed", "first" }, attempts);
        Assert.Equal(1, lifecycle.PendingCount); Assert.Equal(0, lifecycle.Depth);
        bool newSetup = false;
        Assert.Throws<HostInputOperationCleanupException>(() => lifecycle.Enter(() => newSetup = true));
        Assert.False(newSetup);
        fail = false;
        scope.Dispose(); // An explicit retry uses only the captured unresolved restore.
        Assert.Equal(new[] { "last", "failed", "first", "failed" }, attempts);
        Assert.Equal(0, lifecycle.PendingCount);
        using (lifecycle.Enter(() => newSetup = true)) { }
        Assert.True(newSetup);
    }

    [Fact]
    public void EditorOwnership_PrivacyFailedSetupRollbackDoesNotForgetUnresolvedState()
    {
        var lifecycle = new ScreenCapturePrivacy.PrivacyLifecycle();
        int attempts = 0;
        Assert.Throws<HostInputOperationCleanupException>(() => lifecycle.Enter(() =>
        {
            lifecycle.Remember(() => { attempts++; throw new InvalidOperationException("Synthetic restore failure"); });
            throw new InvalidOperationException("Synthetic setup failure");
        }));
        Assert.Equal(1, attempts); Assert.Equal(0, lifecycle.Depth); Assert.Equal(1, lifecycle.PendingCount);
        Assert.Throws<HostInputOperationCleanupException>(() => lifecycle.Enter(() => throw new InvalidOperationException("Must stay blocked")));
        Assert.Equal(1, attempts); Assert.Equal(1, lifecycle.PendingCount);
    }

    [Fact]
    public void EditorOwnership_PrivacyRetainedRestoreStateHasAFixedBound()
    {
        var lifecycle = new ScreenCapturePrivacy.PrivacyLifecycle();
        int attempted = 0;
        Assert.Throws<HostInputOperationCleanupException>(() => lifecycle.Enter(() =>
        {
            for (int i = 0; i <= ScreenCapturePrivacy.PrivacyLifecycle.MaximumRememberedWindows; i++)
                lifecycle.Remember(() => { attempted++; throw new InvalidOperationException("Synthetic restore failure"); });
        }));
        Assert.Equal(ScreenCapturePrivacy.PrivacyLifecycle.MaximumRememberedWindows, attempted);
        Assert.Equal(ScreenCapturePrivacy.PrivacyLifecycle.MaximumRememberedWindows, lifecycle.PendingCount);
        Assert.Equal(0, lifecycle.Depth);
    }

    [Fact]
    public Task EditorOwnership_PrivacyRejectsAnotherDispatcherEvenDuringANestedScope()
        => WpfTestApplication.RunAsync(async () =>
        {
            // No windows are shown. Keeping one real scope active makes the old
            // increment-before-check path return a nested token on the wrong thread.
            using var current = ScreenCapturePrivacy.HideEngine(TimeSpan.Zero);
            await Task.Run(() => Assert.Throws<InvalidOperationException>(
                () => ScreenCapturePrivacy.HideEngine(TimeSpan.Zero)));
        });
}
