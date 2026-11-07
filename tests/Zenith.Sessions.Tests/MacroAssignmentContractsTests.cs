using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class MacroAssignmentContractsTests
{
    private static MacroAssignment Assignment() => new(new(MacroAssignmentTargetKind.Account, Guid.NewGuid()), "Saved account", Guid.NewGuid(), "Saved macro");

    [Fact]
    public void TargetKindsAreDistinctStableNamespacesAndRequireSavedIds()
    {
        var id = Guid.NewGuid(); var account = new MacroAssignmentTarget(MacroAssignmentTargetKind.Account, id).Validated();
        var group = new MacroAssignmentTarget(MacroAssignmentTargetKind.Group, id).Validated();
        var bundle = new MacroAssignmentTarget(MacroAssignmentTargetKind.Bundle, id).Validated();
        Assert.NotEqual(account, group); Assert.NotEqual(group, bundle);
        Assert.Equal(1, (int)account.Kind); Assert.Equal(2, (int)group.Kind); Assert.Equal(3, (int)bundle.Kind);
        Assert.Throws<ArgumentException>(() => (account with { Id = Guid.Empty }).Validated());
        foreach (int kind in new[] { -1, 0, 4, int.MaxValue }) Assert.Throws<ArgumentException>(() => (account with { Kind = (MacroAssignmentTargetKind)kind }).Validated());
    }

    [Fact]
    public void AssignmentRequiresExactNonemptyReferencesAndBoundedSafeDisplayHints()
    {
        var assignment = Assignment(); Assert.Same(assignment, assignment.Validated());
        Assert.Throws<ArgumentException>(() => (assignment with { MacroId = Guid.Empty }).Validated());
        Assert.Throws<ArgumentException>(() => (assignment with { Target = null! }).Validated());
        foreach (string name in new[] { "", " ", new string('x', 201), "bad\0name", "two\nlines", "bad\u2028name", "\ud800", "\udfff",
            "https://fixture.invalid", "www.fixture.invalid", "cookie=fixture", ".ROBLOSECURITY fixture", "Bearer fixture", "access_token=fixture" })
        {
            Assert.Throws<ArgumentException>(() => (assignment with { TargetName = name }).Validated());
            Assert.Throws<ArgumentException>(() => (assignment with { MacroName = name }).Validated());
        }
        Assert.Equal(200, (assignment with { TargetName = new string('x', 200), MacroName = "😀 批次" }).Validated().TargetName.Length);
    }

    [Fact]
    public void DisplayNameCheckNamesTheProblemAndMatchesWhatValidationRejects()
    {
        string lineBreak = "bad" + (char)0x2028 + "name", lone = ((char)0xD800).ToString();
        var cases = new (string? Name, MacroAssignmentNameProblem Problem)[]
        {
            ("Saved account", MacroAssignmentNameProblem.None), (new string('x', 200), MacroAssignmentNameProblem.None), ("😀 批次", MacroAssignmentNameProblem.None),
            ("Robloxian main", MacroAssignmentNameProblem.None), ("Bearer", MacroAssignmentNameProblem.None),
            (null, MacroAssignmentNameProblem.Shape), ("", MacroAssignmentNameProblem.Shape), (" ", MacroAssignmentNameProblem.Shape),
            (new string('x', 201), MacroAssignmentNameProblem.Shape), ("bad\0name", MacroAssignmentNameProblem.Shape), ("two\nlines", MacroAssignmentNameProblem.Shape),
            (lineBreak, MacroAssignmentNameProblem.Shape), (lone, MacroAssignmentNameProblem.InvalidUnicode),
            ("Roblox: main", MacroAssignmentNameProblem.LinkOrCredential), ("ROBLOX-PLAYER:1", MacroAssignmentNameProblem.LinkOrCredential),
            ("https://fixture.invalid", MacroAssignmentNameProblem.LinkOrCredential), ("Visit WWW.fixture", MacroAssignmentNameProblem.LinkOrCredential),
            ("Bearer of Light", MacroAssignmentNameProblem.LinkOrCredential), ("access_token=fixture", MacroAssignmentNameProblem.LinkOrCredential)
        };
        var assignment = Assignment();
        foreach (var (name, problem) in cases)
        {
            Assert.Equal(problem, MacroAssignment.CheckDisplayName(name));
            if (problem == MacroAssignmentNameProblem.None) { (assignment with { TargetName = name!, MacroName = name! }).Validated(); continue; }
            Assert.Throws<ArgumentException>(() => (assignment with { TargetName = name! }).Validated());
            Assert.Throws<ArgumentException>(() => (assignment with { MacroName = name! }).Validated());
        }
    }

    [Fact]
    public void AssignmentSerializationContainsOnlyMetadataAndToStringOmitsDisplayHints()
    {
        var assignment = Assignment() with { TargetName = "private-label-fixture", MacroName = "private-macro-title-fixture" };
        Assert.DoesNotContain(assignment.TargetName, assignment.ToString()); Assert.DoesNotContain(assignment.MacroName, assignment.ToString());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(assignment));
        Assert.Equal(new[] { "MacroId", "MacroName", "Target", "TargetName" }, json.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name));
        Assert.Equal(new[] { "Id", "Kind" }, json.RootElement.GetProperty("Target").EnumerateObject().Select(property => property.Name).OrderBy(name => name));
    }

    [Fact]
    public void StampSeparatesAbsentStoreFromAllCommittedGenerations()
    {
        Assert.Equal(new MacroAssignmentStamp(Guid.Empty, 0), MacroAssignmentStamp.Empty.Validated());
        foreach (var stamp in new[] { new MacroAssignmentStamp(Guid.Empty, 1), new(Guid.NewGuid(), 0), new(Guid.Empty, -1), new(Guid.NewGuid(), -1) })
            Assert.Throws<ArgumentException>(() => stamp.Validated());
        Assert.Equal(long.MaxValue, new MacroAssignmentStamp(Guid.NewGuid(), long.MaxValue).Validated().Revision);
    }

    [Fact]
    public void QueriesBoundPagesAndRequireAReviewedStampAfterTheFirstPage()
    {
        foreach (var query in new[] { new MacroAssignmentQuery(-1), new(10001), new(Limit: 0), new(Limit: 101), new(Offset: 1) })
            Assert.Throws<ArgumentException>(() => query.Validated());
        var expected = new MacroAssignmentStamp(Guid.NewGuid(), 1);
        Assert.Equal(100, new MacroAssignmentQuery(100, 100, expected).Validated().Offset);
    }

    [Fact]
    public void PageValidationFreezesRowsAndRequiresExactCountsCursorAndUniqueTargets()
    {
        var stamp = new MacroAssignmentStamp(Guid.NewGuid(), 7); var first = Assignment(); var second = Assignment();
        var caller = new List<MacroAssignment> { first, second }; var query = new MacroAssignmentQuery(Limit: 2, Expected: stamp);
        var page = new MacroAssignmentPage(stamp, caller, 3, 2).Validated(query); caller.Clear();
        Assert.Equal(new[] { first, second }, page.Items); Assert.Throws<NotSupportedException>(() => ((IList<MacroAssignment>)page.Items).Clear());
        foreach (var malformed in new[] { page with { Total = 10001 }, page with { NextOffset = 1 }, page with { Items = new[] { first, first } },
            page with { Items = Array.Empty<MacroAssignment>() }, page with { Stamp = MacroAssignmentStamp.Empty } })
            Assert.Throws<ArgumentException>(() => malformed.Validated(query with { Expected = null }));
        Assert.Throws<MacroAssignmentConflictException>(() => page.Validated(query with { Expected = stamp with { Revision = 8 } }));
        Assert.Throws<MacroAssignmentConflictException>(() => page.Validated(query with { Expected = stamp with { Generation = Guid.NewGuid() } }));
    }

    [Fact]
    public void EmptyStorePageIsDistinctFromACommittedEmptyLibrary()
    {
        var query = new MacroAssignmentQuery(); var absent = new MacroAssignmentPage(MacroAssignmentStamp.Empty, Array.Empty<MacroAssignment>(), 0, null).Validated(query);
        var committed = absent with { Stamp = new(Guid.NewGuid(), 3) };
        Assert.NotEqual(absent.Stamp, committed.Validated(query).Stamp);
        Assert.Throws<ArgumentException>(() => (absent with { Items = new[] { Assignment() }, Total = 1 }).Validated(query));
    }

    [Fact]
    public void StorageExceptionsUseFixedProblemDescriptions()
    {
        foreach (var problem in Enum.GetValues<MacroAssignmentStorageProblem>())
        {
            var error = new MacroAssignmentStorageException(problem); Assert.Equal(problem, error.Problem); Assert.Null(error.InnerException);
            Assert.DoesNotContain("C:\\", error.Message); Assert.DoesNotContain("SELECT", error.Message);
        }
        Assert.Contains("may have completed", new MacroAssignmentStorageException(MacroAssignmentStorageProblem.OutcomeUnknown).Message);
    }
}
