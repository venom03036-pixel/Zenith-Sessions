using System.Drawing;
using MacroSuite.SDK.AI;

namespace MacroSuite.Core.AI;

/// <summary>
/// Operational safety policy governing human-in-the-loop approval for AI vision actions (Rule 8).
/// </summary>
public enum ApprovalPolicy
{
    /// <summary>Displays an approval toast near the target coordinate; times out to Reject.</summary>
    ManualApproval,

    /// <summary>Immediately approves proposed actions. Strictly requires autonomous mode to be explicitly activated.</summary>
    AutoApprove,

    /// <summary>Immediately rejects all AI-generated actions. Safe default for evaluation or headless testing.</summary>
    AutoReject,
}

/// <summary>
/// Outcome of human or automated approval gate evaluation.
/// </summary>
public enum ApprovalDecision
{
    Approved,
    Rejected,
    TimedOut,
}

/// <summary>
/// Contextual metadata provided to the approval gate when an AI action is proposed.
/// </summary>
public sealed class ApprovalContext
{
    /// <summary>Target screen region or coordinate where action will execute.</summary>
    public Rectangle? TargetRegion { get; init; }

    /// <summary>Human-readable explanation of the proposed action.</summary>
    public string ActionSummary { get; init; } = string.Empty;

    /// <summary>Model's reasoning for this step.</summary>
    public string Reasoning { get; init; } = string.Empty;

    /// <summary>JPEG-encoded thumbnail bytes of the captured screen region for visual preview.</summary>
    public byte[]? ScreenshotThumbnail { get; init; }

    /// <summary>Maximum time window to wait for user confirmation before auto-rejecting.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Gate interface evaluating whether an AI-proposed action is authorized to execute.
/// </summary>
public interface IApprovalGate
{
    /// <summary>The active approval policy.</summary>
    ApprovalPolicy Policy { get; }

    /// <summary>
    /// Submits a proposed AI action for safety approval.
    /// Timeout or cancellation must NEVER hang — always resolves to Rejected or TimedOut.
    /// When <paramref name="forceInteractive"/> is true, AutoApprove is ignored and the manual handler is used.
    /// </summary>
    Task<ApprovalDecision> RequestApprovalAsync(
        AIAction action,
        ApprovalContext context,
        CancellationToken ct = default,
        bool forceInteractive = false);
}

/// <summary>
/// Configurable implementation of <see cref="IApprovalGate"/> supporting manual delegates, auto-approve, and auto-reject.
/// </summary>
public sealed class ApprovalGate : IApprovalGate
{
    private readonly Func<AIAction, ApprovalContext, CancellationToken, Task<ApprovalDecision>>? _customHandler;

    public ApprovalPolicy Policy { get; set; }

    public ApprovalGate(
        ApprovalPolicy policy = ApprovalPolicy.ManualApproval,
        Func<AIAction, ApprovalContext, CancellationToken, Task<ApprovalDecision>>? customHandler = null)
    {
        Policy = policy;
        _customHandler = customHandler;
    }

    /// <inheritdoc/>
    public async Task<ApprovalDecision> RequestApprovalAsync(
        AIAction action,
        ApprovalContext context,
        CancellationToken ct = default,
        bool forceInteractive = false)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);

        if (ct.IsCancellationRequested)
        {
            return ApprovalDecision.Rejected;
        }

        ApprovalPolicy policy = forceInteractive && Policy == ApprovalPolicy.AutoApprove
            ? ApprovalPolicy.ManualApproval
            : Policy;

        switch (policy)
        {
            case ApprovalPolicy.AutoReject:
                return ApprovalDecision.Rejected;

            case ApprovalPolicy.AutoApprove:
                return ApprovalDecision.Approved;

            case ApprovalPolicy.ManualApproval:
                if (_customHandler != null)
                {
                    using var timeoutCts = new CancellationTokenSource(context.Timeout);
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                    try
                    {
                        return await _customHandler(action, context, linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return timeoutCts.IsCancellationRequested
                            ? ApprovalDecision.TimedOut
                            : ApprovalDecision.Rejected;
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException("The approval UI failed before a decision was recorded.", ex);
                    }
                }

                // If no UI handler is attached, fail-safe to reject
                return ApprovalDecision.Rejected;

            default:
                return ApprovalDecision.Rejected;
        }
    }
}
