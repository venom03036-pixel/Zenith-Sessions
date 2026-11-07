using FluentAssertions;
using MacroSuite.App.Common;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Settings;

public sealed class SettingsJsonStoreTests : IDisposable
{
    private readonly string _dir;

    public SettingsJsonStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ZenithSettingsStore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void Load_MissingFile_IsUntrustedMissing()
    {
        string path = Path.Combine(_dir, "settings.json");
        var loaded = SettingsJsonStore.Load(path);
        loaded.Kind.Should().Be(SettingsLoadKind.Missing);
        loaded.Document.Should().BeNull();
        loaded.TrustSafetyCritical.Should().BeFalse();
    }

    [Fact]
    public void Load_CorruptJson_QuarantinesAndFailsClosed()
    {
        string path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ not json");
        var loaded = SettingsJsonStore.Load(path);
        loaded.Kind.Should().Be(SettingsLoadKind.Corrupt);
        loaded.TrustSafetyCritical.Should().BeFalse();
        Directory.GetFiles(_dir, "settings.json.corrupt-*.bak").Should().NotBeEmpty();
    }

    [Fact]
    public void SaveAtomic_RoundTripsAndKeepsBackup()
    {
        string path = Path.Combine(_dir, "settings.json");
        SettingsJsonStore.SaveAtomic(path, """{"alwaysOnTop":true}""");
        SettingsJsonStore.SaveAtomic(path, """{"alwaysOnTop":false}""");
        File.ReadAllText(path).Should().Contain("false");
        File.Exists(path + ".bak").Should().BeTrue();
        var loaded = SettingsJsonStore.Load(path);
        loaded.Kind.Should().Be(SettingsLoadKind.Loaded);
        loaded.TrustSafetyCritical.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_CorruptOrMissingPrimary_RecoversValidBackupAndRepairsPrimary(bool missing)
    {
        string path = Path.Combine(_dir, "settings.json");
        SettingsJsonStore.SaveAtomic(path, """{"value":"previous"}""");
        SettingsJsonStore.SaveAtomic(path, """{"value":"latest"}""");
        if (missing) File.Delete(path); else File.WriteAllText(path, "broken JSON");
        var loaded = SettingsJsonStore.Load(path);
        using var document = loaded.Document;
        loaded.Kind.Should().Be(SettingsLoadKind.Recovered); loaded.TrustSafetyCritical.Should().BeFalse();
        document!.RootElement.GetProperty("value").GetString().Should().Be("previous");
        loaded.Message.Should().Contain("Recovered"); File.ReadAllText(path).Should().Contain("previous");
        File.ReadAllText(path + ".bak").Should().Contain("previous");
    }

    [Fact]
    public void SavingAfterCorruption_PreservesLastValidBackup()
    {
        string path = Path.Combine(_dir, "settings.json");
        SettingsJsonStore.SaveAtomic(path, """{"value":"previous"}""");
        SettingsJsonStore.SaveAtomic(path, """{"value":"latest"}""");
        File.WriteAllText(path, "corrupt"); SettingsJsonStore.SaveAtomic(path, """{"value":"new"}""");
        File.ReadAllText(path + ".bak").Should().Contain("previous");
        File.ReadAllText(path).Should().Contain("new");
        Directory.GetFiles(_dir, "settings.json.corrupt-*.bak").Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("[]")]
    public void InvalidNewJson_CannotReplacePrimaryOrBackup(string invalid)
    {
        string path = Path.Combine(_dir, "settings.json");
        SettingsJsonStore.SaveAtomic(path, """{"value":1}"""); SettingsJsonStore.SaveAtomic(path, """{"value":2}""");
        string primary = File.ReadAllText(path), backup = File.ReadAllText(path + ".bak");
        Action save = () => SettingsJsonStore.SaveAtomic(path, invalid); save.Should().Throw<Exception>();
        File.ReadAllText(path).Should().Be(primary); File.ReadAllText(path + ".bak").Should().Be(backup);
    }

    [Fact]
    public void CorruptPrimaryAndBackup_FailClosedAndKeepBothFiles()
    {
        string path = Path.Combine(_dir, "settings.json"); File.WriteAllText(path, "bad primary"); File.WriteAllText(path + ".bak", "bad backup");
        var loaded = SettingsJsonStore.Load(path); loaded.Kind.Should().Be(SettingsLoadKind.Corrupt); loaded.Document.Should().BeNull();
        File.ReadAllText(path).Should().Be("bad primary"); File.ReadAllText(path + ".bak").Should().Be("bad backup");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidSchemaOrOversizeBackup_IsRejectedWithoutThrowing(bool oversize)
    {
        string path = Path.Combine(_dir, "settings.json"); File.WriteAllText(path, "bad primary");
        File.WriteAllText(path + ".bak", oversize ? new string('x', 1_000_001) : "{\"schema\":9}");
        var loaded = SettingsJsonStore.Load(path, root => root.TryGetProperty("schema", out var schema) && schema.GetInt32() == 1);
        loaded.Kind.Should().Be(SettingsLoadKind.Corrupt); loaded.Document.Should().BeNull(); loaded.TrustSafetyCritical.Should().BeFalse();
        File.ReadAllText(path).Should().Be("bad primary"); File.Exists(path + ".bak").Should().BeTrue();
    }
}
