using MacroSuite.Core.Models;
using MacroSuite.Core.Storage;
using MacroSuite.Infrastructure.Storage;
using Xunit;
using FluentAssertions;

namespace MacroSuite.Infrastructure.Tests.Hardening;

/// <summary>
/// Integration tests for <see cref="MacroFileRepository"/>.
/// Each test uses a temporary directory that is cleaned up after the test.
/// </summary>
public sealed class MacroFileRepositoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppDataPathResolver _resolver;
    private readonly MacroFileRepository _sut;

    public MacroFileRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ZenithMacroRepoTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "Macros"));
        _resolver = new AppDataPathResolver(_tempDir);
        _sut = new MacroFileRepository(_resolver);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── SaveAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_CreatesZmacroFile()
    {
        var model = MakeModel("Save Test");

        await _sut.SaveAsync(model);

        string expectedPath = Path.Combine(_tempDir, "Macros", model.Id + ".zmacro");
        File.Exists(expectedPath).Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_DoesNotLeaveOrphanedTmpFile()
    {
        var model = MakeModel("Atomic Test");

        await _sut.SaveAsync(model);

        var tmpFiles = Directory.GetFiles(Path.Combine(_tempDir, "Macros"), "*.tmp");
        tmpFiles.Should().BeEmpty("no .tmp file should remain after a successful save");
    }

    [Fact]
    public async Task SaveAsync_WritesValidZmacroContainer()
    {
        var model = MakeModel("Container Integrity Test");

        await _sut.SaveAsync(model);

        string path = Path.Combine(_tempDir, "Macros", model.Id + ".zmacro");
        using var fs = File.OpenRead(path);
        // Should not throw — verifies magic, version, and CRC
        var act = () => MacroFileContainer.ReadJson(fs);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task SaveAsync_OverwritesExistingFile()
    {
        var model = MakeModel("Overwrite Test");
        await _sut.SaveAsync(model);

        long firstSize = new FileInfo(Path.Combine(_tempDir, "Macros", model.Id + ".zmacro")).Length;

        // Saving again should overwrite cleanly
        await _sut.SaveAsync(model);

        long secondSize = new FileInfo(Path.Combine(_tempDir, "Macros", model.Id + ".zmacro")).Length;
        secondSize.Should().Be(firstSize);
    }

    // ── SaveBatchAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task SaveBatchAsync_SavesAllModels()
    {
        var models = Enumerable.Range(0, 50)
            .Select(i => MakeModel($"Batch Macro {i}"))
            .ToArray();

        await _sut.SaveBatchAsync(models, concurrencyLimit: 4);

        foreach (var model in models)
        {
            string path = Path.Combine(_tempDir, "Macros", model.Id + ".zmacro");
            File.Exists(path).Should().BeTrue($"macro '{model.Name}' should have been saved");
        }
    }

    [Fact]
    public async Task SaveBatchAsync_WithConcurrencyOne_SavesAllModels()
    {
        var models = Enumerable.Range(0, 10)
            .Select(i => MakeModel($"Sequential {i}"))
            .ToArray();

        await _sut.SaveBatchAsync(models, concurrencyLimit: 1);

        Directory.GetFiles(Path.Combine(_tempDir, "Macros"), "*.zmacro")
                 .Should().HaveCount(10);
    }

    [Fact]
    public async Task SaveBatchAsync_WithHighConcurrency_SavesAllModels()
    {
        var models = Enumerable.Range(0, 100)
            .Select(i => MakeModel($"Parallel {i}"))
            .ToArray();

        await _sut.SaveBatchAsync(models, concurrencyLimit: 16);

        Directory.GetFiles(Path.Combine(_tempDir, "Macros"), "*.zmacro")
                 .Should().HaveCount(100);
    }

    [Fact]
    public async Task SaveBatchAsync_InvalidConcurrencyLimit_Throws()
    {
        var act = async () => await _sut.SaveBatchAsync(Array.Empty<MacroModel>(), concurrencyLimit: 0);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // ── Load ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Load_AfterSave_ReturnsSameModel()
    {
        var model = MakeModel("Load Roundtrip");
        await _sut.SaveAsync(model);

        var loaded = _sut.Load(model.Id.ToString());

        loaded.Id.Should().Be(model.Id);
        loaded.Name.Should().Be(model.Name);
    }

    [Fact]
    public void Load_MissingId_ThrowsFileNotFoundException()
    {
        var act = () => _sut.Load(Guid.NewGuid().ToString());
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Load_LegacyJsonFile_DeserializesCorrectly()
    {
        // Write a plain JSON file (legacy format) directly
        var model = MakeModel("Legacy JSON");
        var serializer = new MacroJsonSerializer();
        string json = serializer.Serialize(model);
        string jsonPath = Path.Combine(_tempDir, "Macros", model.Id + ".json");
        File.WriteAllText(jsonPath, json);

        var loaded = _sut.Load(model.Id.ToString());

        loaded.Name.Should().Be(model.Name);
    }

    // ── LoadAll ────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAll_ReturnsBothZmacroAndLegacyJsonFiles()
    {
        // One binary file
        var binaryModel = MakeModel("Binary Macro");
        await _sut.SaveAsync(binaryModel);

        // One legacy JSON file (no binary twin)
        var legacyModel = MakeModel("Legacy Macro");
        var serializer = new MacroJsonSerializer();
        File.WriteAllText(
            Path.Combine(_tempDir, "Macros", legacyModel.Id + ".json"),
            serializer.Serialize(legacyModel));

        var all = _sut.LoadAll();

        all.Should().HaveCount(2);
        all.Select(m => m.Id).Should().BeEquivalentTo(new[] { binaryModel.Id, legacyModel.Id });
    }

    [Fact]
    public async Task LoadAll_PrefersZmacroOverJsonForSameStem()
    {
        var model = MakeModel("Prefer Binary");
        await _sut.SaveAsync(model);

        // Also write a stale JSON file with the same ID
        // Also write a stale JSON file with the same ID (MacroModel is a class, not record)
        var serializer = new MacroJsonSerializer();
        string staleName = "STALE";
        var staleModel = new MacroModel { Id = model.Id, Name = staleName, SchemaVersion = MacroSchema.CurrentVersion };
        var staleJson = serializer.Serialize(staleModel);
        File.WriteAllText(
            Path.Combine(_tempDir, "Macros", model.Id + ".json"),
            staleJson);

        var all = _sut.LoadAll();

        // Should load only once (from .zmacro) and NOT use the stale JSON name
        all.Should().HaveCount(1);
        all[0].Name.Should().NotBe(staleName);
    }

    [Fact]
    public async Task LoadAll_SkipsCorruptFiles()
    {
        var good = MakeModel("Good Macro");
        await _sut.SaveAsync(good);

        // Write a corrupt .zmacro file
        File.WriteAllBytes(
            Path.Combine(_tempDir, "Macros", "corrupt.zmacro"),
            new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        var all = _sut.LoadAll();

        // Good file loaded; corrupt file skipped without exception
        all.Should().HaveCount(1);
        all[0].Id.Should().Be(good.Id);
    }

    [Fact]
    public void LoadAll_EmptyDirectory_ReturnsEmptyList()
    {
        var all = _sut.LoadAll();
        all.Should().BeEmpty();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static MacroModel MakeModel(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        SchemaVersion = MacroSchema.CurrentVersion,
    };
}
