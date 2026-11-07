using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Storage;

namespace MacroSuite.Infrastructure.Licensing;

/// <summary>Owns serialized activation transitions; signature and possession checks happen outside input hot paths.</summary>
public sealed class OfflineActivationService : IActivationService, IDisposable
{
    private readonly ActivationLicenseVerifier _verifier;
    private readonly ActivationLicenseStore _store;
    private readonly IDeviceBindingService _device;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VerifiedLicense? _current;
    private string _status = "Activation required. Export an activation request, then import the license supplied by the owner.";
    public bool IsActive => CurrentLicense is { } license && !license.IsExpired(DateTimeOffset.UtcNow);
    public string StatusMessage => Volatile.Read(ref _status);
    public VerifiedLicense? CurrentLicense => Volatile.Read(ref _current);
    public event EventHandler? StateChanged;
    public OfflineActivationService(ActivationTrustManifest trust, string? licenseDirectory = null, IDeviceBindingService? deviceBinding = null)
    {
        _verifier = new(trust); _store = new(licenseDirectory ?? AppDataPathResolver.Default.LicenseDirectory);
        _device = deviceBinding ?? new WindowsDeviceBindingService();
    }
    public async Task<ActivationOperationResult> RefreshAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        ActivationOperationResult result;
        try
        {
            string? text = await _store.ReadAsync(ct).ConfigureAwait(false);
            if (text is null) result = Set(null, "Activation required. Export an activation request to get started.");
            else if (!_verifier.TryVerify(text, out var verified, out string? error)) result = Set(null, error ?? "Saved activation is invalid.");
            else if (!await _device.ProvePossessionAsync(verified!, ct).ConfigureAwait(false)) result = Set(null, "Activation belongs to another device or its protected device key is unavailable. Request a replacement activation.");
            else result = Set(verified, "Activated · Standard user");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or FormatException or DecoderFallbackException)
        { result = Set(null, "Saved activation could not be read or verified. Restore it or request a replacement activation."); }
        finally { _gate.Release(); }
        StateChanged?.Invoke(this, EventArgs.Empty); return result;
    }
    public async Task<ActivationRequestResult> CreateRequestAsync(bool allowSoftwareFallback = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            DeviceBindingIdentity identity = await _device.GetOrCreateAsync(allowSoftwareFallback, ct).ConfigureAwait(false);
            var payload = new ActivationRequestPayload { DeviceKeyId = identity.KeyId, DevicePublicKeyBase64 = identity.PublicKeyBase64, DeviceProtection = identity.Protection };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, ActivationWireFormat.JsonOptions);
            byte[] proof = await _device.SignAsync(identity.Protection, bytes, ct).ConfigureAwait(false);
            string text = JsonSerializer.Serialize(new { formatVersion = 2, payload = Convert.ToBase64String(bytes), proof = Convert.ToBase64String(proof) }, ActivationWireFormat.JsonOptions);
            if (!ActivationRequestCodec.TryVerify(text, out _, out string? error)) return new(false, error ?? "Device request verification failed.");
            return new(true, identity.Protection == ActivationProduct.TpmProtection ? "Device activation request is ready." : "Software-protected request is ready. The owner must explicitly approve its weaker protection.", text, identity.Protection);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        { return new(false, allowSoftwareFallback ? "The device key could not be created. Check Windows key storage and try again." : "A TPM-protected key is unavailable. You may explicitly request weaker software protection, subject to owner approval."); }
        finally { _gate.Release(); }
    }
    public async Task<ActivationOperationResult> ImportAsync(string licenseText, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false); bool changed = false; ActivationOperationResult result;
        try
        {
            if (!_verifier.TryVerify(licenseText, out var verified, out string? error)) return new(false, error ?? "License is invalid.");
            if (!await _device.ProvePossessionAsync(verified!, ct).ConfigureAwait(false)) return new(false, "This license belongs to another device or its device key is unavailable.");
            await _store.SaveAsync(ActivationWireFormat.UnwrapLicenseCode(licenseText), ct).ConfigureAwait(false);
            result = Set(verified, "Activated · Standard user"); changed = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or FormatException)
        { result = new(false, "Activation could not be saved. Your previous activation was preserved; check folder access and retry."); }
        finally { _gate.Release(); }
        if (changed) StateChanged?.Invoke(this, EventArgs.Empty); return result;
    }
    private ActivationOperationResult Set(VerifiedLicense? license, string message)
    { Volatile.Write(ref _current, license); Volatile.Write(ref _status, message); return new(license is not null, message); }
    public void RequireActive()
    { if (!IsActive) throw new ActivationRequiredException(StatusMessage); }
    // The composition owner drains active calls before disposal (Rule 12).
    public void Dispose() => _gate.Dispose();
}
