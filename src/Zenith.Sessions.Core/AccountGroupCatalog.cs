namespace Zenith.Sessions.Core;

/// <summary>Independent nonsecret library presentation. Group identity never grants account or launch authority.</summary>
public sealed record AccountGroupDefinition(Guid Id,string Name,string? ColorHex,long SortOrder,long Revision,DateTimeOffset UpdatedUtc)
{
    public AccountGroupDefinition Validated()
    {
        if(Id==Guid.Empty||SortOrder<0||Revision<1||UpdatedUtc==default)
            throw new ArgumentException("A saved group requires its stable identity, order, revision and update time.");
        return this with{Name=AccountGroups.ValidateName(Name),ColorHex=ValidateColor(ColorHex)};
    }
    public static string? ValidateColor(string? value)
    {
        if(value is null)return null;
        if(value.Length>32)throw new ArgumentException("Choose the theme color or enter a group color as #RRGGBB.");
        string color=value.Trim();
        if(color.Length!=7||color[0]!='#')
            throw new ArgumentException("Choose the theme color or enter a group color as #RRGGBB.");
        for(int index=1;index<color.Length;index++)
            if(color[index] is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
                throw new ArgumentException("Choose the theme color or enter a group color as #RRGGBB.");
        return color.ToUpperInvariant();
    }
}

public enum AccountGroupMoveDirection {Up,Down}
public sealed record AccountGroupCatalogQuery(string Search="",int Page=0,int PageSize=100)
{
    public AccountGroupCatalogQuery Validated()
    {
        if(Search is null||Search.Length>AccountGroups.MaximumNameLength||Search.Any(char.IsControl)||Page is <0 or >10000000||PageSize is <1 or >1000)
            throw new ArgumentException("Choose a bounded group page and search.");
        string search=Search.Trim();
        if(search.Length>0)search=AccountGroups.ValidateName(search);
        return this with{Search=search};
    }
}
public sealed record AccountGroupCatalogPage(IReadOnlyList<AccountGroupDefinition> Items,int Total,int Page,int PageSize,long CatalogRevision);

public interface IAccountGroupCatalog
{
    Task<AccountGroupCatalogPage> ReadGroupCatalogAsync(AccountGroupCatalogQuery query,CancellationToken ct);
    Task<AccountGroupDefinition?> FindGroupAsync(string name,CancellationToken ct);
    Task<AccountGroupDefinition?> FindGroupAsync(Guid id,CancellationToken ct);
    Task<AccountGroupDefinition> CreateGroupAsync(string name,string? colorHex,long expectedCatalogRevision,CancellationToken ct);
    Task<AccountGroupDefinition> SetGroupColorAsync(AccountGroupDefinition expected,string? colorHex,long expectedCatalogRevision,CancellationToken ct);
    // Resolves the actual adjacent definition inside the transaction, including off-page neighbors.
    // A boundary no-op retains both row and catalog revisions. Ordering affects group lists only.
    Task<AccountGroupDefinition> MoveGroupAsync(AccountGroupDefinition expected,AccountGroupMoveDirection direction,long expectedCatalogRevision,CancellationToken ct);
}
public sealed class AccountGroupCatalogConflictException() : Exception("This group or its ordering changed elsewhere. Reload the groups before applying the change.");
