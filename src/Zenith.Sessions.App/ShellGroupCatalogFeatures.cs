using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel : IAccountGroupCatalog
{
    private IReadOnlyList<AccountGroupDefinition> _groupCatalogChoices=Array.Empty<AccountGroupDefinition>();
    private string _groupChoicesNotice="";
    public bool HasGroupCatalog=>_accounts is IAccountGroupCatalog;
    public IReadOnlyList<AccountGroupDefinition> GroupCatalogChoices=>_groupCatalogChoices;
    public string GroupChoicesNotice {get=>_groupChoicesNotice;private set=>Set(ref _groupChoicesNotice,value);}
    private IAccountGroupCatalog GroupCatalog=>_accounts as IAccountGroupCatalog
        ??throw new InvalidOperationException("This account library does not support independent saved groups.");

    public Task ShowGroupAccountsAsync(Guid groupId,CancellationToken ct)
    {
        if(groupId==Guid.Empty)throw new ArgumentException("Choose a saved group.");
        int queryGeneration=_queryGeneration;
        return RunOwnedGroupChangeAsync(async token=>
        {
            var group=await Task.Run(()=>GroupCatalog.FindGroupAsync(groupId,token),token)
                ??throw new InvalidOperationException("This saved group was removed. Reload the groups before choosing it.");
            group=group.Validated();
            if(group.Id!=groupId)throw new InvalidDataException("The selected group response is inconsistent. Reload the groups.");
            token.ThrowIfCancellationRequested();
            if(queryGeneration!=_queryGeneration)throw new OperationCanceledException("The account filter changed while this group was loading.");
            var priorGroup=_group;string priorSearch=_search;int priorPage=_page;
            _queryCancellation?.Cancel();_page=0;
            _search="";Raise(nameof(Search));
            _updatingGroups=true;
            try{_group=new(group.Name,group.Name,group.ColorHex);Raise(nameof(Group));}
            finally{_updatingGroups=false;}
            int targetGeneration=++_queryGeneration;
            try{await LoadAccountsAsync(token,targetGeneration,group);}
            catch
            {
                if(targetGeneration==_queryGeneration&&!token.IsCancellationRequested)
                {
                    _page=priorPage;_search=priorSearch;Raise(nameof(Search));_updatingGroups=true;
                    try{_group=priorGroup;Raise(nameof(Group));}finally{_updatingGroups=false;}
                }
                throw;
            }
            token.ThrowIfCancellationRequested();
            if(targetGeneration!=_queryGeneration)throw new OperationCanceledException("A newer account filter replaced this group view.");
            Tab="Accounts";Notice="Showing accounts in “"+group.Name+"”. Empty groups contain no accounts yet.";
            return true;
        },ct);
    }

    public Task<AccountGroupCatalogPage> ReadGroupCatalogAsync(AccountGroupCatalogQuery query,CancellationToken ct)
        =>RunOwnedGroupChangeAsync(token=>Task.Run(()=>GroupCatalog.ReadGroupCatalogAsync(query.Validated(),token),token),ct);
    public Task<AccountGroupDefinition?> FindGroupAsync(string name,CancellationToken ct)
        =>RunOwnedGroupChangeAsync(token=>Task.Run(()=>GroupCatalog.FindGroupAsync(AccountGroups.ValidateName(name),token),token),ct);
    public Task<AccountGroupDefinition?> FindGroupAsync(Guid id,CancellationToken ct)
        =>RunOwnedGroupChangeAsync(token=>Task.Run(()=>GroupCatalog.FindGroupAsync(id,token),token),ct);
    public Task<AccountGroupDefinition> CreateGroupAsync(string name,string? colorHex,long expectedCatalogRevision,CancellationToken ct)
    {
        name=AccountGroups.ValidateName(name);colorHex=AccountGroupDefinition.ValidateColor(colorHex);
        return MutateCatalogAsync(token=>GroupCatalog.CreateGroupAsync(name,colorHex,expectedCatalogRevision,token),
            _=>"Saved the group. It can stay empty until you add accounts.",ct);
    }
    public Task<AccountGroupDefinition> SetGroupColorAsync(AccountGroupDefinition expected,string? colorHex,long expectedCatalogRevision,CancellationToken ct)
    {
        expected=expected.Validated();colorHex=AccountGroupDefinition.ValidateColor(colorHex);
        return MutateCatalogAsync(token=>GroupCatalog.SetGroupColorAsync(expected,colorHex,expectedCatalogRevision,token),
            result=>result==expected?"The group color is unchanged.":"Saved the group color. Account memberships and launch settings are unchanged.",ct);
    }
    public Task<AccountGroupDefinition> MoveGroupAsync(AccountGroupDefinition expected,AccountGroupMoveDirection direction,long expectedCatalogRevision,CancellationToken ct)
    {
        expected=expected.Validated();
        return MutateCatalogAsync(token=>GroupCatalog.MoveGroupAsync(expected,direction,expectedCatalogRevision,token),
            result=>result==expected?"The group is already at that end of the list.":"Saved the group order. Account priority and launch order are unchanged.",ct);
    }
    private Task<AccountGroupDefinition> MutateCatalogAsync(Func<CancellationToken,Task<AccountGroupDefinition>> mutation,
        Func<AccountGroupDefinition,string> message,CancellationToken ct)
    {
        long epoch=Interlocked.Read(ref _groupChangeEpoch);
        return RunOwnedGroupChangeAsync(async token=>
        {
            var saved=await Task.Run(()=>mutation(token),token);
            // The transaction remains committed if Panic/cancellation occurs during refresh.
            // Never compensate by deleting the definition or altering account memberships.
            await LoadAccountsAsync(token);
            if(!IsLocked&&!_shuttingDown&&epoch==Interlocked.Read(ref _groupChangeEpoch))
            {Notice=message(saved);AddEvent("Saved group presentation metadata.");_diagnostics.Record(ManagerEventKind.ProfileSaved);}
            return saved;
        },ct);
    }

    private sealed record GroupPickerSnapshot(IReadOnlyList<GroupFilter> Filters,IReadOnlyList<AccountGroupDefinition> Definitions,string Notice);
    private async Task<GroupPickerSnapshot> ReadGroupPickerAsync(string? selectedName,CancellationToken ct)
    {
        if(_accounts is not IAccountGroupCatalog catalog)
        {
            var names=await _accounts.ReadGroupsAsync(ct).ConfigureAwait(false);
            return new(Array.AsReadOnly(names.Take(1000).Select(name=>new GroupFilter(name,name)).ToArray()),
                Array.Empty<AccountGroupDefinition>(),"");
        }
        var page=await catalog.ReadGroupCatalogAsync(new(PageSize:1000),ct).ConfigureAwait(false);
        if(page.Items is null||page.Items.Count is <0 or >1000||page.Total<page.Items.Count||page.Page!=0||page.PageSize!=1000||page.CatalogRevision<1)
            throw new InvalidDataException("The saved group page is invalid. Existing metadata was preserved.");
        var definitions=new List<AccountGroupDefinition>(page.Items.Count+1);var ids=new HashSet<Guid>();var namesSeen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for(int index=0;index<page.Items.Count;index++)
        {
            ct.ThrowIfCancellationRequested();var definition=page.Items[index].Validated();
            if(!ids.Add(definition.Id)||!namesSeen.Add(definition.Name))throw new InvalidDataException("The saved group page contains duplicate identities.");
            definitions.Add(definition);
        }
        // A bounded suggestion page must not silently reset an explicitly selected group.
        // Pin its exact definition outside the page while keeping the total UI cache bounded.
        if(selectedName is not null&&!namesSeen.Contains(selectedName))
        {
            var selected=await catalog.FindGroupAsync(selectedName,ct).ConfigureAwait(false);
            if(selected is not null)
            {
                selected=selected.Validated();
                if(!string.Equals(selected.Name,selectedName,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The selected group response is inconsistent. Reload the groups.");
                definitions.RemoveAll(group=>group.Id==selected.Id);
                if(definitions.Count==1000)definitions.RemoveAt(definitions.Count-1);
                definitions.Add(selected);
            }
        }
        string notice=page.Total>1000?"Showing up to 1,000 group suggestions; your selected group stays available. Search all groups in Manage groups.":"";
        return new(Array.AsReadOnly(definitions.Select(group=>new GroupFilter(group.Name,group.Name,group.ColorHex)).ToArray()),
            definitions.AsReadOnly(),notice);
    }
}
