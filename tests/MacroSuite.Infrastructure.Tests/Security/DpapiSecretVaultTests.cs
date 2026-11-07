using System.Text;
using FluentAssertions;
using MacroSuite.Infrastructure.Security;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Security;

public class DpapiSecretVaultTests : IDisposable
{
    private readonly string _tempFilePath;

    public DpapiSecretVaultTests()
    {
        _tempFilePath = Path.Combine(Path.GetTempPath(), $"macro_vault_test_{Guid.NewGuid():N}.bin");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFilePath))
        {
            try { File.Delete(_tempFilePath); } catch { }
        }
    }

    [Fact]
    public void SetAndGetSecret_RoundTripsSuccessfully()
    {
        var vault = new DpapiSecretVault(_tempFilePath);

        vault.SetSecret("openai", "sk-proj-test1234567890");
        var retrieved = vault.GetSecret("openai");

        retrieved.Should().Be("sk-proj-test1234567890");
    }

    [Fact]
    public void DiskFile_IsEncryptedAndDoesNotContainPlaintextSecret()
    {
        var vault = new DpapiSecretVault(_tempFilePath);
        const string secret = "super-secret-api-key-value-998877";

        vault.SetSecret("anthropic", secret);

        File.Exists(_tempFilePath).Should().BeTrue();
        byte[] rawBytes = File.ReadAllBytes(_tempFilePath);
        string rawString = Encoding.UTF8.GetString(rawBytes);

        // Rule 10: Assert file on disk does NOT contain plaintext secret
        rawString.Should().NotContain(secret);
    }

    [Fact]
    public void PersistenceAcrossInstances_LoadsExistingSecrets()
    {
        var vault1 = new DpapiSecretVault(_tempFilePath);
        vault1.SetSecret("gemini", "AIzaSyTestKey");

        var vault2 = new DpapiSecretVault(_tempFilePath);
        vault2.HasSecret("gemini").Should().BeTrue();
        vault2.GetSecret("gemini").Should().Be("AIzaSyTestKey");
    }

    [Fact]
    public void RemoveSecret_RemovesAndPersists()
    {
        var vault = new DpapiSecretVault(_tempFilePath);
        vault.SetSecret("test_key", "val123");

        vault.RemoveSecret("test_key").Should().BeTrue();
        vault.HasSecret("test_key").Should().BeFalse();
        vault.GetSecret("test_key").Should().BeNull();

        var reloaded = new DpapiSecretVault(_tempFilePath);
        reloaded.HasSecret("test_key").Should().BeFalse();
    }

    [Fact]
    public void ListSecretKeys_ReturnsAllStoredKeysWithoutValues()
    {
        var vault = new DpapiSecretVault(_tempFilePath);
        vault.SetSecret("openai", "key-a");
        vault.SetSecret("anthropic", "key-b");
        vault.SetSecret("gemini", "key-c");

        var keys = vault.ListSecretKeys();

        keys.Should().BeEquivalentTo(["anthropic", "gemini", "openai"]);
    }
}
