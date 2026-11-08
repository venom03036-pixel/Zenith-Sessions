using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class PortableBackupCodecTests
{
    private static readonly byte[] Password = "synthetic portable password 4"u8.ToArray();
    private static PortableBackupDocument Fixture()
    {
        var profile = AccountProfile.Create("Synthetic portable") with { Username = "Synthetic_User", Notes = "fixture note", IsFavorite = true, Group = "Fixture", AdditionalGroups = "Other" };
        return new(1, [profile], [new("Fixture", "#12AB34", 0), new("Empty group", null, 1)],
            [new(Guid.NewGuid(), "Fixture game", 123)],
            new("graphite-mint", true, new(), new(MainAccountProfileId: profile.Id), new(), false),
            [new(profile.Id, 456, DateTimeOffset.MaxValue, "synthetic-portable-session-value"u8.ToArray())]);
    }

    [Fact]
    public void PasswordSealedRoundTripIncludesAllMetadataCategoriesAndOwnedSecretBuffers()
    {
        using var original = Fixture(); byte[] file = PortableBackupCodec.Seal(original, Password, default);
        Assert.True(file.AsSpan(0, 8).SequenceEqual("ZNSBACK1"u8));
        Assert.DoesNotContain("synthetic-portable-session-value", Encoding.UTF8.GetString(file));
        Assert.DoesNotContain("Synthetic_User", Encoding.UTF8.GetString(file));
        using var decoded = PortableBackupCodec.Open(file, Password, default);
        Assert.Equal(original.Profiles, decoded.Profiles); Assert.Equal(original.Groups, decoded.Groups);
        Assert.Equal(original.Favorites, decoded.Favorites); Assert.Equal(original.Preferences, decoded.Preferences);
        Assert.Equal(original.SignIns[0].Cookie, decoded.SignIns[0].Cookie);
        var bytes = decoded.SignIns[0].Cookie; decoded.Dispose(); Assert.All(bytes, value => Assert.Equal(0, value));
        Assert.DoesNotContain("synthetic-portable-session-value", decoded.SignIns[0].ToString());
    }

    [Theory]
    [InlineData(0)] [InlineData(8)] [InlineData(12)] [InlineData(16)]
    public void UnsupportedHeaderAndKdfParametersAreRejectedBeforeDerivation(int offset)
    {
        using var document = Fixture(); byte[] file = PortableBackupCodec.Seal(document, Password, default);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(offset), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => PortableBackupCodec.Open(file, Password, default));
    }

    [Theory]
    [InlineData(20)] [InlineData(52)] [InlineData(64)] [InlineData(-1)]
    public void HeaderSaltNonceCiphertextAndTagAreAuthenticated(int offset)
    {
        using var document = Fixture(); byte[] file = PortableBackupCodec.Seal(document, Password, default);
        file[offset < 0 ? file.Length - 1 : offset] ^= 1;
        Assert.Throws<CryptographicException>(() => PortableBackupCodec.Open(file, Password, default));
    }

    [Fact]
    public void WrongPasswordTruncationAndTrailingBytesCannotDecode()
    {
        using var document = Fixture(); byte[] file = PortableBackupCodec.Seal(document, Password, default);
        Assert.Throws<CryptographicException>(() => PortableBackupCodec.Open(file, "synthetic wrong password"u8, default));
        Assert.Throws<InvalidDataException>(() => PortableBackupCodec.Open(file.AsSpan(0, file.Length - 1), Password, default));
        Assert.Throws<InvalidDataException>(() => PortableBackupCodec.Open([.. file, 0], Password, default));
    }

    [Theory]
    [InlineData("unsafe")]
    [InlineData("duplicate")]
    [InlineData("too-many")]
    public void EvenCorrectlySealedUntrustedPayloadsCannotSmuggleAuthorityOrUnboundedCollections(string kind)
    {
        using var fixture = Fixture();
        string json = JsonSerializer.Serialize(fixture, new JsonSerializerOptions { IgnoreReadOnlyProperties = true });
        if (kind == "unsafe") json = json.Insert(1, "\"NativeLaunchVerifications\":[\"trusted\"],");
        else if (kind == "duplicate") json = json.Insert(1, "\"Version\":1,");
        else json = "{\"Profiles\":[" + string.Join(',', Enumerable.Repeat("{}", PortableBackupDocument.MaximumProfiles + 1)) + "],\"Groups\":[],\"Favorites\":[],\"SignIns\":[]}";
        Assert.Throws<InvalidDataException>(() => PortableBackupCodec.Open(SealFixtureJson(json), Password, default));
    }

    [Fact]
    public void ImportPreferencesPreserveLocalSecurityAndAutomaticActionsAndRemapTheMainProfile()
    {
        using var fixture = Fixture(); Guid newId = Guid.NewGuid();
        var current = new SessionsSettings(OAuthClientId: "123", IdleLockMinutes: 5, PanicShortcut: "F12", MuteArrivingClients: true,
            WindowLayout: new(PreferredDisplayId: "local-display", ArrivalAction: ClientArrivalWindowAction.Minimize));
        var merged = fixture.Preferences.MergeInto(current, new Dictionary<Guid, Guid> { [fixture.Profiles[0].Id] = newId });
        Assert.Equal(newId, merged.Performance!.MainAccountProfileId);
        Assert.Equal(current.OAuthClientId, merged.OAuthClientId); Assert.Equal(current.IdleLockMinutes, merged.IdleLockMinutes);
        Assert.Equal(current.PanicShortcut, merged.PanicShortcut); Assert.Equal(current.MuteArrivingClients, merged.MuteArrivingClients);
        Assert.Equal(current.WindowLayout!.ArrivalAction, merged.WindowLayout!.ArrivalAction);
        Assert.Equal(current.WindowLayout.PreferredDisplayId, merged.WindowLayout.PreferredDisplayId);
    }

    private static byte[] SealFixtureJson(string json)
    {
        byte[] clear = Encoding.UTF8.GetBytes(json), file = new byte[PortableBackupCodec.HeaderBytes + clear.Length + PortableBackupCodec.TagBytes];
        "ZNSBACK1"u8.CopyTo(file); BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(12), PortableBackupCodec.Iterations); BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(16), clear.Length);
        RandomNumberGenerator.Fill(file.AsSpan(20, 32)); RandomNumberGenerator.Fill(file.AsSpan(52, 12));
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(Password, file.AsSpan(20, 32), PortableBackupCodec.Iterations, HashAlgorithmName.SHA512, 32);
        try { using var aes = new AesGcm(key, 16); aes.Encrypt(file.AsSpan(52, 12), clear, file.AsSpan(64, clear.Length), file.AsSpan(64 + clear.Length), file.AsSpan(0, 64)); return file; }
        finally { CryptographicOperations.ZeroMemory(clear); CryptographicOperations.ZeroMemory(key); }
    }
}
