using MacroSuite.Core.Models;

namespace MacroSuite.Core.Storage;

/// <summary>Protected document I/O. Plain JSON is an in-memory serialization detail.</summary>
public interface IMacroDocumentStore
{
    bool CanWriteLibrary { get; }
    IReadOnlyList<string> EnumerateLibraryFiles();
    MacroDocumentLoadResult Load(string path, string? passphrase = null);
    MacroDocumentLoadResult MigrateLibraryDocument(string path);
    void Save(MacroModel model, string path);
    void ExportBackup(IReadOnlyList<MacroModel> models, string path, string passphrase);
    /// <summary>Explicitly replaces a lost local key after verifying a portable backup, retaining encrypted recovery archives.</summary>
    void RecoverLibraryFromBackup(string path, string passphrase);
    string GetLibraryPath(Guid id);
}

public sealed record MacroDocumentLoadResult(IReadOnlyList<MacroModel> Models, string SourcePath, bool IsLegacy)
{
    /// <summary>Optional evidence from the same purpose-1 bytes decoded by this call; never synthesized for legacy/external documents.</summary>
    public SavedMacroDocumentReceipt? Receipt { get; init; }

    public MacroModel Model => Models.Count == 1 ? Models[0]
        : throw new InvalidDataException("This document contains more than one macro.");
}

/// <summary>Returns a caller-owned 256-bit content key. Callers clear returned bytes after use.</summary>
public interface ILocalMacroKeyStore
{
    byte[] GetKey(bool createIfMissing);
}

public sealed class MacroProtectionException : IOException
{
    public MacroProtectionException(string message, Exception? inner = null) : base(message, inner) { }
}
