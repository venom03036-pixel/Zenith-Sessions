using System.IO;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class PackagedRecipientBundleCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-packaged-bundle-check-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _fixtureId = Guid.NewGuid(), _sessionFixtureId = Guid.NewGuid();

    public PackagedRecipientBundleCheckTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<IReadOnlyList<RecipientBundle>> BundlesAsync()
    {
        using var store = new RecipientBundleStore(_root);
        return (await store.LoadAsync(CancellationToken.None)).Bundles;
    }

    private async Task<RecipientBundle> SeedAsync(RecipientBundle bundle, string? renameTo = null)
    {
        using var store = new RecipientBundleStore(_root);
        var saved = await store.SaveAsync(bundle, CancellationToken.None);
        return renameTo is null ? saved : await store.RenameAsync(saved.Id, saved.Revision, saved.ChangeId, renameTo, CancellationToken.None);
    }

    private static void AssertSame(RecipientBundle expected, RecipientBundle actual)
    {
        Assert.Equal((expected.Id, expected.Name, expected.Revision, expected.ChangeId), (actual.Id, actual.Name, actual.Revision, actual.ChangeId));
        Assert.Equal(expected.ProfileIds, actual.ProfileIds);
    }

    [Fact]
    public async Task CleanFixtureRoundTripsAndLeavesNoBundle()
    {
        await PackagedRecipientBundleCheck.VerifyAsync(_root, _fixtureId, _sessionFixtureId, CancellationToken.None);
        Assert.Empty(await BundlesAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Synthetic renamed bundle")]
    public async Task LeftoverFixtureBundleFromInterruptedRunIsRemovedAndCheckPasses(string? renameTo)
    {
        // The state a run interrupted after the stage saved (and possibly renamed) its bundle but before it deleted it.
        await SeedAsync(new(_fixtureId, "Synthetic account bundle", [_fixtureId, _sessionFixtureId]), renameTo);

        await PackagedRecipientBundleCheck.VerifyAsync(_root, _fixtureId, _sessionFixtureId, CancellationToken.None);

        Assert.Empty(await BundlesAsync());
    }

    [Fact]
    public async Task ForeignBundleInOwnedFixtureIsPreservedAndRejected()
    {
        var foreign = await SeedAsync(new(Guid.NewGuid(), "Someone else's bundle", [_fixtureId]));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PackagedRecipientBundleCheck.VerifyAsync(_root, _fixtureId, _sessionFixtureId, CancellationToken.None));

        AssertSame(foreign, Assert.Single(await BundlesAsync()));
    }

    [Fact]
    public async Task FixtureBundleWithForeignMemberIsPreservedAndRejected()
    {
        var mixed = await SeedAsync(new(_fixtureId, "Synthetic account bundle", [_fixtureId, Guid.NewGuid()]));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PackagedRecipientBundleCheck.VerifyAsync(_root, _fixtureId, _sessionFixtureId, CancellationToken.None));

        AssertSame(mixed, Assert.Single(await BundlesAsync()));
    }

    [Fact]
    public async Task LeftoverAlongsideAnotherBundleIsPreservedAndRejected()
    {
        var leftover = await SeedAsync(new(_fixtureId, "Synthetic account bundle", [_fixtureId, _sessionFixtureId]));
        var other = await SeedAsync(new(Guid.NewGuid(), "Another bundle", [_sessionFixtureId]));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PackagedRecipientBundleCheck.VerifyAsync(_root, _fixtureId, _sessionFixtureId, CancellationToken.None));

        var remaining = await BundlesAsync();
        Assert.Equal(2, remaining.Count);
        AssertSame(leftover, remaining.Single(item => item.Id == leftover.Id));
        AssertSame(other, remaining.Single(item => item.Id == other.Id));
    }
}
