using System.Buffers.Binary;
using System.Security.Cryptography;
using Zenith.MacroHost.Contracts;

namespace Zenith.MacroHost.Windows;

internal enum ForegroundCommand : byte { Start = 1, Status = 2, Pause = 3, Resume = 4, Stop = 5 }

/// <summary>One bounded binary exchange. The pairing secret stays out of the pipe.</summary>
internal sealed class ForegroundControlWire(Stream stream, byte[] key, bool server) : IDisposable
{
    private const int NonceSize = 32, TagSize = 32, MaximumFrame = 96;
    private static ReadOnlySpan<byte> Magic => "ZMC1"u8;
    private readonly byte _outbound = server ? (byte)0x53 : (byte)0x43;
    private readonly byte _inbound = server ? (byte)0x43 : (byte)0x53;
    private ulong _readSequence, _writeSequence;

    internal static async Task<ForegroundControlWire> AcceptAsync(Stream stream, byte[] grant, CancellationToken ct)
    {
        byte[] hello = new byte[4 + NonceSize];
        await stream.ReadExactlyAsync(hello, ct).ConfigureAwait(false);
        if (!hello.AsSpan(0, 4).SequenceEqual(Magic)) throw Invalid();
        byte[] hostNonce = RandomNumberGenerator.GetBytes(NonceSize);
        await stream.WriteAsync(hostNonce, ct).ConfigureAwait(false);
        await stream.WriteAsync(Proof(grant, 1, hello, hostNonce), ct).ConfigureAwait(false);
        byte[] supplied = new byte[TagSize];
        await stream.ReadExactlyAsync(supplied, ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(supplied, Proof(grant, 2, hello, hostNonce)))
            throw Invalid();
        return new(stream, Proof(grant, 3, hello, hostNonce), server: true);
    }

    internal static byte[] Proof(byte[] grant, byte role, ReadOnlySpan<byte> hello, ReadOnlySpan<byte> hostNonce)
    {
        Span<byte> transcript = stackalloc byte[1 + 4 + 2 * NonceSize];
        transcript[0] = role;
        hello.CopyTo(transcript[1..]);
        hostNonce.CopyTo(transcript[(1 + 4 + NonceSize)..]);
        return HMACSHA256.HashData(grant, transcript);
    }

    internal async Task<byte[]> ReadAsync(CancellationToken ct)
    {
        byte[] prefix = new byte[2];
        await stream.ReadExactlyAsync(prefix, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(prefix);
        if (length is < 1 or > MaximumFrame) throw Invalid();
        byte[] body = new byte[length], received = new byte[TagSize];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        await stream.ReadExactlyAsync(received, ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(received, Tag(_inbound, checked(++_readSequence), body)))
            throw Invalid();
        return body;
    }

    internal Task WriteAsync(byte[] body, CancellationToken ct)
    {
        if (body.Length is < 1 or > MaximumFrame) throw Invalid();
        byte[] packet = new byte[2 + body.Length + TagSize];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, (ushort)body.Length);
        body.CopyTo(packet, 2);
        Tag(_outbound, checked(++_writeSequence), body).CopyTo(packet, 2 + body.Length);
        return stream.WriteAsync(packet, ct).AsTask();
    }

    private byte[] Tag(byte direction, ulong sequence, ReadOnlySpan<byte> body)
    {
        Span<byte> data = stackalloc byte[1 + 8 + MaximumFrame];
        data[0] = direction;
        BinaryPrimitives.WriteUInt64LittleEndian(data[1..], sequence);
        body.CopyTo(data[9..]);
        return HMACSHA256.HashData(key, data[..(9 + body.Length)]);
    }

    internal static ForegroundControlRequest ParseStart(ReadOnlySpan<byte> body)
    {
        if (body.Length != 69 || body[0] != (byte)ForegroundCommand.Start) throw Invalid();
        var request = new ForegroundControlRequest(new Guid(body.Slice(1, 16), bigEndian: true),
            new Guid(body.Slice(17, 16), bigEndian: true), new Guid(body.Slice(33, 16), bigEndian: true),
            BinaryPrimitives.ReadInt64LittleEndian(body[49..]), BinaryPrimitives.ReadInt32LittleEndian(body[57..]),
            BinaryPrimitives.ReadInt64LittleEndian(body[61..]));
        if (!request.IsValid) throw Invalid();
        return request;
    }

    internal static byte[] Reply(ForegroundCommand command, Guid id, ForegroundControlSnapshot snapshot, bool accepted)
    {
        byte[] response = [(byte)command, (byte)snapshot.State, (byte)snapshot.Outcome, accepted ? (byte)1 : (byte)0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        id.TryWriteBytes(response.AsSpan(4), bigEndian: true, out _);
        return response;
    }

    internal static InvalidDataException Invalid() => new("The foreground control message was refused.");
    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}
