using System.IO;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class AccountGroupManagerViewModelTests
{
    private static AccountGroupChangePreview Preview(AccountGroupChange change, int count = 3, bool destinationExists = false)
        => new(change, count, count > 0 ? 1 : 0, destinationExists, new string('A', 64));
    private static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task ApplyRequiresReviewAndPinsTheActualReviewedChange()
    {
        AccountGroupChangePreview? applied = null;
        await using var model = new AccountGroupManagerViewModel("Original", () => new[] { "Original" }, () => true,
            (change, _) => Task.FromResult(Preview(change)), (expected, _) => { applied = expected; return Task.FromResult(3); });
        model.DestinationName = "Renamed";
        Assert.False(model.CanApply); model.Apply.Execute(null); await model.Apply.Execution; Assert.Null(applied);
        model.Review.Execute(null); await model.Review.Execution;
        Assert.True(model.CanApply); Assert.Contains("3", model.Status);
        var expected = model.Preview; model.Apply.Execute(null); await model.Apply.Execution;
        Assert.Same(expected, applied); Assert.Equal("Original", applied!.Change.SourceName); Assert.Equal("Renamed", applied.Change.DestinationName);
        Assert.Null(model.Preview); Assert.False(model.CanApply); Assert.Contains("Updated 3", model.Status);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("destination")]
    [InlineData("kind")]
    [InlineData("merge")]
    public async Task EditingAnyReviewedInputRequiresANewReview(string field)
    {
        int writes = 0;
        await using var model = new AccountGroupManagerViewModel("Original", () => new[] { "Original" }, () => true,
            (change, _) => Task.FromResult(Preview(change)), (_, _) => { writes++; return Task.FromResult(3); });
        model.DestinationName = "Target"; model.Review.Execute(null); await model.Review.Execution; Assert.True(model.CanApply);
        switch (field)
        {
            case "source": model.SourceName = "Other"; break;
            case "destination": model.DestinationName = "Other"; break;
            case "kind": model.Choice = AccountGroupManagerViewModel.Choices[1]; break;
            case "merge": model.AllowMerge = true; break;
        }
        Assert.Null(model.Preview); Assert.False(model.CanApply);
        model.Apply.Execute(null); await model.Apply.Execution; Assert.Equal(0, writes);
    }

    [Fact]
    public async Task ExistingDestinationRequiresExplicitMergeAndAnotherReview()
    {
        var reviewed = new List<AccountGroupChange>();
        await using var model = new AccountGroupManagerViewModel("Original", () => new[] { "Original", "Target" }, () => true,
            (change, _) => { reviewed.Add(change); return Task.FromResult(Preview(change, destinationExists: true)); }, (_, _) => Task.FromResult(3));
        model.DestinationName = "Target"; model.Review.Execute(null); await model.Review.Execution;
        Assert.False(model.CanApply); Assert.Contains("Enable merging", model.Status);
        model.AllowMerge = true; Assert.Null(model.Preview); Assert.False(model.CanApply);
        model.Review.Execute(null); await model.Review.Execution;
        Assert.True(model.CanApply); Assert.False(reviewed[0].AllowMerge); Assert.True(reviewed[1].AllowMerge);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateReviewSuccessOrFailureCannotRestoreAnObsoleteDraft(bool fail)
    {
        var entered = Gate<bool>(); var release = Gate<AccountGroupChangePreview>(); CancellationToken observed = default;
        await using var model = new AccountGroupManagerViewModel("Original", () => new[] { "Original" }, () => true,
            (change, ct) => { observed = ct; entered.TrySetResult(true); return release.Task; }, (_, _) => Task.FromResult(3));
        model.DestinationName = "Old draft"; model.Review.Execute(null); await entered.Task;
        var old = new AccountGroupChange(AccountGroupChangeKind.Rename, "Original", "Old draft");
        model.DestinationName = "New draft"; Assert.True(observed.IsCancellationRequested);
        if (fail) release.TrySetException(new IOException("Synthetic obsolete failure")); else release.TrySetResult(Preview(old));
        await model.Review.Execution;
        Assert.Null(model.Preview); Assert.False(model.CanApply); Assert.Contains("Review the current", model.Status); Assert.DoesNotContain("failure", model.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PanicOrLockFencePreservesStopFeedbackAndJoinsAnOutstandingApply(bool fail)
    {
        var entered = Gate<bool>(); var release = Gate<int>(); CancellationToken observed = default;
        var model = new AccountGroupManagerViewModel("Original", () => new[] { "Original" }, () => true,
            (change, _) => Task.FromResult(Preview(change)), (_, ct) => { observed = ct; entered.TrySetResult(true); return release.Task; });
        model.DestinationName = "Target"; model.Review.Execute(null); await model.Review.Execution;
        model.Apply.Execute(null); await entered.Task; model.Fence("Stopped by test Panic");
        var closing = model.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted); Assert.True(observed.IsCancellationRequested);
        if (fail) release.TrySetException(new IOException("Synthetic late failure")); else release.TrySetResult(3);
        await closing;
        Assert.False(model.CanApply); Assert.False(model.CanReview); Assert.Null(model.Preview);
        Assert.DoesNotContain("Updated", model.Status); Assert.DoesNotContain("failure", model.Status);
    }

    [Fact]
    public async Task ARefreshReviewClearsTheOldPreviewEvenWhenTheNewReadIsCancelled()
    {
        int reads = 0;
        await using var model = new AccountGroupManagerViewModel("Original", () => new[] { "Original" }, () => true,
            (change, _) => ++reads == 1 ? Task.FromResult(Preview(change)) : Task.FromCanceled<AccountGroupChangePreview>(new CancellationToken(true)),
            (_, _) => Task.FromResult(3));
        model.DestinationName = "Target"; model.Review.Execute(null); await model.Review.Execution; Assert.True(model.CanApply);
        model.Review.Execute(null); await model.Review.Execution;
        Assert.Null(model.Preview); Assert.False(model.CanApply); Assert.Contains("Review stopped", model.Status);
    }

    [Fact]
    public async Task EmptyGroupOrUnavailableOwnerNeverOffersApply()
    {
        bool available = true; int writes = 0;
        await using var model = new AccountGroupManagerViewModel("Missing", () => Array.Empty<string>(), () => available,
            (change, _) => Task.FromResult(Preview(change, 0)), (_, _) => { writes++; return Task.FromResult(0); });
        model.DestinationName = "Target"; model.Review.Execute(null); await model.Review.Execution;
        Assert.False(model.CanApply); Assert.Contains("No accounts", model.Status);
        available = false; Assert.False(model.CanReview); model.Apply.Execute(null); await model.Apply.Execution; Assert.Equal(0, writes);
    }

    [Fact]
    public async Task RemovalRequiresAnExplicitDifferentFallbackAndDoesNotRequestMerge()
    {
        AccountGroupChange? requested = null;
        await using var model = new AccountGroupManagerViewModel("General", () => new[] { "General" }, () => true,
            (change, _) => { requested = change; return Task.FromResult(Preview(change)); }, (_, _) => Task.FromResult(3));
        model.Choice = AccountGroupManagerViewModel.Choices[1]; model.AllowMerge = true;
        model.DestinationName = "general"; model.Review.Execute(null); await model.Review.Execution;
        Assert.Null(requested); Assert.False(model.CanApply); Assert.Contains("different fallback", model.Status);
        model.DestinationName = "Unassigned"; model.Review.Execute(null); await model.Review.Execution;
        Assert.Equal(AccountGroupChangeKind.Remove, requested!.Kind); Assert.False(requested.AllowMerge); Assert.True(model.CanApply);
        Assert.Contains("Accounts and saved sign-ins stay", model.Status);
    }
}
