namespace MacroSuite.Core.Abstractions;

public enum PhysicalInputNeutralState { Unknown, Held, Neutral }

/// <summary>Actual physical user-input state; never inferred from synthesized held-input bookkeeping.</summary>
public interface IPhysicalInputReadiness
{
    PhysicalInputNeutralState GetNeutralState();
}
