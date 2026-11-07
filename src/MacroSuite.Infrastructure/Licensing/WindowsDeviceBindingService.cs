using System.Security.Cryptography;
using MacroSuite.Core.Licensing;

namespace MacroSuite.Infrastructure.Licensing;

/// <summary>Per-Windows-user non-exportable CNG keys. TPM first; software requires explicit request consent.</summary>
public sealed class WindowsDeviceBindingService : IDeviceBindingService
{
    private readonly string _keyPrefix;
    public WindowsDeviceBindingService(string keyPrefix = "ZenithMacros.Public.Activation.v2") => _keyPrefix = keyPrefix;

    public Task<DeviceBindingIdentity> GetOrCreateAsync(bool allowSoftwareFallback, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try { return GetOrCreate(ActivationProduct.TpmProtection); }
            catch (CryptographicException) when (allowSoftwareFallback) { return GetOrCreate(ActivationProduct.SoftwareProtection); }
        }, ct);

    private DeviceBindingIdentity GetOrCreate(string protection)
    {
        CngProvider provider = Provider(protection); string name = Name(protection);
        using CngKey key = CngKey.Exists(name, provider)
            ? CngKey.Open(name, provider)
            : CngKey.Create(CngAlgorithm.Rsa, name, new CngKeyCreationParameters
            {
                Provider = provider, ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.Signing,
                Parameters = { new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None) }
            });
        using var rsa = new RSACng(key); byte[] publicKey = rsa.ExportSubjectPublicKeyInfo();
        if (rsa.KeySize != 2048) throw new CryptographicException("The activation device key has an unsupported size.");
        return new(ActivationWireFormat.KeyId(publicKey), Convert.ToBase64String(publicKey), protection);
    }

    public Task<byte[]> SignAsync(string protection, byte[] challenge, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (challenge.Length > ActivationProduct.MaximumFileBytes) throw new CryptographicException("Activation challenge is too large.");
            using var key = CngKey.Open(Name(protection), Provider(protection)); using var rsa = new RSACng(key);
            return rsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }, ct);

    public Task<bool> ProvePossessionAsync(VerifiedLicense license, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Opening an established activation never creates or replaces a key.
                using var key = CngKey.Open(Name(license.DeviceProtection), Provider(license.DeviceProtection)); using var rsa = new RSACng(key);
                byte[] publicKey = rsa.ExportSubjectPublicKeyInfo();
                if (rsa.KeySize != 2048 || ActivationWireFormat.KeyId(publicKey) != license.DeviceKeyId) return false;
                byte[] challenge = RandomNumberGenerator.GetBytes(32);
                byte[] signature = rsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var verifier = RSA.Create(); verifier.ImportSubjectPublicKeyInfo(publicKey, out _);
                return verifier.VerifyData(challenge, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch (CryptographicException) { return false; }
        }, ct);

    private string Name(string protection) => _keyPrefix + "." + protection;
    private static CngProvider Provider(string protection) => protection switch
    {
        ActivationProduct.TpmProtection => CngProvider.MicrosoftPlatformCryptoProvider,
        ActivationProduct.SoftwareProtection => CngProvider.MicrosoftSoftwareKeyStorageProvider,
        _ => throw new CryptographicException("Unsupported device protection.")
    };
}
