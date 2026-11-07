namespace MacroSuite.Core.Steps;

/// <summary>Explicit continuation overrides for an unsuccessful conditional block.</summary>
public enum FlowRoute
{
    Default = 0,
    Continue = 1,
    Retry = 2,
    Jump = 3,
    Stop = 4
}

/// <summary>Additive routing settings. Existing explicit condition policies remain authoritative when Default is selected.</summary>
public sealed record BlockRoutingOptions
{
    public FlowRoute OnNotSatisfied { get; init; }
    public FlowRoute OnFailure { get; init; }
    public Guid? TargetStepId { get; init; }
    public int RetryCount { get; init; } = 3;
    public int RetryDelayMs { get; init; } = 100;
}
