using System.Security.Cryptography;
using MacroSuite.Core.Storage;

namespace MacroSuite.Infrastructure.Storage;

/// <summary>A random document key protected by the current Windows user's DPAPI credentials.</summary>
public sealed class DpapiMacroKeyStore : ILocalMacroKeyStore
{
    private static readonly byte[] Entropy = "ZenithMacros.ProtectedDocuments.v2"u8.ToArray();
    private readonly string _path;
    private readonly object _gate = new();

    public DpapiMacroKeyStore(string path) => _path = Path.GetFullPath(path);

    public byte[] GetKey(bool createIfMissing)
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                if (!createIfMissing)
                    throw new MacroProtectionException("The macro encryption key is missing. Restore an encrypted backup; your existing files have been kept.");
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                byte[] key = RandomNumberGenerator.GetBytes(32);
                byte[]? wrapped = null;
                string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    wrapped = ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { output.Write(wrapped); output.Flush(flushToDisk: true); }
                    // A fully flushed DPAPI envelope is promoted without replacing a concurrent key.
                    File.Move(temporary, _path, overwrite: false);
                    return key.ToArray();
                }
                catch (IOException) when (File.Exists(_path))
                {
                    // Another process may have created it; validate and use that key below.
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                    if (wrapped is not null) CryptographicOperations.ZeroMemory(wrapped);
                    try { File.Delete(temporary); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { System.Diagnostics.Trace.TraceWarning("An encrypted temporary key envelope could not be removed ({0}).", ex.GetType().Name); }
                }
            }
            try
            {
                var info = new FileInfo(_path);
                if (info.Length is < 1 or > 16384) throw new CryptographicException();
                byte[] wrapped = File.ReadAllBytes(_path);
                try
                {
                    byte[] key = ProtectedData.Unprotect(wrapped, Entropy, DataProtectionScope.CurrentUser);
                    if (key.Length == 32) return key;
                    CryptographicOperations.ZeroMemory(key);
                    throw new CryptographicException();
                }
                finally { CryptographicOperations.ZeroMemory(wrapped); }
            }
            catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException or IOException)
            {
                throw new MacroProtectionException("Windows could not unlock the macro encryption key. Restore an encrypted backup using its passphrase. Your files have been kept.", ex);
            }
        }
    }
}
