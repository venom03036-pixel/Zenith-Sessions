namespace MacroSuite.Core.Licensing;

/// <summary>
/// Standard implementation of <see cref="IFeatureGate"/> querying an underlying <see cref="ILicenseService"/>.
/// </summary>
public sealed class FeatureGate : IFeatureGate
{
    private readonly ILicenseService _licenseService;

    public FeatureGate(ILicenseService licenseService)
    {
        _licenseService = licenseService ?? throw new ArgumentNullException(nameof(licenseService));
    }

    /// <inheritdoc/>
    public bool IsAvailable(FeatureId feature)
    {
        return _licenseService.IsFeatureLicensed(feature);
    }

    /// <inheritdoc/>
    public void Require(FeatureId feature)
    {
        if (!IsAvailable(feature))
        {
            throw new FeatureNotLicensedException(feature);
        }
    }
}
