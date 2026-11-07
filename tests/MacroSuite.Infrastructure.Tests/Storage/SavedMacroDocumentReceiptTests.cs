using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using MacroSuite.Infrastructure.Storage;
using OpenCvSharp;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Storage;

public sealed class SavedMacroDocumentReceiptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-macro-receipt-" + Guid.NewGuid().ToString("N"));
    private readonly TestKeys _keys = new();
    private string Library => Path.Combine(_root, "Macros");
    private ProtectedMacroStore Store => new(Library, _keys);
    public SavedMacroDocumentReceiptTests() => Directory.CreateDirectory(Library);
    private static MacroModel Example(Guid? id = null, string name = "Private synthetic saved body") => new()
    {
        Id = id ?? Guid.NewGuid(), Name = name, Description = "Synthetic contents must remain encrypted on disk",
        Settings = new MacroSettings { Hotkey = "Ctrl+7", PlaybackSpeed = 1.5, RepeatCount = 2 },
        Steps = new List<MacroStep> { new KeyPressStep(65), new DelayStep(TimeSpan.FromMilliseconds(30)) }
    };

    [Fact]
    public void CanonicalWriteAndLoadReceiptsIdentifyExactCiphertextAndPreparedModel()
    {
        ISavedMacroDocumentStore store = Store; var source = Example(); string path = store.GetLibraryPath(source.Id);

        SavedMacroDocument saved = store.SaveWithReceipt(source, path);

        Assert.NotNull(saved.Receipt); Assert.Equal(1, _keys.Requests);
        AssertReceipt(saved.Receipt!, source.Id, File.ReadAllBytes(path));
        var loaded = store.Load(path);
        Assert.Equal(saved.Receipt, loaded.Receipt);
        Assert.Equal(new MacroJsonSerializer().Serialize(saved.SavedModel), new MacroJsonSerializer().Serialize(loaded.Model));
        Assert.DoesNotContain(source.Name, Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        Assert.Empty(Directory.GetFiles(Library, "*.tmp"));
    }

    [Fact]
    public void ExistingVoidSaveStillLoadsWithReceiptAndOriginalThreeFieldDeconstruction()
    {
        IMacroDocumentStore store = Store; var model = Example(); string path = store.GetLibraryPath(model.Id);
        store.Save(model, path);

        var loaded = store.Load(path); var (models, sourcePath, legacy) = loaded;

        Assert.Equal(model.Id, Assert.Single(models).Id); Assert.Equal(path, sourcePath); Assert.False(legacy);
        Assert.NotNull(loaded.Receipt); AssertReceipt(loaded.Receipt!, model.Id, File.ReadAllBytes(path));
        var oldConstructor = new MacroDocumentLoadResult(models, sourcePath, legacy);
        Assert.Null(oldConstructor.Receipt);
    }

    [Fact]
    public void IdenticalResaveGetsFreshCiphertextReceiptAndRetainsModelIdentity()
    {
        var store = Store; var model = Example(); string path = store.GetLibraryPath(model.Id);
        var first = store.SaveWithReceipt(model, path); byte[] firstBytes = File.ReadAllBytes(path);
        var second = store.SaveWithReceipt(model, path); byte[] secondBytes = File.ReadAllBytes(path);

        Assert.NotNull(first.Receipt); Assert.NotNull(second.Receipt);
        Assert.NotEqual(first.Receipt!.CiphertextSha256, second.Receipt!.CiphertextSha256);
        AssertReceipt(first.Receipt, model.Id, firstBytes); AssertReceipt(second.Receipt, model.Id, secondBytes);
        Assert.Equal(new MacroJsonSerializer().Serialize(first.SavedModel), new MacroJsonSerializer().Serialize(second.SavedModel));
    }

    [Fact]
    public void SavedModelIsDetachedFromMutableSourceAndItsLaterMutationCannotChangeDisk()
    {
        var store = Store; var source = Example(); string path = store.GetLibraryPath(source.Id);
        var sourceSteps = (List<MacroStep>)source.Steps;
        var saved = store.SaveWithReceipt(source, path);
        Assert.NotSame(source, saved.SavedModel); Assert.NotSame(source.Settings, saved.SavedModel.Settings);
        Assert.NotSame(source.Steps, saved.SavedModel.Steps); Assert.NotSame(source.Steps[0], saved.SavedModel.Steps[0]);

        source.Settings.PlaybackSpeed = 7; sourceSteps.Clear();

        Assert.Equal(1.5, saved.SavedModel.Settings.PlaybackSpeed); Assert.Equal(2, saved.SavedModel.Steps.Count);
        saved.SavedModel.Settings.RepeatCount = 9;
        var loaded = store.Load(path);
        Assert.Equal(1.5, loaded.Model.Settings.PlaybackSpeed); Assert.Equal(2, loaded.Model.Settings.RepeatCount);
        Assert.Equal(2, loaded.Model.Steps.Count); Assert.Equal(saved.Receipt, loaded.Receipt);
    }

    [Fact]
    public void ReturnedModelIsActualMigratedAndEmbeddedPreparedDocument()
    {
        byte[] image = Png(); string imagePath = Path.Combine(_root, "synthetic.png"); File.WriteAllBytes(imagePath, image);
        var sourceCondition = new ImageCondition(imagePath, 0.9);
        var source = new MacroModel { SchemaVersion = 2, Name = "Prepared synthetic body", Steps = [new WaitUntilStep(sourceCondition)] };
        var store = Store; string path = store.GetLibraryPath(source.Id);

        var saved = store.SaveWithReceipt(source, path);

        Assert.Equal(2, source.SchemaVersion); Assert.Equal(MacroSchema.CurrentVersion, saved.SavedModel.SchemaVersion);
        Assert.Null(sourceCondition.TemplateBytes); Assert.Equal(imagePath, sourceCondition.TemplatePath);
        var prepared = Assert.IsType<ImageCondition>(Assert.IsType<WaitUntilStep>(Assert.Single(saved.SavedModel.Steps)).Condition);
        Assert.Null(prepared.TemplatePath); Assert.Equal(image, prepared.TemplateBytes);
        Assert.NotSame(sourceCondition, prepared);
        Assert.Equal(new MacroJsonSerializer().Serialize(saved.SavedModel), new MacroJsonSerializer().Serialize(store.Load(path).Model));
        AssertReceipt(saved.Receipt!, source.Id, File.ReadAllBytes(path)); Assert.Equal(image, File.ReadAllBytes(imagePath));
    }

    [Fact]
    public void AuthenticatedExternalTemplateLoadsNormallyButNeedsEmbeddedSaveBeforeReceipt()
    {
        byte[] image = Png(); string imagePath = Path.Combine(_root, "external-source.png"); File.WriteAllBytes(imagePath, image);
        var source = new MacroModel { Name = "Historical external image reference", Steps = [new WaitUntilStep(new ImageCondition(imagePath))] };
        var store = Store; string path = store.GetLibraryPath(source.Id);
        WriteAuthenticatedPurposeOne(path, new MacroJsonSerializer().Serialize(source)); byte[] original = File.ReadAllBytes(path);

        var loaded = store.Load(path);

        Assert.False(loaded.IsLegacy); Assert.Null(loaded.Receipt); Assert.Equal(original, File.ReadAllBytes(path));
        var preparedCondition = Assert.IsType<ImageCondition>(Assert.IsType<WaitUntilStep>(Assert.Single(loaded.Model.Steps)).Condition);
        Assert.Equal(image, preparedCondition.TemplateBytes); Assert.Null(preparedCondition.TemplatePath);
        File.Delete(imagePath); // The detached prepared bytes, not the external file, supply the explicit new save.
        var saved = store.SaveWithReceipt(loaded.Model, path);
        Assert.NotNull(saved.Receipt); AssertReceipt(saved.Receipt!, source.Id, File.ReadAllBytes(path));
        Assert.Equal(saved.Receipt, store.Load(path).Receipt);
    }

    [Fact]
    public void SelfContainedOlderSchemaGetsSameReadReceiptWithoutRewritingCiphertext()
    {
        var source = new MacroModel { SchemaVersion = 2, Name = "Historical self-contained body", Steps = [new KeyPressStep(65)] };
        var store = Store; string path = store.GetLibraryPath(source.Id);
        WriteAuthenticatedPurposeOne(path, new MacroJsonSerializer().Serialize(source)); byte[] original = File.ReadAllBytes(path);

        var loaded = store.Load(path);

        Assert.False(loaded.IsLegacy); Assert.Equal(MacroSchema.CurrentVersion, loaded.Model.SchemaVersion);
        Assert.NotNull(loaded.Receipt); AssertReceipt(loaded.Receipt!, source.Id, original);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(loaded.Receipt, store.MigrateLibraryDocument(path).Receipt);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void LoadReceiptUsesTheSameReadBytesEvenIfPathChangesDuringKeyAcquisition()
    {
        var store = Store; Guid id = Guid.NewGuid(); string path = store.GetLibraryPath(id);
        var first = store.SaveWithReceipt(Example(id, "First private body"), path); byte[] firstBytes = File.ReadAllBytes(path);
        var second = store.SaveWithReceipt(Example(id, "Second private body"), path); byte[] secondBytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, firstBytes);
        _keys.NextRequest = () => File.WriteAllBytes(path, secondBytes);

        var loaded = store.Load(path);

        Assert.Equal("First private body", loaded.Model.Name); Assert.Equal(first.Receipt, loaded.Receipt);
        AssertReceipt(loaded.Receipt!, id, firstBytes); Assert.Equal(secondBytes, File.ReadAllBytes(path));
        Assert.Equal(second.Receipt, store.Load(path).Receipt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataUncertaintyRemovesReceiptWithoutBreakingTheAlreadyAuthenticatedLoad(bool replacedByDirectory)
    {
        var store = Store; var model = Example(); string path = store.GetLibraryPath(model.Id);
        store.SaveWithReceipt(model, path);
        _keys.NextRequest = () =>
        {
            File.Delete(path);
            if (replacedByDirectory) Directory.CreateDirectory(path);
        };

        var loaded = store.Load(path);

        Assert.Equal(model.Id, loaded.Model.Id); Assert.Equal(model.Name, loaded.Model.Name);
        Assert.False(loaded.IsLegacy); Assert.Null(loaded.Receipt);
        Assert.Equal(replacedByDirectory, Directory.Exists(path)); Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("friendly")]
    [InlineData("nested")]
    [InlineData("external")]
    [InlineData("other-id")]
    public void NoncanonicalSavePathsRemainUsableButNeverReceiveReceipt(string kind)
    {
        var store = Store; var model = Example(); string filename = Path.GetFileName(store.GetLibraryPath(model.Id));
        string path = kind switch
        {
            "friendly" => Path.Combine(Library, "Friendly saved name.zmacro"),
            "nested" => Path.Combine(Library, "Nested", filename),
            "external" => Path.Combine(_root, "External", filename),
            _ => store.GetLibraryPath(Guid.NewGuid())
        };

        var saved = store.SaveWithReceipt(model, path); var loaded = store.Load(path);

        Assert.Null(saved.Receipt); Assert.Null(loaded.Receipt); Assert.False(loaded.IsLegacy);
        Assert.Equal(model.Id, saved.SavedModel.Id); Assert.Equal(model.Id, loaded.Model.Id);
        Assert.Equal(new MacroJsonSerializer().Serialize(saved.SavedModel), new MacroJsonSerializer().Serialize(loaded.Model));
    }

    [Fact]
    public void EmptyIdRetainsExistingSaveAndLoadBehaviorWithNoReceipt()
    {
        var store = Store; var model = Example(Guid.Empty); string path = store.GetLibraryPath(model.Id);
        store.Save(model, path);

        var saved = store.SaveWithReceipt(model, path); var loaded = store.Load(path);

        Assert.Equal(Guid.Empty, saved.SavedModel.Id); Assert.Equal(Guid.Empty, loaded.Model.Id);
        Assert.Null(saved.Receipt); Assert.Null(loaded.Receipt);
    }

    [Fact]
    public void FirstSaveCanCreateItsCanonicalDirectoryAndReturnReceipt()
    {
        string fresh = Path.Combine(_root, "Fresh", "Macros"); var store = new ProtectedMacroStore(fresh, _keys);
        var model = Example(); string path = store.GetLibraryPath(model.Id);
        Assert.False(Directory.Exists(fresh));

        var saved = store.SaveWithReceipt(model, path);

        Assert.NotNull(saved.Receipt); AssertReceipt(saved.Receipt!, model.Id, File.ReadAllBytes(path));
    }

    [Fact]
    public void PurposeTwoBackupAtCanonicalLookingPathHasNoSavedDocumentReceipt()
    {
        const string passphrase = "Synthetic backup passphrase 4619";
        var store = Store; var model = Example(); string path = store.GetLibraryPath(model.Id);
        store.ExportBackup([model], path, passphrase);

        var loaded = store.Load(path, passphrase);

        Assert.False(loaded.IsLegacy); Assert.Equal(model.Id, loaded.Model.Id); Assert.Null(loaded.Receipt);
    }

    [Fact]
    public void CanonicalLookingPlainLegacyLoadRemainsReadOnlyAndHasNoReceipt()
    {
        var store = Store; var model = Example(); string path = store.GetLibraryPath(model.Id);
        new MacroJsonSerializer().SerializeToFile(model, path); byte[] before = File.ReadAllBytes(path);

        var loaded = store.Load(path);

        Assert.True(loaded.IsLegacy); Assert.Null(loaded.Receipt); Assert.Equal(model.Id, loaded.Model.Id);
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.Equal(0, _keys.Requests);
    }

    [Theory]
    [InlineData(".json")]
    [InlineData(".zmacro")]
    public void CanonicalLegacyMigrationReturnsReceiptFromFinalProtectedVerificationRead(string extension)
    {
        var store = Store; var model = Example(); string canonical = store.GetLibraryPath(model.Id);
        string source = Path.ChangeExtension(canonical, extension); new MacroJsonSerializer().SerializeToFile(model, source);
        Assert.Null(store.Load(source).Receipt);

        var migrated = store.MigrateLibraryDocument(source);

        Assert.False(migrated.IsLegacy); Assert.Equal(canonical, migrated.SourcePath); Assert.NotNull(migrated.Receipt);
        AssertReceipt(migrated.Receipt!, model.Id, File.ReadAllBytes(canonical));
        Assert.Equal(migrated.Receipt, store.Load(canonical).Receipt);
        Assert.Single(Directory.GetFiles(Path.Combine(Library, "Backups", "LegacyEncrypted"), "*.zrollback"));
        if (extension == ".json") Assert.False(File.Exists(source));
    }

    [Fact]
    public void NoncanonicalMigrationKeepsItsExistingFilenameAndNullReceipt()
    {
        var store = Store; var model = Example(); string source = Path.Combine(Library, "Friendly legacy name.json");
        new MacroJsonSerializer().SerializeToFile(model, source);

        var migrated = store.MigrateLibraryDocument(source);

        Assert.Equal(Path.ChangeExtension(source, ".zmacro"), migrated.SourcePath); Assert.False(migrated.IsLegacy);
        Assert.Equal(model.Id, migrated.Model.Id); Assert.Null(migrated.Receipt); Assert.False(File.Exists(source));
        Assert.False(File.Exists(store.GetLibraryPath(model.Id)));
    }

    [Fact]
    public void FailedAtomicReplacementReturnsNoResultAndPreservesExistingCiphertext()
    {
        var store = Store; var original = Example(); string path = store.GetLibraryPath(original.Id);
        var saved = store.SaveWithReceipt(original, path); byte[] before = File.ReadAllBytes(path);
        SavedMacroDocument? returned = null;
        using (var pinned = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var denial = Record.Exception(() => { returned = store.SaveWithReceipt(Example(original.Id, "Uncommitted private replacement"), path); });
            Assert.True(denial is IOException or UnauthorizedAccessException);
        }

        Assert.Null(returned); Assert.Equal(before, File.ReadAllBytes(path)); Assert.Equal(saved.Receipt, store.Load(path).Receipt);
        Assert.Empty(Directory.GetFiles(Library, "*.tmp"));
    }

    [Fact]
    public void FailedPreparationDoesNotPublishReceiptOrChangeOriginal()
    {
        var store = Store; var original = Example(); string path = store.GetLibraryPath(original.Id);
        var saved = store.SaveWithReceipt(original, path); byte[] before = File.ReadAllBytes(path);
        var invalid = new MacroModel { Id = original.Id, Steps = [new WaitUntilStep(new ImageCondition(Path.Combine(_root, "Missing.png")))] };

        Assert.ThrowsAny<IOException>(() => store.SaveWithReceipt(invalid, path));

        Assert.Equal(before, File.ReadAllBytes(path)); Assert.Equal(saved.Receipt, store.Load(path).Receipt);
        Assert.Empty(Directory.GetFiles(Library, "*.tmp"));
    }

    [Fact]
    public void FailedAuthenticationNeverReturnsReceiptOrChangesCiphertext()
    {
        var store = Store; var model = Example(); string path = store.GetLibraryPath(model.Id);
        store.SaveWithReceipt(model, path); byte[] before = File.ReadAllBytes(path);
        var wrong = new TestKeys(); wrong.Key[0] ^= 0x5a; var reader = new ProtectedMacroStore(Library, wrong);

        Assert.Throws<MacroProtectionException>(() => reader.Load(path));

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ReceiptValidationIsStrictAndResultDescriptionsContainNoBodyOrDigest()
    {
        Guid id = Guid.NewGuid(); string digest = new('A', 64);
        var valid = new SavedMacroDocumentReceipt(id, digest, 65); Assert.Same(valid, valid.Validated());
        SavedMacroDocumentReceipt[] invalid = [valid with { MacroId = Guid.Empty }, valid with { CiphertextSha256 = "" },
            valid with { CiphertextSha256 = new('a', 64) }, valid with { CiphertextSha256 = new('G', 64) },
            valid with { CiphertextSha256 = new('A', 63) }, valid with { CiphertextLength = 64 },
            valid with { CiphertextLength = SavedMacroDocumentReceipt.MaximumCiphertextLength + 1 }];
        foreach (var receipt in invalid) Assert.Throws<ArgumentException>(() => receipt.Validated());
        Assert.DoesNotContain(digest, valid.ToString());
        var document = new SavedMacroDocument(Example(name: "Private body marker"), valid);
        Assert.DoesNotContain("Private body marker", document.ToString()); Assert.DoesNotContain(digest, document.ToString());
    }

    private static void AssertReceipt(SavedMacroDocumentReceipt receipt, Guid id, byte[] ciphertext)
    {
        Assert.Same(receipt, receipt.Validated()); Assert.Equal(id, receipt.MacroId);
        Assert.Equal(ciphertext.LongLength, receipt.CiphertextLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(ciphertext)), receipt.CiphertextSha256);
    }
    private static byte[] Png()
    {
        using var image = new Mat(8, 8, MatType.CV_8UC3, new Scalar(11, 57, 190));
        Cv2.ImEncode(".png", image, out byte[] bytes); return bytes;
    }
    private void WriteAuthenticatedPurposeOne(string path, string json)
    {
        byte[] plain = Encoding.UTF8.GetBytes(json), compressed;
        using (var output = new MemoryStream())
        {
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(plain);
            compressed = output.ToArray();
        }
        byte[] ciphertext = new byte[64 + compressed.Length];
        try
        {
            "ZMCR"u8.CopyTo(ciphertext); ciphertext[4] = 2; ciphertext[5] = 1; ciphertext[6] = 1;
            BinaryPrimitives.WriteInt32BigEndian(ciphertext.AsSpan(12), plain.Length);
            BinaryPrimitives.WriteInt32BigEndian(ciphertext.AsSpan(16), compressed.Length);
            RandomNumberGenerator.Fill(ciphertext.AsSpan(36, 12));
            using (var cipher = new AesGcm(_keys.Key, 16))
                cipher.Encrypt(ciphertext.AsSpan(36, 12), compressed, ciphertext.AsSpan(64), ciphertext.AsSpan(48, 16), ciphertext.AsSpan(0, 48));
            File.WriteAllBytes(path, ciphertext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(compressed);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }
    private sealed class TestKeys : ILocalMacroKeyStore
    {
        public byte[] Key { get; } = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        public int Requests { get; private set; }
        public Action? NextRequest { get; set; }
        public byte[] GetKey(bool createIfMissing)
        {
            Requests++; var action = NextRequest; NextRequest = null; action?.Invoke(); return Key.ToArray();
        }
    }
    public void Dispose()
    {
        string owned = Path.GetFullPath(_root), temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!owned.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(owned).StartsWith("zenith-macro-receipt-", StringComparison.Ordinal))
            throw new InvalidOperationException("The synthetic receipt fixture does not own its cleanup path.");
        if (Directory.Exists(owned)) Directory.Delete(owned, recursive: true);
        CryptographicOperations.ZeroMemory(_keys.Key);
    }
}
