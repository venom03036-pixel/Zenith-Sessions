using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class ProfileMetadataImportTests
{
    private static readonly AccountProfile Profile = AccountProfile.Create("Synthetic metadata") with { Username = "synthetic", Group = "Main", AdditionalGroups = "Raid\nShared" };
    private static byte[] Document(int version, params AccountProfile[] rows) => JsonSerializer.SerializeToUtf8Bytes(new { Version = version, Profiles = rows });

    [Fact]
    public void CurrentRoundTripKeepsMembershipButDoesNotSerializeComputedGroupViews()
    {
        byte[] bytes = Document(3, Profile);
        Assert.Equal(Profile.Validated(), Assert.Single(ProfileMetadataImport.Decode(bytes)));
        string json = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("GroupNames", json); Assert.DoesNotContain("GroupsSummary", json);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void LegacyEmptyMembershipsRemainCompatibleButNewMembershipsCannotBeSilentlyLost(int version)
    {
        var old = Profile with { AdditionalGroups = "" };
        Assert.Equal(old, Assert.Single(ProfileMetadataImport.Decode(Document(version, old))));
        Assert.Throws<InvalidDataException>(() => ProfileMetadataImport.Decode(Document(version, Profile)));
        Assert.Throws<InvalidDataException>(() => ProfileMetadataImport.Decode(Document(version, old with { AdditionalGroups = " \n" })));
        Assert.Throws<InvalidDataException>(() => ProfileMetadataImport.Decode(Document(version, old with { AdditionalGroups = "Main" })));
    }

    [Fact]
    public void DuplicateRecipientsAreRejectedInsteadOfCreatingTwoDifferentImportedAccounts()
        => Assert.Throws<InvalidDataException>(() => ProfileMetadataImport.Decode(Document(3, Profile, Profile)));

    [Fact]
    public void Utf8ByteOrderMarkFromAnEditorKeepsTheSameValidMetadata()
    {
        byte[] bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Document(3, Profile)).ToArray();
        Assert.Equal(Profile.Validated(), Assert.Single(ProfileMetadataImport.Decode(bytes)));
    }

    [Theory]
    [InlineData("{\"Version\":3,\"Version\":2,\"Profiles\":[]}")]
    [InlineData("{\"Version\":3,\"Profiles\":[],\"Profiles\":[]}")]
    [InlineData("{\"Version\":3,\"Profiles\":[],\"Credentials\":[]}")]
    [InlineData("{\"Version\":3,\"Profiles\":[],\"version\":3}")]
    [InlineData("{\"Version\":4,\"Profiles\":[]}")]
    [InlineData("{\"Version\":\"3\",\"Profiles\":[]}")]
    [InlineData("{\"Version\":3,\"Profiles\":null}")]
    [InlineData("{\"Version\":3,\"Profiles\":[null]}")]
    [InlineData("{\"Profiles\":[]}")]
    [InlineData("[]")]
    public void AmbiguousOrUnsupportedEnvelopesFailWithFixedNonEchoingFeedback(string json)
    {
        var error = Assert.Throws<InvalidDataException>(() => ProfileMetadataImport.Decode(Encoding.UTF8.GetBytes(json)));
        Assert.DoesNotContain("Credentials", error.Message); Assert.Contains("preserved", error.Message);
    }

    [Theory]
    [InlineData("\"AdditionalGroups\":\"Lost\",\"AdditionalGroups\":\"\"")]
    [InlineData("\"AdditionalGroup\":\"Misspelled\"")]
    [InlineData("\"SessionCookie\":\"synthetic-secret-marker\"")]
    [InlineData("\"GroupNames\":[\"Unrecognized\"]")]
    [InlineData("\"IdentityState\":{}")]
    public void ProfileFieldTyposDuplicatesAndSecretsCannotEnterMetadata(string fields)
    {
        var row = Profile with { AdditionalGroups = "" };
        string baseJson = JsonSerializer.Serialize(row);
        if (fields.StartsWith("\"AdditionalGroups\"", StringComparison.Ordinal))
            baseJson = baseJson.Replace("\"AdditionalGroups\":\"\"", fields, StringComparison.Ordinal);
        else if (fields.StartsWith("\"IdentityState\"", StringComparison.Ordinal))
            baseJson = baseJson.Replace("\"IdentityState\":\"Not linked\"", fields, StringComparison.Ordinal);
        else baseJson = baseJson[..^1] + "," + fields + "}";
        string json = "{\"Version\":3,\"Profiles\":[" + baseJson + "]}";
        var error = Assert.Throws<InvalidDataException>(() => ProfileMetadataImport.Decode(Encoding.UTF8.GetBytes(json)));
        Assert.DoesNotContain("synthetic-secret-marker", error.ToString());
    }

    [Fact]
    public void OversizedAndCancelledImportsDoNotDecodeRows()
    {
        Assert.Throws<InvalidDataException>(() => ProfileMetadataImport.Decode(new byte[ProfileMetadataImport.MaximumBytes + 1]));
        Assert.Throws<InvalidDataException>(() => ProfileMetadataImport.Decode(Array.Empty<byte>()));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => ProfileMetadataImport.Decode(Document(3, Profile), stop.Token));
    }

    [Fact]
    public void TooManyProfilesAreRejectedBeforeIndividualProfileDeserialization()
    {
        string json = "{\"Version\":3,\"Profiles\":[" + string.Join(',', Enumerable.Repeat("{}", ProfileMetadataImport.MaximumProfiles + 1)) + "]}";
        Assert.Throws<InvalidDataException>(() => ProfileMetadataImport.Decode(Encoding.UTF8.GetBytes(json)));
    }
}
