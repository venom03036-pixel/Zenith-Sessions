namespace MacroSuite.Core.Licensing;

/// <summary>
/// UI-facing license key policy. Unsigned prefixes and random strings never unlock features.
/// </summary>
public readonly record struct LicenseInstallDecision(bool Accepted, string Message, LicenseInfo? License);

public static class LicenseInstallPolicy
{
    public const string VerificationUnavailableMessage =
        "License verification is unavailable in this build.";

    public const string UnverifiedKeyMessage =
        "Unverified license keys cannot grant features. Use the developer master key on a developer build.";

    public static bool IsDeveloperAdminKey(string key)
        => key.StartsWith("ADMIN", StringComparison.OrdinalIgnoreCase)
           || key.Contains("ADMIN", StringComparison.OrdinalIgnoreCase)
           || key.Contains("MASTER", StringComparison.OrdinalIgnoreCase);

    public static LicenseInstallDecision FromUserKey(string? key, string email, bool isDeveloperBuild)
    {
        if (string.IsNullOrWhiteSpace(key))
            return new LicenseInstallDecision(false, "A license key is required.", null);

        if (!isDeveloperBuild)
        {
            if (TryInstallSignedEnvelope(key.Trim(), out var signed, out string? signedError) && signed is not null)
                return new LicenseInstallDecision(true, "Signed license installed.", signed);

            return new LicenseInstallDecision(false, signedError ?? VerificationUnavailableMessage, null);
        }

        if (TryInstallSignedEnvelope(key.Trim(), out var verified, out _) && verified is not null)
            return new LicenseInstallDecision(true, "Signed license installed.", verified);

        if (!IsDeveloperAdminKey(key.Trim()))
            return new LicenseInstallDecision(false, UnverifiedKeyMessage, null);

        var license = LicenseInfo.CreateAdministrator(
            licenseKey: key.Trim(),
            customerId: "Administrator",
            email: string.IsNullOrWhiteSpace(email) ? "developer@zenithmacros.dev" : email.Trim(),
            issued: DateTimeOffset.UtcNow,
            expiration: DateTimeOffset.MaxValue);
        return new LicenseInstallDecision(true, "Developer administrator license applied.", license);
    }

    public static bool TryInstallSignedEnvelope(string key, out LicenseInfo? license, out string? error)
    {
        license = null;
        error = null;
        string trimmed = key.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '{')
        {
            error = VerificationUnavailableMessage;
            return false;
        }

        SignedLicenseEnvelope? envelope;
        try
        {
            envelope = System.Text.Json.JsonSerializer.Deserialize<SignedLicenseEnvelope>(
                trimmed,
                SignedLicenseVerifier.JsonOptions);
        }
        catch (Exception ex)
        {
            error = $"License envelope is malformed: {ex.Message}";
            return false;
        }

        if (envelope?.Payload is null || string.IsNullOrWhiteSpace(envelope.Signature))
        {
            error = "License envelope is missing payload or signature.";
            return false;
        }

        var info = SignedLicenseVerifier.AttachSignature(envelope.Payload, envelope.Signature);
        if (!SignedLicenseVerifier.TryVerify(info, out error))
            return false;

        license = info;
        return true;
    }
}

public sealed class SignedLicenseEnvelope
{
    public SignedLicensePayload? Payload { get; init; }
    public string? Signature { get; init; }
}
