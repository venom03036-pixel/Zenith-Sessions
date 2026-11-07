using System.IO.Compression;
using System.Text;
using MacroSuite.Core.Storage;
using MacroSuite.Infrastructure.Storage;
using Xunit;
using FluentAssertions;

namespace MacroSuite.Infrastructure.Tests.Hardening;

/// <summary>
/// Tests for <see cref="MacroFileContainer"/> and the embedded <see cref="Crc32Helper"/>.
/// All tests use in-memory streams — no disk access required (Rule 18 testability).
/// </summary>
public sealed class MacroFileContainerTests
{
    // ── Round-trip ─────────────────────────────────────────────────────────

    [Fact]
    public void Write_ThenReadJson_ReturnsOriginalJson()
    {
        const string json = """{"name":"Test","steps":[]}""";

        using var ms = new MemoryStream();
        MacroFileContainer.Write(ms, json);
        ms.Position = 0;

        string result = MacroFileContainer.ReadJson(ms);

        result.Should().Be(json);
    }

    [Fact]
    public void Write_ThenReadJson_WorksForLargePayloads()
    {
        // 200 KB JSON (triggers meaningful GZip compression)
        string json = "{\"steps\":[" + string.Join(",", Enumerable.Range(0, 1000).Select(i =>
            $"{{\"$type\":\"delay\",\"stepId\":\"{Guid.NewGuid()}\",\"duration\":{i}}}")) + "]}";

        using var ms = new MemoryStream();
        MacroFileContainer.Write(ms, json);
        ms.Position = 0;

        string result = MacroFileContainer.ReadJson(ms);

        result.Should().Be(json);
    }

    // ── Magic header ───────────────────────────────────────────────────────

    [Fact]
    public void ReadJson_WithBadMagic_ThrowsMacroFileFormatException()
    {
        var bytes = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 1, 0, 0, 0, 0, 0, 0, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0 };
        using var ms = new MemoryStream(bytes);

        var act = () => MacroFileContainer.ReadJson(ms);

        act.Should().Throw<MacroFileFormatException>()
           .WithMessage("*magic*");
    }

    [Fact]
    public void ReadJson_WithTruncatedFile_ThrowsMacroFileFormatException()
    {
        // Only 5 bytes — shorter than the 13-byte header
        var bytes = new byte[] { 0x5A, 0x4D, 0x43, 0x52, 1 };
        using var ms = new MemoryStream(bytes);

        var act = () => MacroFileContainer.ReadJson(ms);

        act.Should().Throw<MacroFileFormatException>()
           .WithMessage("*too small*");
    }

    // ── Container version ──────────────────────────────────────────────────

    [Fact]
    public void ReadJson_WithUnsupportedContainerVersion_ThrowsMacroFileFormatException()
    {
        using var ms = new MemoryStream();
        MacroFileContainer.Write(ms, "{}");

        // Patch version byte (byte index 4) to an unsupported value
        ms.Position = 4;
        ms.WriteByte(255);
        ms.Position = 0;

        var act = () => MacroFileContainer.ReadJson(ms);

        act.Should().Throw<MacroFileFormatException>()
           .WithMessage("*version 255*");
    }

    // ── CRC32 integrity ────────────────────────────────────────────────────

    [Fact]
    public void ReadJson_WithCorruptedCrc_ThrowsMacroFileFormatException()
    {
        using var ms = new MemoryStream();
        MacroFileContainer.Write(ms, """{"name":"intact"}""");

        // Corrupt the CRC field (bytes 5–8)
        ms.Position = 5;
        ms.WriteByte(0xDE);
        ms.WriteByte(0xAD);
        ms.WriteByte(0xBE);
        ms.WriteByte(0xEF);
        ms.Position = 0;

        var act = () => MacroFileContainer.ReadJson(ms);

        act.Should().Throw<MacroFileFormatException>()
           .WithMessage("*CRC32 mismatch*");
    }

    [Fact]
    public void ReadJson_WithCorruptedPayload_ThrowsMacroFileFormatException()
    {
        using var ms = new MemoryStream();
        MacroFileContainer.Write(ms, """{"name":"intact"}""");

        // Flip a byte in the compressed payload (after the 13-byte header)
        ms.Position = 13;
        int b = ms.ReadByte();
        ms.Position = 13;
        ms.WriteByte((byte)(b ^ 0xFF));
        ms.Position = 0;

        var act = () => MacroFileContainer.ReadJson(ms);

        // CRC should catch this before GZip even tries to decompress
        act.Should().Throw<MacroFileFormatException>()
           .WithMessage("*CRC32 mismatch*");
    }

    // ── HasMagicHeader ─────────────────────────────────────────────────────

    [Fact]
    public void HasMagicHeader_ReturnsTrueForZmacroStream()
    {
        using var ms = new MemoryStream();
        MacroFileContainer.Write(ms, "{}");
        ms.Position = 0;

        MacroFileContainer.HasMagicHeader(ms).Should().BeTrue();
        // Stream position should be reset
        ms.Position.Should().Be(0);
    }

    [Fact]
    public void HasMagicHeader_ReturnsFalseForPlainJson()
    {
        var json = Encoding.UTF8.GetBytes("""{"name":"legacy"}""");
        using var ms = new MemoryStream(json);

        MacroFileContainer.HasMagicHeader(ms).Should().BeFalse();
        ms.Position.Should().Be(0);
    }

    // ── Legacy JSON fallback ───────────────────────────────────────────────

    [Fact]
    public void ReadLegacyJson_ReturnsPlainUtf8Content()
    {
        const string json = """{"name":"legacy","schemaVersion":0}""";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));

        string result = MacroFileContainer.ReadLegacyJson(ms);

        result.Should().Be(json);
    }

    // ── CRC32 helper ───────────────────────────────────────────────────────

    [Fact]
    public void Crc32_KnownValue_MatchesIeeeStandard()
    {
        // CRC32 of ASCII "123456789" = 0xCBF43926 per IEEE 802.3
        byte[] input = Encoding.ASCII.GetBytes("123456789");
        uint crc = Crc32Helper.Compute(input);
        crc.Should().Be(0xCBF43926u);
    }

    [Fact]
    public void Crc32_EmptyInput_Returns0xFFFFFFFF_XoredResult()
    {
        // CRC32("") = 0x00000000 per standard
        byte[] empty = Array.Empty<byte>();
        uint crc = Crc32Helper.Compute(empty);
        crc.Should().Be(0x00000000u);
    }

    [Fact]
    public void Crc32_DifferentData_ProducesDifferentHash()
    {
        byte[] a = Encoding.UTF8.GetBytes("hello world");
        byte[] b = Encoding.UTF8.GetBytes("hello World"); // capital W
        Crc32Helper.Compute(a).Should().NotBe(Crc32Helper.Compute(b));
    }

    // ── GZip compression ──────────────────────────────────────────────────

    [Fact]
    public void Write_ProducesCompressedOutput_SmallerThanInput()
    {
        // Highly compressible string
        string json = new string('x', 10_000);
        using var ms = new MemoryStream();
        MacroFileContainer.Write(ms, json);

        // Compressed .zmacro should be much smaller than raw bytes
        ms.Length.Should().BeLessThan(Encoding.UTF8.GetByteCount(json));
    }
}
