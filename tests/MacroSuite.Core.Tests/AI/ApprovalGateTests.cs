using FluentAssertions;
using MacroSuite.Core.AI;
using MacroSuite.SDK.AI;
using Xunit;

namespace MacroSuite.Core.Tests.AI;

public class ApprovalGateTests
{
    [Fact]
    public async Task RequestApprovalAsync_AutoApprove_ReturnsApproved()
    {
        var gate = new ApprovalGate(ApprovalPolicy.AutoApprove);
        var action = new AIAction { ActionType = "click", X = 100, Y = 100 };
        var context = new ApprovalContext { ActionSummary = "Click (100,100)" };

        var decision = await gate.RequestApprovalAsync(action, context);

        decision.Should().Be(ApprovalDecision.Approved);
    }

    [Fact]
    public async Task RequestApprovalAsync_AutoReject_ReturnsRejected()
    {
        var gate = new ApprovalGate(ApprovalPolicy.AutoReject);
        var action = new AIAction { ActionType = "click", X = 100, Y = 100 };
        var context = new ApprovalContext { ActionSummary = "Click (100,100)" };

        var decision = await gate.RequestApprovalAsync(action, context);

        decision.Should().Be(ApprovalDecision.Rejected);
    }

    [Fact]
    public async Task RequestApprovalAsync_ManualApproval_CallsCustomHandler()
    {
        bool handlerCalled = false;
        var gate = new ApprovalGate(ApprovalPolicy.ManualApproval, (act, ctx, ct) =>
        {
            handlerCalled = true;
            return Task.FromResult(ApprovalDecision.Approved);
        });

        var action = new AIAction { ActionType = "click", X = 50, Y = 50 };
        var context = new ApprovalContext();

        var decision = await gate.RequestApprovalAsync(action, context);

        handlerCalled.Should().BeTrue();
        decision.Should().Be(ApprovalDecision.Approved);
    }

    [Fact]
    public async Task RequestApprovalAsync_ManualApproval_NoHandler_FailsSafeToReject()
    {
        var gate = new ApprovalGate(ApprovalPolicy.ManualApproval, customHandler: null);
        var action = new AIAction { ActionType = "click", X = 50, Y = 50 };
        var context = new ApprovalContext();

        var decision = await gate.RequestApprovalAsync(action, context);

        decision.Should().Be(ApprovalDecision.Rejected);
    }

    [Fact]
    public async Task RequestApprovalAsync_ManualApproval_Timeout_ReturnsTimedOut()
    {
        var gate = new ApprovalGate(ApprovalPolicy.ManualApproval, async (act, ctx, ct) =>
        {
            // Simulate hanging waiting for user interaction
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return ApprovalDecision.Approved;
        });

        var action = new AIAction { ActionType = "click", X = 50, Y = 50 };
        var context = new ApprovalContext { Timeout = TimeSpan.FromMilliseconds(50) };

        var decision = await gate.RequestApprovalAsync(action, context);

        decision.Should().Be(ApprovalDecision.TimedOut);
    }

    [Fact]
    public async Task RequestApprovalAsync_CancelledToken_ReturnsRejected()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var gate = new ApprovalGate(ApprovalPolicy.AutoApprove);
        var action = new AIAction { ActionType = "click" };
        var context = new ApprovalContext();

        var decision = await gate.RequestApprovalAsync(action, context, cts.Token);

        decision.Should().Be(ApprovalDecision.Rejected);
    }
}
