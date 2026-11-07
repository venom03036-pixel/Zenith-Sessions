namespace Zenith.MacroHost.Contracts;

/// <summary>Metadata-only protocol identity. There is no execution operation or capability in version 1.</summary>
public static class MacroHostProtocol
{
    public const int Version = 1;
    public const string Product = "Zenith.Macros.Personal";
    public const string PipeName = "Zenith.Macros.Personal.Catalog.v1";
    public const int MaximumFrameBytes = 256 * 1024;
}

public static class MacroCatalogLimits
{
    public const int MaximumItems = 10_000;
    public const int MaximumPageSize = 100;
    public const int MaximumNameLength = 200;
    public const int MaximumHotkeyLength = 80;
    public const int MaximumHostVersionLength = 64;
    public const int MaximumTopLevelActions = 100_000;
}

public enum MacroCatalogState { Ready, Starting, Changed, Unavailable, Incompatible, Disconnected, Busy }

public sealed record MacroCatalogQuery(int Offset = 0, int Limit = 100, Guid? HostSessionId = null, long? CatalogRevision = null)
{
    public MacroCatalogQuery Validated()
    {
        if (Offset is < 0 or > MacroCatalogLimits.MaximumItems || Limit is < 1 or > MacroCatalogLimits.MaximumPageSize
            || HostSessionId == Guid.Empty || CatalogRevision is <= 0 || HostSessionId.HasValue != CatalogRevision.HasValue
            || Offset != 0 && !HostSessionId.HasValue)
            throw new ArgumentException("The saved-macro catalog query has invalid bounds or an incomplete generation cursor.");
        return this;
    }
}

public sealed record SavedMacroDescriptor(Guid Id, Guid SavedRevision, string Name, string Hotkey,
    int TopLevelActions, bool Enabled, string TargetMode)
{
    public SavedMacroDescriptor Validated()
    {
        if (Id == Guid.Empty || SavedRevision == Guid.Empty
            || !CatalogText.Valid(Name, MacroCatalogLimits.MaximumNameLength, allowEmpty: false)
            || !CatalogText.Valid(Hotkey, MacroCatalogLimits.MaximumHotkeyLength, allowEmpty: true)
            || TopLevelActions is < 0 or > MacroCatalogLimits.MaximumTopLevelActions
            || TargetMode is not ("Legacy" or "Screen" or "ActiveExternalWindow" or "Application" or "SpecificWindow"))
            throw new ArgumentException("The saved-macro descriptor contains invalid or oversized display metadata.");
        return this;
    }
}

public sealed record MacroCatalogPage(MacroCatalogState State, Guid HostSessionId, long CatalogRevision,
    string HostVersion, IReadOnlyList<SavedMacroDescriptor> Items, int Total, int? NextOffset, string Explanation)
{
    public static MacroCatalogPage Failure(MacroCatalogState state)
    {
        if (!Enum.IsDefined(state) || state == MacroCatalogState.Ready) throw new ArgumentOutOfRangeException(nameof(state));
        return new(state, Guid.Empty, 0, "", Array.Empty<SavedMacroDescriptor>(), 0, null, MacroCatalogMessages.For(state));
    }

    /// <summary>Validate intrinsic metadata bounds and return an immutable, privately owned item collection.</summary>
    public MacroCatalogPage Validated()
    {
        bool knownHost = HostSessionId != Guid.Empty && CatalogRevision > 0;
        bool unknownHost = HostSessionId == Guid.Empty && CatalogRevision == 0;
        if (!Enum.IsDefined(State) || (!knownHost && !unknownHost) || Items is null
            || Items.Count > MacroCatalogLimits.MaximumPageSize || Total is < 0 or > MacroCatalogLimits.MaximumItems
            || !CatalogText.Valid(HostVersion, MacroCatalogLimits.MaximumHostVersionLength, allowEmpty: !knownHost)
            || Explanation != MacroCatalogMessages.For(State))
            throw InvalidPage();
        if (State != MacroCatalogState.Ready)
        {
            if (Items.Count != 0 || Total != 0 || NextOffset is not null) throw InvalidPage();
            return this with { Items = Array.Empty<SavedMacroDescriptor>() };
        }
        if (!knownHost || Items.Count > Total || Items.Count == 0 && NextOffset is not null
            || NextOffset is { } next && (next <= 0 || next >= Total || next < Items.Count || Items.Count == 0)) throw InvalidPage();
        var owned = new SavedMacroDescriptor[Items.Count]; var ids = new HashSet<Guid>();
        for (int index = 0; index < owned.Length; index++)
        {
            var item = Items[index];
            try { item?.Validated(); }
            catch (ArgumentException) { throw InvalidPage(); }
            if (item is null || !ids.Add(item.Id)) throw InvalidPage();
            owned[index] = item;
        }
        return this with { Items = Array.AsReadOnly(owned) };
    }

    /// <summary>Also bind a ready page to the exact requested offset, limit and optional host generation.</summary>
    public MacroCatalogPage Validated(MacroCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query); query.Validated(); var page = Validated();
        if (page.State != MacroCatalogState.Ready) return page;
        if (query.Offset > page.Total || page.Items.Count != Math.Min(query.Limit, page.Total - query.Offset)
            || page.NextOffset != (query.Offset + page.Items.Count < page.Total ? query.Offset + page.Items.Count : (int?)null)
            || query.HostSessionId is { } session && session != page.HostSessionId
            || query.CatalogRevision is { } revision && revision != page.CatalogRevision) throw InvalidPage();
        return page;
    }

    private static InvalidDataException InvalidPage() => new("The saved-macro catalog page has an invalid metadata shape or generation.");
}

public static class MacroCatalogMessages
{
    public static string For(MacroCatalogState state) => state switch
    {
        MacroCatalogState.Ready => "Saved macro metadata is available. This link cannot execute macros or verify an account.",
        MacroCatalogState.Starting => "The Personal host is loading its saved macro metadata.",
        MacroCatalogState.Changed => "The saved macro catalog changed. Refresh from the first page.",
        MacroCatalogState.Unavailable => "The saved macro library could not be fully confirmed. Reload it in the Personal host.",
        MacroCatalogState.Incompatible => "The Personal host catalog protocol or metadata is incompatible.",
        MacroCatalogState.Disconnected => "The Personal macro host is not connected. Open it explicitly, then refresh.",
        MacroCatalogState.Busy => "The Personal host catalog is busy. Refresh again after its current request finishes.",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };
}

public interface IMacroCatalogSource
{
    Task<MacroCatalogPage> GetPageAsync(MacroCatalogQuery query, CancellationToken ct = default);
}

public interface IZenithMacroCatalogClient
{
    Task<MacroCatalogPage> GetPageAsync(MacroCatalogQuery query, CancellationToken ct = default);
}

internal static class CatalogText
{
    internal static bool Valid(string? value, int maximum, bool allowEmpty)
    {
        if (value is null || value.Length > maximum || !allowEmpty && string.IsNullOrWhiteSpace(value)) return false;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsControl(character) || char.IsLowSurrogate(character)) return false;
            if (char.IsHighSurrogate(character) && (++index >= value.Length || !char.IsLowSurrogate(value[index]))) return false;
        }
        return true;
    }
}
