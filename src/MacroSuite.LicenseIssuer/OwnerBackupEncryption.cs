using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MacroSuite.LicenseIssuer;

internal static class OwnerBackupEncryption
{
    private const int Iterations = 600_000;
    private const int HeaderLength = 8 + 4 + 32 + 12 + 16;
    private static ReadOnlySpan<byte> Magic => "ZENISSB2"u8;
    public static byte[] Encrypt(byte[] plain, string password)
    {
        byte[] result = new byte[HeaderLength + plain.Length]; Magic.CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8, 4), Iterations);
        RandomNumberGenerator.Fill(result.AsSpan(12, 32)); RandomNumberGenerator.Fill(result.AsSpan(44, 12));
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, result.AsSpan(12, 32), Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(result.AsSpan(44, 12), plain, result.AsSpan(HeaderLength), result.AsSpan(56, 16), result.AsSpan(0, 44));
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public static byte[] Decrypt(byte[] encrypted, string password)
    {
        if (encrypted.Length <= HeaderLength || !encrypted.AsSpan(0, 8).SequenceEqual(Magic) || BinaryPrimitives.ReadInt32LittleEndian(encrypted.AsSpan(8, 4)) != Iterations)
            throw new CryptographicException("Unsupported owner backup.");
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, encrypted.AsSpan(12, 32), Iterations, HashAlgorithmName.SHA256, 32);
        byte[] plain = new byte[encrypted.Length - HeaderLength];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(encrypted.AsSpan(44, 12), encrypted.AsSpan(HeaderLength), encrypted.AsSpan(56, 16), plain, encrypted.AsSpan(0, 44));
            return plain;
        }
        catch { CryptographicOperations.ZeroMemory(plain); throw; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
