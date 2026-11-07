namespace MacroSuite.Core.Steps;

/// <summary>A positive root Wait Image can watch the whole run and redirect once per appearance.</summary>
public sealed record AppearanceRedirectOptions
{
    public Guid TargetStepId { get; init; }
    public int CooldownMs { get; init; } = 500;
}
