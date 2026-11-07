using System.Text.Json.Serialization;
using MacroSuite.Core.Execution;

namespace MacroSuite.Core.Rules;

/// <summary>
/// A boolean condition evaluated during macro execution or wait steps.
/// Composable into boolean trees via AND, OR, NOT.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ColorCondition), typeDiscriminator: "color")]
[JsonDerivedType(typeof(ImageCondition), typeDiscriminator: "image")]
[JsonDerivedType(typeof(AndCondition), typeDiscriminator: "and")]
[JsonDerivedType(typeof(OrCondition), typeDiscriminator: "or")]
[JsonDerivedType(typeof(NotCondition), typeDiscriminator: "not")]
[JsonDerivedType(typeof(VisualPresenceCondition), typeDiscriminator: "visualpresence")]
public interface ICondition
{
    /// <summary>Human-readable label or description of the condition.</summary>
    string Description { get; }

    /// <summary>
    /// Canonical evaluation. Failures are <see cref="ConditionOutcome.Failed"/>; cancellation throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<ConditionEvaluation> EvaluateDetailedAsync(MacroContext context, CancellationToken ct = default);

    /// <summary>
    /// Boolean wrapper over <see cref="EvaluateDetailedAsync"/>. Throws on <see cref="ConditionOutcome.Failed"/>
    /// so leftover callers cannot treat matcher/capture failure as false.
    /// </summary>
    async Task<bool> EvaluateAsync(MacroContext context, CancellationToken ct = default)
    {
        ConditionEvaluation result = await EvaluateDetailedAsync(context, ct).ConfigureAwait(false);
        result.ThrowIfFailed();
        return result.IsSatisfied;
    }
}

/// <summary>
/// Concrete-type helper so <c>color.EvaluateAsync</c> resolves; default interface
/// methods are only visible through <see cref="ICondition"/>.
/// </summary>
public static class ConditionEvaluateExtensions
{
    public static Task<bool> EvaluateAsync(this ICondition condition, MacroContext context, CancellationToken ct = default)
        => ((ICondition)condition).EvaluateAsync(context, ct);
}

