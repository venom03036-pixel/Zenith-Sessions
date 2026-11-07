using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MacroSuite.Core.Storage;

namespace MacroSuite.Infrastructure.Storage;

/// <summary>
/// Reads and writes the <c>.zmacro</c> binary macro container format.
/// </summary>
/// <remarks>
/// <para>Binary layout:</para>
/// <code>
/// [4 bytes]  Magic header: 0x5A 0x4D 0x43 0x52  ("ZMCR")
/// [1 byte]   Container version (currently 1)
/// [4 bytes]  CRC32 of the compressed payload (big-endian uint32)
/// [4 bytes]  Compressed payload length N (big-endian int32)
/// [N bytes]  GZip-compressed UTF-8 JSON payload
/// </code>
/// <para>
/// All methods are static — this class has no state.  Thread-safe by design.
/// </para>
/// </remarks>
public static class MacroFileContainer
{
    // Total fixed-size header: 4 (magic) + 1 (ver) + 4 (crc) + 4 (len) = 13 bytes
    private const int HeaderSize = 13;

    /// <summary>
    /// Writes a JSON string as a compressed, checksummed <c>.zmacro</c> container to <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">A writable, seekable stream (e.g., a <see cref="FileStream"/>).</param>
    /// <param name="json">The UTF-8 JSON string to compress and store.</param>
    /// <exception cref="ArgumentNullException"/>
    public static void Write(Stream destination, string json)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        // 1. Compress JSON → compressed bytes
        byte[] compressed = CompressJson(json);

        // 2. CRC32 of compressed bytes
        uint crc = Crc32Helper.Compute(compressed);

        // 3. Write header
        Span<byte> header = stackalloc byte[HeaderSize];
        MacroSchema.MagicHeader.CopyTo(header);                        // bytes 0–3
        header[4] = MacroSchema.ContainerVersion;                       // byte  4
        BinaryPrimitives.WriteUInt32BigEndian(header[5..], crc);        // bytes 5–8
        BinaryPrimitives.WriteInt32BigEndian(header[9..], compressed.Length); // bytes 9–12
        destination.Write(header);

        // 4. Write compressed payload
        destination.Write(compressed);
    }

    /// <summary>
    /// Reads a <c>.zmacro</c> binary container from <paramref name="source"/> and returns the
    /// decompressed JSON payload.
    /// </summary>
    /// <param name="source">A readable stream positioned at the start of the container.</param>
    /// <returns>The UTF-8 JSON string stored inside the container.</returns>
    /// <exception cref="MacroFileFormatException">
    /// Thrown when the magic header is wrong, the container version is unsupported,
    /// or the CRC32 checksum does not match the payload (corruption detected).
    /// </exception>
    public static string ReadJson(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Span<byte> header = stackalloc byte[HeaderSize];
        int read = source.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false);
        if (read < HeaderSize)
        {
            throw new MacroFileFormatException(
                $"File is too small to be a valid .zmacro container ({read} bytes read, expected at least {HeaderSize}).");
        }

        // Validate magic
        ReadOnlySpan<byte> magic = MacroSchema.MagicHeader;
        if (!header[..4].SequenceEqual(magic))
        {
            throw new MacroFileFormatException(
                "File does not have the expected .zmacro magic header (0x5A 0x4D 0x43 0x52). " +
                "It may be a plain JSON file — use TryReadAsLegacyJson instead.");
        }

        // Validate container version
        byte containerVersion = header[4];
        if (containerVersion != MacroSchema.ContainerVersion)
        {
            throw new MacroFileFormatException(
                $"Unsupported .zmacro container version {containerVersion}. " +
                $"This build supports version {MacroSchema.ContainerVersion}.");
        }

        uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(header[5..]);
        int payloadLength = BinaryPrimitives.ReadInt32BigEndian(header[9..]);

        if (payloadLength < 0 || payloadLength > 100 * 1024 * 1024) // 100 MB sanity cap on compressed size
        {
            throw new MacroFileFormatException(
                $"Payload length field {payloadLength} is outside the valid range [0, 104857600].");
        }

        // Read compressed payload
        byte[] compressed = new byte[payloadLength];
        source.ReadExactly(compressed);

        // Verify CRC32
        uint actualCrc = Crc32Helper.Compute(compressed);
        if (actualCrc != expectedCrc)
        {
            throw new MacroFileFormatException(
                $"CRC32 mismatch: expected 0x{expectedCrc:X8}, computed 0x{actualCrc:X8}. " +
                "The file is corrupt or has been tampered with.");
        }

        // Decompress
        return DecompressJson(compressed);
    }

    /// <summary>
    /// Determines whether the stream starts with the <c>.zmacro</c> magic header without
    /// consuming or modifying the stream position.
    /// </summary>
    public static bool HasMagicHeader(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!source.CanSeek || source.Length < 4)
        {
            return false;
        }

        long originalPos = source.Position;
        try
        {
            Span<byte> buf = stackalloc byte[4];
            int read = source.ReadAtLeast(buf, 4, throwOnEndOfStream: false);
            return read == 4 && buf.SequenceEqual(MacroSchema.MagicHeader);
        }
        finally
        {
            source.Position = originalPos;
        }
    }

    /// <summary>
    /// Reads the entire stream as a UTF-8 string (plain JSON fallback for legacy files).
    /// </summary>
    public static string ReadLegacyJson(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return reader.ReadToEnd();
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private static byte[] CompressJson(string json)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(json);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(utf8);
        }
        return ms.ToArray();
    }

    private static string DecompressJson(byte[] compressed)
    {
        using var compressed_ms = new MemoryStream(compressed);
        using var gz = new GZipStream(compressed_ms, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// Fast CRC32 computation using a pre-built lookup table.
/// Implements the standard IEEE 802.3 polynomial (0xEDB88320 reflected).
/// No allocations on the hot path.
/// </summary>
internal static class Crc32Helper
{
    private static readonly uint[] s_table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint entry = i;
            for (int j = 0; j < 8; j++)
            {
                entry = (entry & 1) != 0
                    ? (entry >> 1) ^ 0xEDB88320u
                    : entry >> 1;
            }
            table[i] = entry;
        }
        return table;
    }

    /// <summary>Computes CRC32 over <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc = (crc >> 8) ^ s_table[(crc ^ b) & 0xFF];
        }
        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>Computes CRC32 over a byte array.</summary>
    public static uint Compute(byte[] data) => Compute(data.AsSpan());
}
