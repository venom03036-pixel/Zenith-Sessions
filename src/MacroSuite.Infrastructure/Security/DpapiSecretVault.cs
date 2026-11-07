using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MacroSuite.Core.Security;
using MacroSuite.Core.Storage;

namespace MacroSuite.Infrastructure.Security;

/// <summary>
/// DPAPI-encrypted secret vault implementing <see cref="ISecretVault"/> (Rule 10).
/// Stores API keys and credentials encrypted using Windows DPAPI (CurrentUser scope).
/// Secrets are never persisted in plaintext, written to logs, or serialized into macros.
/// </summary>
public sealed class DpapiSecretVault : ISecretVault
{
    private static readonly byte[] s_entropy = "ZenithMacros_SecretVault_Salt_v1"u8.ToArray();

    private readonly string _vaultFilePath;
    private readonly object _lock = new();
    private Dictionary<string, string> _secrets;

    public DpapiSecretVault(string? customVaultFilePath = null)
    {
        _vaultFilePath = customVaultFilePath ?? Path.Combine(
            AppDataPathResolver.Default.SecurityDirectory,
            "providers.bin");

        _secrets = LoadVaultInternal();
    }

    /// <inheritdoc/>
    public void SetSecret(string keyName, string secretValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        ArgumentNullException.ThrowIfNull(secretValue);

        lock (_lock)
        {
            _secrets[keyName] = secretValue;
            PersistVaultInternal();
        }
    }

    /// <inheritdoc/>
    public string? GetSecret(string keyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);

        lock (_lock)
        {
            return _secrets.TryGetValue(keyName, out var value) ? value : null;
        }
    }

    /// <inheritdoc/>
    public bool HasSecret(string keyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);

        lock (_lock)
        {
            return _secrets.ContainsKey(keyName);
        }
    }

    /// <inheritdoc/>
    public bool RemoveSecret(string keyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);

        lock (_lock)
        {
            if (_secrets.Remove(keyName))
            {
                PersistVaultInternal();
                return true;
            }

            return false;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> ListSecretKeys()
    {
        lock (_lock)
        {
            return _secrets.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private void PersistVaultInternal()
    {
        string json = JsonSerializer.Serialize(_secrets);
        byte[] plainBytes = Encoding.UTF8.GetBytes(json);
        byte[] encryptedBytes = ProtectedData.Protect(plainBytes, s_entropy, DataProtectionScope.CurrentUser);

        string? dir = Path.GetDirectoryName(_vaultFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(_vaultFilePath, encryptedBytes);
    }

    private Dictionary<string, string> LoadVaultInternal()
    {
        if (!File.Exists(_vaultFilePath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            byte[] encryptedBytes = File.ReadAllBytes(_vaultFilePath);
            byte[] plainBytes = ProtectedData.Unprotect(encryptedBytes, s_entropy, DataProtectionScope.CurrentUser);
            string json = Encoding.UTF8.GetString(plainBytes);

            var dictionary = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return dictionary != null
                ? new Dictionary<string, string>(dictionary, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // If vault is corrupted or machine credentials changed, fail safe with empty state
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
