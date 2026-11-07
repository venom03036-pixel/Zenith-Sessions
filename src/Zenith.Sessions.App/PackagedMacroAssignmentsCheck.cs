using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

internal static class PackagedMacroAssignmentsCheck
{
    public static async Task VerifyAsync(string root, CancellationToken ct)
    {
        var target = new MacroAssignmentTarget(MacroAssignmentTargetKind.Account, new("f021bb35-f9ad-402e-bbbc-089ae5a6a557"));
        var fixture = new MacroAssignment(target, "Owned packaged account fixture", new("7dc18234-4d56-4686-933a-2d1d0d67ae59"), "Owned packaged macro fixture");
        await using var store = new MacroAssignmentStore(Path.Combine(root, "macro-assignment-fixture"));
        var initial = await store.ReadAsync(new(), ct);
        if (initial.Items.Any(item => item != fixture)) throw new InvalidDataException("The assignment fixture contains unexpected references; they were preserved.");
        var saved = await store.SaveAsync(fixture, initial.Stamp, ct);
        var loaded = await store.ReadAsync(new(Expected: saved.Stamp), ct);
        if (loaded.Items.Count != 1 || loaded.Items[0] != fixture)
            throw new InvalidDataException("Packaged macro assignment references did not round trip.");
        bool rejected = false;
        try { await store.SaveAsync(fixture, new(Guid.NewGuid(), loaded.Stamp.Revision), ct); }
        catch (MacroAssignmentConflictException) { rejected = true; }
        if (!rejected) throw new InvalidDataException("A foreign assignment generation was accepted.");
        var removed = await store.RemoveAsync(target, loaded.Stamp, ct);
        var empty = await store.ReadAsync(new(Expected: removed.Stamp), ct);
        if (empty.Total != 0 || empty.Items.Count != 0) throw new InvalidDataException("The owned assignment reference was not removed.");
    }
}
