using System.Security.Cryptography;
using System.Text;
using MacroSuite.Core.Licensing;

namespace MacroSuite.Infrastructure.Licensing;

public sealed class ActivationLicenseStore
{
    private static readonly byte[] s_entropy = "ZenithMacros.Public.Activation.v2"u8.ToArray();
    private readonly string _path;
    public ActivationLicenseStore(string directory) => _path = Path.Combine(directory, "activation-v2.bin");
    public async Task<string?> ReadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return null;
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > ActivationProduct.MaximumFileBytes + 4096) throw new FormatException("Saved activation file is too large.");
        byte[] encrypted = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(encrypted, ct).ConfigureAwait(false);
        byte[] plain = ProtectedData.Unprotect(encrypted, s_entropy, DataProtectionScope.CurrentUser);
        try
        {
            if (plain.Length > ActivationProduct.MaximumFileBytes) throw new FormatException("Saved activation file is too large.");
            return new UTF8Encoding(false, true).GetString(plain);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public async Task SaveAsync(string text, CancellationToken ct = default)
    {
        byte[] plain = Encoding.UTF8.GetBytes(text);
        if (plain.Length > ActivationProduct.MaximumFileBytes) throw new FormatException("License exceeds 64 KiB.");
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, s_entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await stream.WriteAsync(encrypted, ct).ConfigureAwait(false); await stream.FlushAsync(ct).ConfigureAwait(false); stream.Flush(true); }
            ct.ThrowIfCancellationRequested(); File.Move(temp, _path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
