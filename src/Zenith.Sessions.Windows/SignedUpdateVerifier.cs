using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zenith.Sessions.Windows;

public sealed record UpdateSigningKey(string Id, string PublicKeySpki, string PublisherCertificateSha256);
public sealed record UpdateManifest(int FormatVersion, string Product, string Version, bool ReleaseReady,
    DateTimeOffset ExpiresUtc, string PackageUri, long PackageBytes, string PackageSha256);
public sealed record SignedUpdateEnvelope(string KeyId, string ManifestBase64, string SignatureBase64);

/// <summary>Only a verifier can issue this capability. Untrusted JSON never directly authorizes installation.</summary>
public sealed class VerifiedUpdate
{
    internal VerifiedUpdate(UpdateManifest manifest, UpdateSigningKey key, byte[] envelope)
    { Manifest = manifest; Key = key; Envelope = envelope; }
    public UpdateManifest Manifest { get; }
    internal UpdateSigningKey Key { get; }
    internal byte[] Envelope { get; }
}

public sealed class SignedUpdateVerifier
{
    public const int MaximumEnvelopeBytes = 65536;
    public const long MaximumPackageBytes = 512L * 1024 * 1024;
    public const string Product = "Zenith.Sessions.Personal";
    public const string Domain = "Zenith.Sessions.UpdateManifest.v1\n";
    private readonly Dictionary<string, UpdateSigningKey> _keys;
    private readonly TimeProvider _time;
    public SignedUpdateVerifier(IEnumerable<UpdateSigningKey> keys, TimeProvider? time = null)
    {
        _keys = keys.ToDictionary(key => key.Id, StringComparer.Ordinal);
        _time = time ?? TimeProvider.System;
    }
    public VerifiedUpdate Verify(ReadOnlySpan<byte> bytes, Version currentVersion)
    {
        if (bytes.Length is 0 or > MaximumEnvelopeBytes) throw new InvalidDataException("Update envelope exceeds its bound.");
        var envelope = JsonSerializer.Deserialize<SignedUpdateEnvelope>(bytes) ?? throw new InvalidDataException("Missing envelope.");
        if (envelope.KeyId is null || !_keys.TryGetValue(envelope.KeyId, out var key))
            throw new CryptographicException("This release signing key is not trusted by this installation.");
        if (!IsHash(key.PublisherCertificateSha256)) throw new CryptographicException("No release publisher is configured.");
        byte[] manifestBytes = Convert.FromBase64String(envelope.ManifestBase64);
        byte[] signature = Convert.FromBase64String(envelope.SignatureBase64);
        if (manifestBytes.Length is 0 or > 32768 || signature.Length != 64) throw new InvalidDataException("Invalid signed manifest size.");
        using var ecdsa = ECDsa.Create();
        byte[] publicKey = Convert.FromBase64String(key.PublicKeySpki);
        ecdsa.ImportSubjectPublicKeyInfo(publicKey, out int read);
        if (read != publicKey.Length || ecdsa.KeySize != 256 || ecdsa.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7"
            || !ecdsa.VerifyData(SigningBytes(manifestBytes), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("Update signature verification failed.");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes) ?? throw new InvalidDataException("Missing manifest.");
        if (manifest.FormatVersion != 1 || manifest.Product != Product || !manifest.ReleaseReady
            || !Version.TryParse(manifest.Version, out var version) || version <= currentVersion
            || manifest.ExpiresUtc <= _time.GetUtcNow() || manifest.ExpiresUtc > _time.GetUtcNow().AddDays(90)
            || manifest.PackageBytes is <= 0 or > MaximumPackageBytes || !IsHash(manifest.PackageSha256)
            || !Uri.TryCreate(manifest.PackageUri, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFile))
            throw new InvalidDataException("The signed update is incompatible, expired, a preview, or not newer.");
        return new(manifest, key, bytes.ToArray());
    }
    public static byte[] SigningBytes(ReadOnlySpan<byte> manifest)
    {
        byte[] prefix = Encoding.UTF8.GetBytes(Domain), result = new byte[prefix.Length + manifest.Length];
        prefix.CopyTo(result, 0); manifest.CopyTo(result.AsSpan(prefix.Length)); return result;
    }
    internal static bool IsHash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
}
