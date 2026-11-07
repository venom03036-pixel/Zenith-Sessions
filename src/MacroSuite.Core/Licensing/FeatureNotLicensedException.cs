namespace MacroSuite.Core.Licensing;

/// <summary>
/// Thrown when an execution boundary requires a feature not permitted under the active license (Rule 18).
/// </summary>
public sealed class FeatureNotLicensedException : InvalidOperationException
{
    /// <summary>The feature that was required but not licensed.</summary>
    public FeatureId Feature { get; }

    public FeatureNotLicensedException(FeatureId feature)
        : base($"Feature '{feature}' is not licensed under the active license tier. Upgrade your license to enable this capability.")
    {
        Feature = feature;
    }

    public FeatureNotLicensedException(FeatureId feature, string message)
        : base(message)
    {
        Feature = feature;
    }

    public FeatureNotLicensedException(FeatureId feature, string message, Exception innerException)
        : base(message, innerException)
    {
        Feature = feature;
    }
}
