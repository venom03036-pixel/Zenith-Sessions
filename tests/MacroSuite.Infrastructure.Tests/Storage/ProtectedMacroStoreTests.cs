using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using MacroSuite.Infrastructure.Storage;
using OpenCvSharp;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Storage;

public sealed class ProtectedMacroStoreTests : IDisposable
{
    private const string Passphrase = "correct backup passphrase 4971";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-protected-" + Guid.NewGuid().ToString("N"));
    private string Library => Path.Combine(_root, "Macros");
    private readonly TestKeys _keys = new();
    private ProtectedMacroStore Store => new(Library, _keys);
    private static MacroModel Example(string name = "Private macro content must never appear on disk") => new()
    {
        Name = name, Description = "Private description: secret documented sequence.",
        Settings = new MacroSettings { Hotkey = "Ctrl+9", RepeatCount = 7, PlaybackSpeed = 1.5 },
        Steps = [new KeyPressStep(65), new DelayStep(TimeSpan.FromMilliseconds(123))]
    };

    public ProtectedMacroStoreTests() => Directory.CreateDirectory(Library);

    [Fact]
    public void LocalDocument_RoundTripProtectsContentAndPreservesMetadata()
    {
        var store = Store; MacroModel model = Example(); string file = store.GetLibraryPath(model.Id);
        store.Save(model, file);
        byte[] bytes = File.ReadAllBytes(file);
        bytes.AsSpan(0, 4).ToArray().Should().Equal("ZMCR"u8.ToArray()); bytes[4].Should().Be(2);
        Encoding.UTF8.GetString(bytes).Should().NotContain(model.Name).And.NotContain(model.Description);
        new MacroJsonSerializer().Serialize(store.Load(file).Model).Should().Be(new MacroJsonSerializer().Serialize(model));
        Directory.GetFiles(Library, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void RewritingSameMacro_UsesFreshNonceAndCiphertext()
    {
        var store = Store; MacroModel model = Example(); string path = store.GetLibraryPath(model.Id);
        store.Save(model, path); byte[] first = File.ReadAllBytes(path);
        store.Save(model, path); byte[] second = File.ReadAllBytes(path);
        first.AsSpan(36, 12).ToArray().Should().NotEqual(second.AsSpan(36, 12).ToArray());
        first.Should().NotEqual(second); store.Load(path).Model.Name.Should().Be(model.Name);
    }

    [Theory]
    [InlineData(0)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    [InlineData(8)] [InlineData(12)] [InlineData(16)] [InlineData(20)] [InlineData(36)] [InlineData(48)] [InlineData(64)]
    public void AlteredHeaderTagOrPayload_IsRejectedWithoutChangingFile(int offset)
    {
        var store = Store; MacroModel model = Example(); string path = store.GetLibraryPath(model.Id);
        store.Save(model, path); byte[] changed = File.ReadAllBytes(path); changed[offset] ^= 1; File.WriteAllBytes(path, changed);
        Action load = () => store.Load(path); load.Should().Throw<Exception>();
        File.ReadAllBytes(path).Should().Equal(changed);
    }

    [Fact]
    public void WrongKey_FailsAndDoesNotOverwriteTheDocument()
    {
        var store = Store; MacroModel model = Example(); string path = store.GetLibraryPath(model.Id);
        store.Save(model, path); byte[] previous = File.ReadAllBytes(path);
        var wrongKeys = new TestKeys(); wrongKeys.Key[0] ^= 42;
        Action load = () => new ProtectedMacroStore(Library, wrongKeys).Load(path);
        load.Should().Throw<MacroProtectionException>(); File.ReadAllBytes(path).Should().Equal(previous);
    }

    [Fact]
    public void MissingKey_NeverGeneratesReplacementOverExistingCiphertext()
    {
        var store = new ProtectedMacroStore(Library); MacroModel model = Example(); string path = store.GetLibraryPath(model.Id);
        store.Save(model, path); byte[] previous = File.ReadAllBytes(path);
        string keyFile = Path.Combine(Library, "Security", "macro-data-key.bin"); File.Delete(keyFile);
        store.CanWriteLibrary.Should().BeFalse();
        Action save = () => store.Save(Example("other"), store.GetLibraryPath(Guid.NewGuid()));
        save.Should().Throw<MacroProtectionException>(); File.Exists(keyFile).Should().BeFalse(); File.ReadAllBytes(path).Should().Equal(previous);
    }

    [Fact]
    public void NestedTemplatePaths_AreEmbeddedAndExternalImagesAreKept()
    {
        string imagePath = Path.Combine(_root, "external.png"); byte[] png = Png(); File.WriteAllBytes(imagePath, png);
        var image = new ImageCondition(imagePath, 0.91);
        var presence = new VisualPresenceCondition { Kind = VisualPresenceKind.ImagePresent, TemplatePath = imagePath };
        MacroModel model = new() { Name = "Nested", Steps = [new LoopStep(3,
            [new IfThenElseStep(new AndCondition(image, new NotCondition(presence)),
                [new WaitUntilStep(image)], [new LoopStep(1, [new WaitUntilStep(image)])])], new OrCondition(image))] };
        var store = Store; string path = store.GetLibraryPath(model.Id); store.Save(model, path);
        string json = new MacroJsonSerializer().Serialize(store.Load(path).Model);
        json.Should().NotContain(imagePath).And.Contain(Convert.ToBase64String(png));
        File.ReadAllBytes(imagePath).Should().Equal(png);
        var loop = (LoopStep)store.Load(path).Model.Steps[0];
        ((ImageCondition)((OrCondition)loop.WhileCondition!).Conditions[0]).TemplateBytes.Should().Equal(png);
    }

    [Fact]
    public void ArbitraryNonImageReference_IsNotBundledAsAnAttachment()
    {
        string secret = Path.Combine(_root, "not-an-image.txt"); File.WriteAllText(secret, "Sensitive arbitrary file must not be attached.");
        var store = Store; MacroModel model = new() { Steps = [new WaitUntilStep(new ImageCondition(secret))] };
        Action save = () => store.Save(model, store.GetLibraryPath(model.Id));
        save.Should().Throw<Exception>(); Directory.GetFiles(Library, "*.zmacro").Should().BeEmpty();
    }

    [Fact]
    public void AggregateExternalImages_RejectBeforeGrowingDocumentBeyondItsBudget()
    {
        // A small valid PNG with harmless trailing padding lets this test exercise
        // encoded-asset limits without allocating a huge native pixel surface.
        byte[] paddedImage = new byte[32 * 1024 * 1024]; Png().CopyTo(paddedImage, 0);
        string image = Path.Combine(_root, "padded.png"); File.WriteAllBytes(image, paddedImage);
        var model = new MacroModel { Steps = [new WaitUntilStep(new ImageCondition(image)),
            new WaitUntilStep(new ImageCondition(image)), new WaitUntilStep(new ImageCondition(image))] };
        Action save = () => Store.Save(model, Store.GetLibraryPath(model.Id));
        save.Should().Throw<InvalidDataException>().WithMessage("*100 MB*");
        Directory.GetFiles(Library, "*.zmacro").Should().BeEmpty();
    }

    [Fact]
    public void LegacyOwnedJson_MigratesOnlyAfterVerifiedEncryptedRollback()
    {
        MacroModel model = Example(); string source = Path.Combine(Library, "owned.json");
        new MacroJsonSerializer().SerializeToFile(model, source);
        var store = Store; var result = store.MigrateLibraryDocument(source);
        File.Exists(source).Should().BeFalse(); Path.GetExtension(result.SourcePath).Should().Be(".zmacro");
        result.Model.Name.Should().Be(model.Name);
        string rollback = Directory.GetFiles(Path.Combine(Library, "Backups", "LegacyEncrypted"), "*.zrollback").Single();
        Encoding.UTF8.GetString(File.ReadAllBytes(rollback)).Should().NotContain(model.Name);
    }

    [Fact]
    public void LegacyV1Container_MigratesWithAllSettingsAndStepIds()
    {
        MacroModel model = Example(); string source = Path.Combine(Library, "v1.zmacro");
        using (var output = File.Create(source)) MacroFileContainer.Write(output, new MacroJsonSerializer().Serialize(model));
        var result = Store.MigrateLibraryDocument(source);
        File.ReadAllBytes(source)[4].Should().Be(2);
        result.Model.Steps[0].StepId.Should().Be(model.Steps[0].StepId); result.Model.Settings.Hotkey.Should().Be("Ctrl+9");
    }

    [Fact]
    public void MissingTemplate_BlocksMigrationAndPreservesLegacyOriginal()
    {
        var model = new MacroModel { Steps = [new WaitUntilStep(new ImageCondition(Path.Combine(_root, "missing.png")))] };
        string source = Path.Combine(Library, "missing.json"); new MacroJsonSerializer().SerializeToFile(model, source);
        byte[] previous = File.ReadAllBytes(source); Action migrate = () => Store.MigrateLibraryDocument(source);
        migrate.Should().Throw<Exception>(); File.ReadAllBytes(source).Should().Equal(previous);
        Directory.GetFiles(Library, "*.zmacro").Should().BeEmpty();
    }

    [Fact]
    public void ExternalLegacyFile_IsImportedWithoutMigrationOrDeletion()
    {
        string source = Path.Combine(_root, "external.json"); var model = Example(); new MacroJsonSerializer().SerializeToFile(model, source);
        byte[] previous = File.ReadAllBytes(source); var loaded = Store.Load(source);
        loaded.IsLegacy.Should().BeTrue(); Action migration = () => Store.MigrateLibraryDocument(source);
        migration.Should().Throw<ArgumentException>(); File.ReadAllBytes(source).Should().Equal(previous);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    public void LegacyBomEncodedJson_MigratesWithoutLosingInternationalText(string encodingName)
    {
        Encoding encoding = encodingName switch { "utf16le" => new UnicodeEncoding(false, true), "utf16be" => new UnicodeEncoding(true, true), _ => new UTF8Encoding(true) };
        var model = Example("Macro café 日本語"); string path = Path.Combine(Library, "bom.json");
        File.WriteAllBytes(path, encoding.GetPreamble().Concat(encoding.GetBytes(new MacroJsonSerializer().Serialize(model))).ToArray());
        Store.MigrateLibraryDocument(path).Model.Name.Should().Be(model.Name);
    }

    [Fact]
    public void PassphraseBackup_RestoresOnDifferentLocalKeyAndIncludesImages()
    {
        byte[] image = Png(); MacroModel first = Example();
        MacroModel second = new() { Name = "Image macro", Steps = [new WaitUntilStep(new ImageCondition(image))] };
        string backup = Path.Combine(_root, "portable.zbackup"); Store.ExportBackup([first, second], backup, Passphrase);
        var recipient = new ProtectedMacroStore(Path.Combine(_root, "Recipient"), new TestKeys());
        var restored = recipient.Load(backup, Passphrase); restored.Models.Should().HaveCount(2);
        ((ImageCondition)((WaitUntilStep)restored.Models[1].Steps[0]).Condition).TemplateBytes.Should().Equal(image);
        Encoding.UTF8.GetString(File.ReadAllBytes(backup)).Should().NotContain(first.Name).And.NotContain(Convert.ToBase64String(image));
    }

    [Fact]
    public void BackupWrongPassphraseAndDamagedKdfHeader_FailWithoutMutatingInput()
    {
        string backup = Path.Combine(_root, "backup.zbackup"); Store.ExportBackup([Example()], backup, Passphrase);
        byte[] original = File.ReadAllBytes(backup); Action wrong = () => Store.Load(backup, "incorrect passphrase value");
        wrong.Should().Throw<MacroProtectionException>(); File.ReadAllBytes(backup).Should().Equal(original);
        byte[] damaged = original.ToArray(); BinaryPrimitives.WriteInt32BigEndian(damaged.AsSpan(8), int.MaxValue); File.WriteAllBytes(backup, damaged);
        Action tampered = () => Store.Load(backup, Passphrase); tampered.Should().Throw<MacroProtectionException>();
    }

    [Fact]
    public void WrongRecoveryPassphrase_DoesNotCreateKeysOrChangeExistingFiles()
    {
        var store = new ProtectedMacroStore(Library); var model = Example(); store.Save(model, store.GetLibraryPath(model.Id));
        string backup = Path.Combine(_root, "backup.zbackup"); store.ExportBackup([model], backup, Passphrase);
        var before = Directory.GetFiles(Library, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        Action recover = () => store.RecoverLibraryFromBackup(backup, "incorrect backup passphrase"); recover.Should().Throw<MacroProtectionException>();
        Directory.Exists(Path.Combine(Library, "Recovery")).Should().BeFalse();
        foreach (var pair in before) File.ReadAllBytes(pair.Key).Should().Equal(pair.Value);
    }

    [Fact]
    public void LostKeyRecovery_StagesVerifiesAndPreservesEncryptedOriginals()
    {
        var store = new ProtectedMacroStore(Library); MacroModel original = Example(); string oldPath = store.GetLibraryPath(original.Id);
        store.Save(original, oldPath); byte[] oldBytes = File.ReadAllBytes(oldPath);
        MacroModel restored = Example("Restored from portable backup"); string backup = Path.Combine(_root, "backup.zbackup");
        store.ExportBackup([restored], backup, Passphrase);
        string keyPath = Path.Combine(Library, "Security", "macro-data-key.bin"); File.WriteAllBytes(keyPath, [1, 2, 3]);
        store.CanWriteLibrary.Should().BeFalse(); store.RecoverLibraryFromBackup(backup, Passphrase);
        store.CanWriteLibrary.Should().BeTrue(); store.Load(store.GetLibraryPath(restored.Id)).Model.Name.Should().Be(restored.Name);
        File.Exists(oldPath).Should().BeFalse(); File.Exists(Path.Combine(Library, "Recovery.pending")).Should().BeFalse();
        string recovery = Directory.GetDirectories(Path.Combine(Library, "Recovery")).Single();
        Directory.GetFiles(Path.Combine(recovery, "Previous"), "*.zrollback").Should().HaveCount(2);
        foreach (string file in Directory.GetFiles(recovery, "*", SearchOption.AllDirectories))
            Encoding.UTF8.GetString(File.ReadAllBytes(file)).Should().NotContain(original.Description);
        oldBytes.Length.Should().BeGreaterThan(64);
    }

    [Fact]
    public void InterruptedRecoveryMarker_BlocksWritesAndReadsUntilExplicitRecovery()
    {
        var store = new ProtectedMacroStore(Library); MacroModel model = Example(); string path = store.GetLibraryPath(model.Id); store.Save(model, path);
        byte[] previous = File.ReadAllBytes(path); File.WriteAllText(Path.Combine(Library, "Recovery.pending"), "pending");
        store.CanWriteLibrary.Should().BeFalse(); Action load = () => store.Load(path); Action save = () => store.Save(model, path);
        load.Should().Throw<MacroProtectionException>(); save.Should().Throw<MacroProtectionException>(); File.ReadAllBytes(path).Should().Equal(previous);
    }

    [Fact]
    public void SchemaTwo_ExplicitSavePreservesVerifiedEncryptedOriginal_AndLoadDoesNotRewrite()
    {
        var model = Example(); var store = Store; string path = store.GetLibraryPath(model.Id);
        string oldJson = new MacroJsonSerializer().Serialize(model).Replace("\"schemaVersion\": 3", "\"schemaVersion\": 2");
        byte[] plain = Encoding.UTF8.GetBytes(oldJson);
        using var compressedStream = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(compressedStream, System.IO.Compression.CompressionLevel.Fastest, true)) gzip.Write(plain);
        byte[] compressed = compressedStream.ToArray(), original = new byte[64 + compressed.Length];
        "ZMCR"u8.CopyTo(original); original[4] = 2; original[5] = 1; original[6] = 1;
        BinaryPrimitives.WriteInt32BigEndian(original.AsSpan(12), plain.Length);
        BinaryPrimitives.WriteInt32BigEndian(original.AsSpan(16), compressed.Length);
        RandomNumberGenerator.Fill(original.AsSpan(36, 12));
        using (var cipher = new AesGcm(_keys.Key, 16)) cipher.Encrypt(original.AsSpan(36, 12), compressed, original.AsSpan(64), original.AsSpan(48, 16), original.AsSpan(0, 48));
        File.WriteAllBytes(path, original);
        var loaded = store.Load(path).Model;
        File.ReadAllBytes(path).Should().Equal(original);
        store.Save(loaded, path);
        string rollback = Directory.GetFiles(Path.Combine(Library, "Backups", "BeforeSchema3"), "*.zmacro").Single();
        File.ReadAllBytes(rollback).Should().Equal(original);
        store.Load(rollback).Model.Id.Should().Be(model.Id);
        File.ReadAllBytes(path).Should().NotEqual(original);
        store.Save(loaded, path);
        Directory.GetFiles(Path.GetDirectoryName(rollback)!, "*.zmacro").Should().ContainSingle();
    }
    [Fact]
    public void LegacyMigrationNameCollision_PreservesBothOriginals()
    {
        var store = Store; var model = Example(); string protectedPath = store.GetLibraryPath(model.Id);
        store.Save(model, protectedPath); byte[] encrypted = File.ReadAllBytes(protectedPath);
        string source = Path.ChangeExtension(protectedPath, ".json");
        new MacroJsonSerializer().SerializeToFile(Example("Separate older macro"), source);
        byte[] older = File.ReadAllBytes(source);
        Action migrate = () => store.MigrateLibraryDocument(source);
        migrate.Should().Throw<MacroProtectionException>();
        File.ReadAllBytes(source).Should().Equal(older); File.ReadAllBytes(protectedPath).Should().Equal(encrypted);
    }

    private static byte[] Png()
    {
        using var image = new Mat(8, 8, MatType.CV_8UC3, new Scalar(11, 57, 190));
        Cv2.ImEncode(".png", image, out byte[] bytes); return bytes;
    }

    private sealed class TestKeys : ILocalMacroKeyStore
    {
        public byte[] Key { get; } = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        public byte[] GetKey(bool createIfMissing) => Key.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
