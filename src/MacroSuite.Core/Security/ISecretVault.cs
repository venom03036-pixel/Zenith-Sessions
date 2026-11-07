namespace MacroSuite.Core.Security;

/// <summary>
/// Secure storage contract for API keys and sensitive credentials (Rule 10).
/// Implementations must use platform encryption (e.g. Windows DPAPI) and never persist plaintext.
/// </summary>
public interface ISecretVault
{
    /// <summary>Stores or overwrites an encrypted secret under the specified key name.</summary>
    void SetSecret(string keyName, string secretValue);

    /// <summary>Retrieves and decrypts a secret value by key name, or returns null if not found.</summary>
    string? GetSecret(string keyName);

    /// <summary>Returns true if an encrypted secret exists for the given key name.</summary>
    bool HasSecret(string keyName);

    /// <summary>Deletes an encrypted secret from the vault.</summary>
    bool RemoveSecret(string keyName);

    /// <summary>Lists all key names currently configured in the vault without revealing secrets.</summary>
    IReadOnlyList<string> ListSecretKeys();
}
