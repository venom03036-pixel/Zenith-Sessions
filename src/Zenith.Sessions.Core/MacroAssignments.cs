using System.Text;

namespace Zenith.Sessions.Core;

public enum MacroAssignmentTargetKind { Account = 1, Group = 2, Bundle = 3 }
public sealed record MacroAssignmentTarget(MacroAssignmentTargetKind Kind, Guid Id)
{
    public MacroAssignmentTarget Validated()
    {
        if (!Enum.IsDefined(Kind) || Id == Guid.Empty) throw new ArgumentException("A saved account, group or bundle identity is required.");
        return this;
    }
}

/// <summary>Why a display hint cannot be stored. None means it can.</summary>
public enum MacroAssignmentNameProblem { None = 0, Shape = 1, InvalidUnicode = 2, LinkOrCredential = 3 }

/// <summary>Durable metadata only. Names are last-observed hints; references never authorize execution.</summary>
public sealed record MacroAssignment(MacroAssignmentTarget Target, string TargetName, Guid MacroId, string MacroName)
{
    public const int MaximumCount = 10_000, MaximumPageSize = 100, MaximumNameLength = 200;
    public MacroAssignment Validated() => Validated(saved: false);
    /// <summary>Checks an assignment read back from storage. Its names met the rule in force when they were saved, so a marker
    /// the shared display-name rule added later never makes a saved row unreadable.</summary>
    public MacroAssignment ValidatedSaved() => Validated(saved: true);
    private MacroAssignment Validated(bool saved)
    {
        if (Target is null || MacroId == Guid.Empty) throw new ArgumentException("A saved target and macro identity are required.");
        Target.Validated();
        if (!IsPlain(TargetName) || !IsPlain(MacroName))
            throw new ArgumentException("Use display names of 1 to 200 characters on one line, without links, access codes or credentials.");
        return this;
        bool IsPlain(string? name) => saved ? DisplayNameRules.IsReadable(name, MaximumNameLength) : DisplayNameRules.IsPlain(name, MaximumNameLength);
    }
    /// <summary>Explains why <see cref="Validated()"/> would refuse a new name. The decision is the shared
    /// <see cref="DisplayNameRules.IsPlain"/> rule; this only names the reason, and the result never contains the name.</summary>
    public static MacroAssignmentNameProblem CheckDisplayName(string? name)
    {
        if (DisplayNameRules.IsPlain(name, MaximumNameLength)) return MacroAssignmentNameProblem.None;
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaximumNameLength
            || name.Any(character => char.IsControl(character) || character == (char)0x2028 || character == (char)0x2029))
            return MacroAssignmentNameProblem.Shape;
        try { new UTF8Encoding(false, true).GetByteCount(name); }
        catch (EncoderFallbackException) { return MacroAssignmentNameProblem.InvalidUnicode; }
        return MacroAssignmentNameProblem.LinkOrCredential;
    }
    public override string ToString() => $"MacroAssignment {{ Target = {Target?.Kind}:{Target?.Id}, MacroId = {MacroId}, Execution = unavailable }}";
}

public sealed record MacroAssignmentStamp(Guid Generation, long Revision)
{
    public static MacroAssignmentStamp Empty { get; } = new(Guid.Empty, 0);
    public MacroAssignmentStamp Validated()
    {
        if (Revision < 0 || (Generation == Guid.Empty) != (Revision == 0)) throw new ArgumentException("The assignment store generation is invalid.");
        return this;
    }
}

public sealed record MacroAssignmentQuery(int Offset = 0, int Limit = 100, MacroAssignmentStamp? Expected = null)
{
    public MacroAssignmentQuery Validated()
    {
        if (Offset is < 0 or > MacroAssignment.MaximumCount || Limit is < 1 or > MacroAssignment.MaximumPageSize || Offset > 0 && Expected is null)
            throw new ArgumentException("Use a bounded assignment page and an exact store stamp for later pages.");
        Expected?.Validated(); return this;
    }
}

public sealed record MacroAssignmentPage(MacroAssignmentStamp Stamp, IReadOnlyList<MacroAssignment> Items, int Total, int? NextOffset)
{
    public MacroAssignmentPage Validated(MacroAssignmentQuery query)
    {
        ArgumentNullException.ThrowIfNull(query); query.Validated();
        if (Stamp is null || Items is null) throw new ArgumentException("The assignment page is invalid.");
        Stamp.Validated();
        if (query.Expected is { } expected && expected != Stamp) throw new MacroAssignmentConflictException();
        if (Total is < 0 or > MacroAssignment.MaximumCount || query.Offset > Total || Items.Count > MacroAssignment.MaximumPageSize
            || Items.Count != Math.Min(query.Limit, Total - query.Offset)
            || NextOffset != (query.Offset + Items.Count < Total ? query.Offset + Items.Count : (int?)null)
            || Stamp == MacroAssignmentStamp.Empty && Total != 0) throw new ArgumentException("The assignment page has an invalid bounded shape.");
        var owned = new MacroAssignment[Items.Count]; var targets = new HashSet<MacroAssignmentTarget>();
        for (int index = 0; index < owned.Length; index++)
        {
            var item = Items[index] ?? throw new ArgumentException("The assignment page has an invalid entry.");
            item.ValidatedSaved(); if (!targets.Add(item.Target)) throw new ArgumentException("The assignment page contains a duplicate target.");
            owned[index] = item;
        }
        return this with { Items = Array.AsReadOnly(owned) };
    }
}

public enum MacroAssignmentWriteState { Applied, Unchanged }
public sealed record MacroAssignmentWriteResult(MacroAssignmentWriteState State, MacroAssignmentStamp Stamp);
public interface IMacroAssignmentStore
{
    Task<MacroAssignmentPage> ReadAsync(MacroAssignmentQuery query, CancellationToken ct);
    Task<MacroAssignmentWriteResult> SaveAsync(MacroAssignment assignment, MacroAssignmentStamp expectedStamp, CancellationToken ct);
    Task<MacroAssignmentWriteResult> RemoveAsync(MacroAssignmentTarget target, MacroAssignmentStamp expectedStamp, CancellationToken ct);
}
public sealed class MacroAssignmentConflictException() : IOException("Macro assignments changed elsewhere. Refresh assignments before reviewing this change again.");
public sealed class MacroAssignmentBusyException() : IOException("Assignment storage is busy. Wait for its owned operations to finish, then retry.");
public sealed class MacroAssignmentCapacityException() : IOException("Assignment storage reached its operational budget of 10,000 references or 16 MiB per database file. Saved profiles are not limited.");
public enum MacroAssignmentStorageProblem { Damaged, UnsupportedVersion, UnsafePath, OutcomeUnknown }
public sealed class MacroAssignmentStorageException(MacroAssignmentStorageProblem problem) : IOException(problem switch
{
    MacroAssignmentStorageProblem.UnsupportedVersion => "Assignment storage has an unsupported version. Existing files were preserved; use a compatible Zenith Sessions version.",
    MacroAssignmentStorageProblem.UnsafePath => "Assignment storage cannot use redirected or unsafe files or directories. Existing files were preserved.",
    MacroAssignmentStorageProblem.OutcomeUnknown => "The assignment commit outcome could not be confirmed. Persistence may have completed; refresh before retrying. Existing files were preserved.",
    _ => "Assignment storage is damaged, foreign or unreadable. Existing files were preserved; review this separate metadata database."
})
{
    public MacroAssignmentStorageProblem Problem { get; } = problem;
}
