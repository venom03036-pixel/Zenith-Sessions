using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// M-L2-2: a request recovered after a restart that was saved before any validation, preparation or provider
// call (Blocked/Interrupted) never started a client. Cancel is its only action, so neither the queue row nor
// the account list may ask the user to review it. A recovered row that may have started (ReviewRequired/
// Interrupted) keeps its review wording because Review is available there.
public sealed class RecoveredLaunchWordingTests
{
    private static readonly DateTimeOffset At=new(2026,11,7,12,0,0,TimeSpan.Zero);
    private static LaunchOperation Recovered(AccountProfile profile,LaunchState state)=>
        new(new LaunchIntent(Guid.NewGuid(),profile.Id,profile.Revision,123,42,0,At.AddMinutes(-2),7),state,LaunchProblem.Interrupted,At);

    [Fact]
    public void ANeverStartedRecoveredRequestSaysItNeverStartedAndPointsToCancelNotReview()
    {
        var profile=AccountProfile.Create("Synthetic sample");var operation=Recovered(profile,LaunchState.Blocked);
        var row=new LaunchRow(operation);
        Assert.True(row.CanCancel);Assert.False(row.CanReview);
        Assert.Equal("Not started · interrupted",row.State);
        Assert.Equal("This request never started a client. Cancel it and queue again if it is still needed.",row.Detail);
        Assert.DoesNotContain("review",row.State+" "+row.Detail,StringComparison.OrdinalIgnoreCase);
        // The account list shows the same request with the same words, and its Details explanation matches the queue row.
        var account=new AccountRow(profile);account.SetLaunchStatus(operation);
        Assert.Equal(row.State,account.LaunchStatus);Assert.Equal(row.Detail,account.PowerExplanation);
    }

    [Fact]
    public void APossiblyStartedRecoveredRequestStillAsksForReviewAndCanBeReviewed()
    {
        var profile=AccountProfile.Create("Synthetic sample");var operation=Recovered(profile,LaunchState.ReviewRequired);
        var row=new LaunchRow(operation);
        Assert.True(row.CanReview);Assert.Equal("Review required",row.State);
        Assert.Equal("Recovered queue requires review. Nothing resumed automatically.",row.Detail);
        var account=new AccountRow(profile);account.SetLaunchStatus(operation);
        Assert.Equal("Review required",account.LaunchStatus);Assert.Equal(row.Detail,account.PowerExplanation);
    }
}
