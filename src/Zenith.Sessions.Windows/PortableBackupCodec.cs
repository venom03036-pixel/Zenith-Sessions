using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zenith.Sessions.Windows;

/// <summary>Versioned password-sealed portable format. Plaintext exists only in bounded owned memory.</summary>
public static class PortableBackupCodec
{
    public const int MaximumPayloadBytes = 32 * 1024 * 1024;
    public const int HeaderBytes = 64, TagBytes = 16, Iterations = 600000;
    private static readonly byte[] Magic = "ZNSBACK1"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new()
    { MaxDepth = 16, IgnoreReadOnlyProperties = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static byte[] Seal(PortableBackupDocument document, ReadOnlySpan<byte> password, CancellationToken ct)
    {
        ValidatePassword(password, true); ct.ThrowIfCancellationRequested(); document.Validated();
        using var plaintext = new BoundedPlaintextStream(); byte[] key = [];
        try
        {
            using (var writer = new Utf8JsonWriter(plaintext)) JsonSerializer.Serialize(writer, document, Json);
            ct.ThrowIfCancellationRequested();
            int length = checked((int)plaintext.Length);
            byte[] output = new byte[HeaderBytes + length + TagBytes];
            Magic.CopyTo(output, 0); BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8), 1);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(12), Iterations);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(16), length);
            RandomNumberGenerator.Fill(output.AsSpan(20, 32)); RandomNumberGenerator.Fill(output.AsSpan(52, 12));
            key = Rfc2898DeriveBytes.Pbkdf2(password, output.AsSpan(20, 32), Iterations, HashAlgorithmName.SHA512, 32);
            ct.ThrowIfCancellationRequested();
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(output.AsSpan(52, 12), plaintext.GetBuffer().AsSpan(0, length), output.AsSpan(HeaderBytes, length), output.AsSpan(HeaderBytes + length, TagBytes), output.AsSpan(0, HeaderBytes));
            ct.ThrowIfCancellationRequested(); return output;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static PortableBackupDocument Open(ReadOnlySpan<byte> file, ReadOnlySpan<byte> password, CancellationToken ct)
    {
        ValidatePassword(password, false); ct.ThrowIfCancellationRequested();
        if (file.Length < HeaderBytes + TagBytes || !file[..8].SequenceEqual(Magic)
            || BinaryPrimitives.ReadInt32LittleEndian(file[8..]) != 1 || BinaryPrimitives.ReadInt32LittleEndian(file[12..]) != Iterations)
            throw PortableBackupDocument.Invalid();
        int length = BinaryPrimitives.ReadInt32LittleEndian(file[16..]);
        if (length is <= 0 or > MaximumPayloadBytes || file.Length != HeaderBytes + length + TagBytes) throw PortableBackupDocument.Invalid();
        byte[] clear = new byte[length], key = []; PortableBackupDocument? result = null;
        var decodedCookies = new List<byte[]>(); bool transferred = false;
        try
        {
            key = Rfc2898DeriveBytes.Pbkdf2(password, file.Slice(20, 32), Iterations, HashAlgorithmName.SHA512, 32);
            ct.ThrowIfCancellationRequested();
            using (var aes = new AesGcm(key, TagBytes)) aes.Decrypt(file.Slice(52, 12), file.Slice(HeaderBytes, length), file.Slice(HeaderBytes + length, TagBytes), clear, file[..HeaderBytes]);
            ct.ThrowIfCancellationRequested();
            using (var tree = JsonDocument.Parse(clear, new JsonDocumentOptions { MaxDepth = 16 }))
            {
                RejectDuplicateMembers(tree.RootElement, ct);
                BoundArray(tree.RootElement, "Profiles", PortableBackupDocument.MaximumProfiles);
                BoundArray(tree.RootElement, "Groups", PortableBackupDocument.MaximumGroups);
                BoundArray(tree.RootElement, "Favorites", Zenith.Sessions.Core.FavoriteDestination.MaximumCount);
                BoundArray(tree.RootElement, "SignIns", PortableBackupDocument.MaximumProfiles);
            }
            var options = new JsonSerializerOptions(Json); options.Converters.Add(new OwnedSignInReader(decodedCookies));
            result = JsonSerializer.Deserialize<PortableBackupDocument>(clear, options) ?? throw PortableBackupDocument.Invalid();
            result.Validated(); ct.ThrowIfCancellationRequested();
            var owned = result; result = null; transferred = true; return owned;
        }
        catch (CryptographicException) { throw new CryptographicException("The backup password was not accepted or its authenticated contents were changed. No sign-ins were imported."); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        { throw PortableBackupDocument.Invalid(); }
        finally
        {
            result?.Dispose();
            if (!transferred) foreach (var cookie in decodedCookies) CryptographicOperations.ZeroMemory(cookie);
            CryptographicOperations.ZeroMemory(clear); CryptographicOperations.ZeroMemory(key);
        }
    }

    public static async Task<PortableBackupDocument> ReadAsync(string path, ReadOnlyMemory<byte> password, CancellationToken ct)
    {
        byte[] file = [];
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32768, FileOptions.Asynchronous);
            if (stream.Length is <= HeaderBytes + TagBytes or > MaximumPayloadBytes + HeaderBytes + TagBytes) throw PortableBackupDocument.Invalid();
            file = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(file, ct).ConfigureAwait(false);
            return Open(file, password.Span, ct);
        }
        finally { CryptographicOperations.ZeroMemory(file); }
    }
    private static void BoundArray(JsonElement root, string name, int maximum)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > maximum)
            throw PortableBackupDocument.Invalid();
    }
    private static void RejectDuplicateMembers(JsonElement value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw PortableBackupDocument.Invalid(); RejectDuplicateMembers(property.Value, ct); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicateMembers(item, ct);
    }
    private static void ValidatePassword(ReadOnlySpan<byte> password, bool creating)
    { if (password.Length < (creating ? 12 : 1) || password.Length > 1024) throw new ArgumentException("Use an export password of at least 12 UTF-8 bytes and at most 1,024 bytes."); }
    private sealed class OwnedSignInReader(List<byte[]> allocations) : JsonConverter<PortableSavedSignIn>
    {
        public override PortableSavedSignIn Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw PortableBackupDocument.Invalid();
            int seen = 0; Guid profile = default; long user = 0; DateTimeOffset expiry = default; byte[]? cookie = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw PortableBackupDocument.Invalid();
                string? name = reader.GetString(); if (!reader.Read()) throw PortableBackupDocument.Invalid();
                int flag = name switch { "SourceProfileId" => 1, "VerifiedUserId" => 2, "ExpiresUtc" => 4, "Cookie" => 8, _ => 0 };
                if (flag == 0 || (seen & flag) != 0) throw PortableBackupDocument.Invalid(); seen |= flag;
                switch (flag)
                {
                    case 1: profile = reader.GetGuid(); break;
                    case 2: user = reader.GetInt64(); break;
                    case 4: expiry = reader.GetDateTimeOffset(); break;
                    case 8:
                        if (reader.TokenType != JsonTokenType.String || (reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length) > 22000) throw PortableBackupDocument.Invalid();
                        cookie = reader.GetBytesFromBase64(); allocations.Add(cookie); break;
                }
            }
            if (seen != 15 || reader.TokenType != JsonTokenType.EndObject) throw PortableBackupDocument.Invalid();
            return new PortableSavedSignIn(profile, user, expiry, cookie!).Validated();
        }
        public override void Write(Utf8JsonWriter writer, PortableSavedSignIn value, JsonSerializerOptions options) => throw new NotSupportedException();
    }
    private sealed class BoundedPlaintextStream : MemoryStream
    {
        // Reserve once so growth never abandons an uncleared previous plaintext buffer.
        public BoundedPlaintextStream() : base(MaximumPayloadBytes) { }
        private void Bound(int length) { if (Position + length > MaximumPayloadBytes) throw PortableBackupDocument.Invalid(); }
        public override void Write(byte[] buffer, int offset, int count) { Bound(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Bound(buffer.Length); base.Write(buffer); }
        protected override void Dispose(bool disposing) { if (TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan()); base.Dispose(disposing); }
    }
}
