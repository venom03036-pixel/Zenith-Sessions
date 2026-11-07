using System.Security.Cryptography;
using System.Text.Json;
using MacroSuite.Core.Storage;
namespace MacroSuite.Infrastructure.Storage;

public sealed partial class ProtectedMacroStore
{
    // Preserve authenticated pre-schema-3 bytes before the first explicit save of an older managed document.
    private void PreservePreviousSchema(string path, byte[] key)
    {
        if (!IsImmediateLibraryFile(path) || !File.Exists(path)) return;
        byte[] original = ReadBounded(path, MaxDecodedBytes + HeaderSize);
        if (!IsV2(original)) throw new MacroProtectionException("Migrate the existing document before overwriting it. Its original was preserved.");
        byte[] plain = Decrypt(original, key, null, 1);
        try
        {
            using var json = JsonDocument.Parse(plain);
            int version = json.RootElement.TryGetProperty("schemaVersion", out var item) ? item.GetInt32() : 1;
            if (version >= 3) return;
            byte[] originalHash = SHA256.HashData(original);
            string hash = Convert.ToHexString(originalHash);
            string backup = Path.Combine(_rootDirectory, "Backups", "BeforeSchema3", Path.GetFileNameWithoutExtension(path) + "-" + hash + ".zmacro");
            if (!File.Exists(backup)) WriteAtomic(backup, original);
            byte[] verified = ReadBounded(backup, MaxDecodedBytes + HeaderSize);
            if (!CryptographicOperations.FixedTimeEquals(originalHash, SHA256.HashData(verified)))
                throw new IOException("The encrypted pre-update copy could not be verified; the original document was not replaced.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}

