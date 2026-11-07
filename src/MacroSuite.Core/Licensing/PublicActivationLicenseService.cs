namespace MacroSuite.Core.Licensing;

/// <summary>Adapts verified public activation into the existing local feature gates.</summary>
public sealed class PublicActivationLicenseService : ILicenseService, IDisposable
{
    private readonly IActivationService _activation;
    private LicenseInfo? _previous;
    public PublicActivationLicenseService(IActivationService activation)
    { _activation = activation ?? throw new ArgumentNullException(nameof(activation)); _activation.StateChanged += OnChanged; }
    public bool IsFeatureLicensed(FeatureId feature) => _activation.IsActive && ActivationProduct.IncludedFeatures.Contains(feature);
    public LicenseInfo GetCurrentLicense()
    {
        var current = _activation.CurrentLicense;
        return new LicenseInfo
        {
            LicenseKey = current?.LicenseId ?? string.Empty, CustomerId = current?.CustomerId ?? "Activation required",
            Type = LicenseType.Standard, IssuedUtc = current?.IssuedUtc ?? DateTimeOffset.UnixEpoch,
            ExpirationUtc = current?.ExpiresUtc ?? DateTimeOffset.MaxValue,
            LicensedFeatures = _activation.IsActive ? ActivationProduct.IncludedFeatures.ToHashSet() : new HashSet<FeatureId>()
        };
    }
    public async Task<LicenseValidationResult> ValidateAsync(CancellationToken ct = default)
    {
        var result = await _activation.RefreshAsync(ct).ConfigureAwait(false);
        return result.Success ? LicenseValidationResult.Success(GetCurrentLicense()) : LicenseValidationResult.Failure(result.Message);
    }
    public event EventHandler<LicenseChangedEventArgs>? LicenseChanged;
    private void OnChanged(object? sender, EventArgs args)
    {
        LicenseInfo next = GetCurrentLicense(); LicenseInfo? previous = _previous; _previous = next;
        LicenseChanged?.Invoke(this, new LicenseChangedEventArgs(previous, next));
    }
    public void Dispose() => _activation.StateChanged -= OnChanged;
}
