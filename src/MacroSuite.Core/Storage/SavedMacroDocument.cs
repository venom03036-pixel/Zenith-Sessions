using MacroSuite.Core.Models;

namespace MacroSuite.Core.Storage;

/// <summary>
/// Identifies the exact authenticated ciphertext read or published by one owned document operation.
/// This value is not a current-file lease, authorization, or a cross-process compare-and-swap guarantee.
/// </summary>
public sealed record SavedMacroDocumentReceipt(Guid MacroId, string CiphertextSha256, long CiphertextLength)
{
    public const long MinimumCiphertextLength = 65;
    public const long MaximumCiphertextLength = 100L * 1024 * 1024 + 64;

    public SavedMacroDocumentReceipt Validated()
    {
        if (MacroId == Guid.Empty || CiphertextLength is < MinimumCiphertextLength or > MaximumCiphertextLength
            || CiphertextSha256 is null || CiphertextSha256.Length != 64
            || CiphertextSha256.Any(character => character is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new ArgumentException("The saved macro receipt has an invalid identity, digest or bounded ciphertext length.");
        return this;
    }

    public override string ToString() => $"SavedMacroDocumentReceipt {{ MacroId = {MacroId}, CiphertextLength = {CiphertextLength} }}";
}

/// <summary>The caller owns the detached model actually prepared for this successful save; the store caches no body.</summary>
public sealed record SavedMacroDocument(MacroModel SavedModel, SavedMacroDocumentReceipt? Receipt)
{
    public override string ToString() => $"SavedMacroDocument {{ SavedModel = [private], Receipt = {(Receipt is null ? "absent" : "present")} }}";
}

/// <summary>
/// Optional exact-write evidence. Ordinary save behavior is preserved; successful saves outside an eligible
/// canonical owned document return the prepared model with no receipt. No extra document read is required.
/// </summary>
public interface ISavedMacroDocumentStore : IMacroDocumentStore
{
    SavedMacroDocument SaveWithReceipt(MacroModel model, string path);
}
