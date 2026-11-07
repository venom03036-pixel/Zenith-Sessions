namespace MacroSuite.Core.Licensing;

/// <summary>
/// Licensing enforcement boundary (Rule 18).
/// Controls whether macro steps, tools, and background services may run.
/// </summary>
public interface IFeatureGate
{
    /// <summary>
    /// Returns true if the feature is currently licensed.
    /// Does NOT throw — safe for UI state binding and visibility checks.
    /// </summary>
    bool IsAvailable(FeatureId feature);

    /// <summary>
    /// Throws <see cref="FeatureNotLicensedException"/> if the feature is not licensed.
    /// Called at every execution boundary requiring an active license.
    /// </summary>
    void Require(FeatureId feature);
}
