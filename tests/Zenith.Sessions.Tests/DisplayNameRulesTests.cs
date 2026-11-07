using System.IO;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Favorites, recipient bundles, macro assignments and saved private servers share one display-name rule.
public sealed class DisplayNameRulesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-display-names-" + Guid.NewGuid().ToString("N"));

    // Before the rule was shared, each of these was refused by some of the four validators and accepted by others.
    public static TheoryData<string> OnceDivergentNames => new()
    {
        "www.example.invalid", "roblox:x", "roblox-player:1+launchmode:play", "cookie=x", "roblox.com/share?code=abcd&type=Server",
    };

    [Theory]
    [MemberData(nameof(OnceDivergentNames))]
    public void EveryNewDisplayNameFollowsTheSameRule(string name)
    {
        Assert.Throws<ArgumentException>(() => new SavedPrivateServer(Guid.NewGuid(), name, 1, 1, null, "abcd", 0).Validated());
        Assert.Throws<ArgumentException>(() => new FavoriteDestination(Guid.NewGuid(), name, 1).Validated());
        Assert.Throws<ArgumentException>(() => RecipientBundle.ValidateName(name));
        Assert.Throws<ArgumentException>(() => RecipientBundle.Create(name, [Guid.NewGuid()]));
        Assert.Throws<ArgumentException>(() => new MacroAssignment(new(MacroAssignmentTargetKind.Account, Guid.NewGuid()), name, Guid.NewGuid(), "Macro").Validated());
        Assert.Throws<ArgumentException>(() => new MacroAssignment(new(MacroAssignmentTargetKind.Account, Guid.NewGuid()), "Target", Guid.NewGuid(), name).Validated());
        Assert.Throws<ArgumentException>(() => FavoriteDestination.ValidateName(name));
    }

    [Theory]
    [MemberData(nameof(OnceDivergentNames))]
    public void NamesSavedUnderTheEarlierNarrowerRulesStayReadable(string name)
    {
        // Every store re-checks names on load and treats one bad record as a damaged file, so a saved revision keeps the name it was saved under.
        Assert.Equal(name, new SavedPrivateServer(Guid.NewGuid(), name, 1, 1, null, "abcd", 1).Validated().Name);
        Assert.Equal(name, new FavoriteDestination(Guid.NewGuid(), name, 1, null, 1, Guid.NewGuid()).Validated().Name);
        Assert.Equal(name, new RecipientBundle(Guid.NewGuid(), name, [Guid.NewGuid()], 1, Guid.NewGuid()).Validated().Name);
        var assignment = new MacroAssignment(new(MacroAssignmentTargetKind.Account, Guid.NewGuid()), name, Guid.NewGuid(), name);
        Assert.Same(assignment, assignment.ValidatedSaved());
        Assert.Same(assignment, Assert.Single(new MacroAssignmentPage(new(Guid.NewGuid(), 1), [assignment], 1, null).Validated(new()).Items));
        // Markers that every validator always refused stay refused when a saved record is read back.
        Assert.Throws<ArgumentException>(() => new FavoriteDestination(Guid.NewGuid(), "privateServerLinkCode=x", 1, null, 1, Guid.NewGuid()).Validated());
        Assert.Throws<ArgumentException>(() => new RecipientBundle(Guid.NewGuid(), "Bearer x", [Guid.NewGuid()], 1, Guid.NewGuid()).Validated());
        Assert.Throws<ArgumentException>(() => new SavedPrivateServer(Guid.NewGuid(), "https://x.invalid", 1, 1, null, "abcd", 1).Validated());
        Assert.Throws<ArgumentException>(() => (assignment with { MacroName = ".ROBLOSECURITY x" }).ValidatedSaved());
    }

    [Fact]
    public async Task ASavedFavoriteKeepsItsNameButARenameMeetsTheWholeRule()
    {
        Directory.CreateDirectory(_root); using var store = new FavoriteDestinationsStore(_root);
        var legacy = new FavoriteDestination(Guid.NewGuid(), "Promo code=1", 5, null, 1, Guid.NewGuid());
        await File.WriteAllTextAsync(store.Path, JsonSerializer.Serialize(new
        { Version = 1, Revision = 1, Items = new[] { new { legacy.Id, legacy.Name, legacy.PlaceId, legacy.JobId, legacy.Revision, legacy.ChangeId } } }));
        var loaded = Assert.Single((await store.LoadAsync(default)).Destinations); Assert.Equal("Promo code=1", loaded.Name);
        var kept = await store.SaveAsync(loaded, default); Assert.Equal("Promo code=1", kept.Name);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(kept with { Name = "www.example.invalid" }, default));
        Assert.Equal(kept, Assert.Single((await store.LoadAsync(default)).Destinations));
        Assert.Equal("Promo", (await store.SaveAsync(kept with { Name = "Promo" }, default)).Name);
    }

    [Fact]
    public async Task ASavedBundleKeepsItsNameThroughMemberEditsButARenameMeetsTheWholeRule()
    {
        Directory.CreateDirectory(_root); using var store = new RecipientBundleStore(_root);
        var legacy = new RecipientBundle(Guid.NewGuid(), "Promo code=1", [Guid.NewGuid()], 1, Guid.NewGuid());
        await File.WriteAllTextAsync(store.Path, JsonSerializer.Serialize(new
        { Version = 1, Revision = 1, Items = new[] { new { legacy.Id, legacy.Name, legacy.ProfileIds, legacy.Revision, legacy.ChangeId } } }));
        var loaded = Assert.Single((await store.LoadAsync(default)).Bundles);
        var edited = await store.SaveAsync(loaded with { ProfileIds = [.. loaded.ProfileIds, Guid.NewGuid()] }, default);
        Assert.Equal("Promo code=1", edited.Name); Assert.Equal(2, edited.ProfileIds.Count);
        await Assert.ThrowsAsync<ArgumentException>(() => store.RenameAsync(edited.Id, edited.Revision, edited.ChangeId, "cookie=x", default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(edited with { Name = "roblox:x" }, default));
        var current = Assert.Single((await store.LoadAsync(default)).Bundles); Assert.Equal((edited.Name, edited.Revision), (current.Name, current.Revision));
        Assert.Equal("Promo", (await store.RenameAsync(edited.Id, edited.Revision, edited.ChangeId, "Promo", default)).Name);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
