using System.Collections.ObjectModel;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

/// <summary>One owned metadata/review/request operation. The queue callback retains native execution guards.</summary>
public sealed class RecipientBundlesViewModel : Observable,IAsyncDisposable
{
    private readonly IRecipientBundleStore _store;
    private readonly Func<IReadOnlyList<Guid>> _selection;
    private readonly Func<IReadOnlyList<Guid>,CancellationToken,Task<IReadOnlyList<BundleMemberReview>>> _readMembers;
    private readonly Func<RobloxRequestedDestination?> _destination;
    private readonly Func<RobloxRequestedDestination,IReadOnlyList<BundleMemberReview>,CancellationToken,Task<string>> _queue;
    private readonly Func<bool> _canUse;
    private readonly Action<Exception> _failure;
    private readonly CancellationTokenSource _lifetime=new();
    private CancellationTokenSource? _operationStop;
    private Task _active=Task.CompletedTask;
    private Task? _dispose;
    private long _generation,_selectionEpoch;
    private bool _busy,_available;
    private string _name="",_status="Save a named set of checked accounts, then reuse it with a game or server link.";
    private RecipientBundle? _selected;
    public ObservableCollection<RecipientBundle> Bundles {get;}=[];
    public ObservableCollection<BundleMemberReview> Members {get;}=[];
    public RecipientBundle? SelectedBundle
    {
        get=>_selected;
        set {if(Set(ref _selected,value)){_selectionEpoch++;NameInput=value?.Name??"";Members.Clear();RefreshAvailability();}}
    }
    public string NameInput
    {
        get=>_name;
        set {if(value is null || value.Length>80){Status="Bundle names can contain up to 80 characters.";return;}Set(ref _name,value);}
    }
    public string Status {get=>_status;private set=>Set(ref _status,value);}
    public bool IsBusy {get=>_busy;private set{if(Set(ref _busy,value))RefreshAvailability();}}
    public bool CanManage => CanStart()&&!IsBusy;
    public bool CanUseSelected => CanManage&&_available&&SelectedBundle is { } selected&&Bundles.Contains(selected);
    public bool CanQueueSelected => CanUseSelected&&_destination() is not null;
    public string SelectedSummary => SelectedBundle is { } selected?$"{selected.ProfileIds.Count} saved recipient references · live sign-in is checked before dispatch":"Select a saved bundle.";
    public Task ActiveOperation=>_active;
    public AsyncCommand Refresh {get;}
    public AsyncCommand SaveChecked {get;}
    public AsyncCommand ReplaceRecipients {get;}
    public AsyncCommand AddChecked {get;}
    public AsyncCommand RemoveMissing {get;}
    public AsyncCommand Rename {get;}
    public AsyncCommand Delete {get;}
    public AsyncCommand Review {get;}
    public AsyncCommand Queue {get;}
    public Command Cancel {get;}

    public RecipientBundlesViewModel(IRecipientBundleStore store,Func<IReadOnlyList<Guid>> selection,
        Func<IReadOnlyList<Guid>,CancellationToken,Task<IReadOnlyList<BundleMemberReview>>> readMembers,
        Func<RobloxRequestedDestination?> destination,
        Func<RobloxRequestedDestination,IReadOnlyList<BundleMemberReview>,CancellationToken,Task<string>> queue,
        Func<bool> canUse,Action<Exception>? failure=null)
    {
        _store=store;_selection=selection;_readMembers=readMembers;_destination=destination;_queue=queue;_canUse=canUse;_failure=failure??(_=>{});
        Refresh=new(_=>LoadAsync(),ReportFailure);SaveChecked=new(_=>SaveCheckedAsync(NameInput),ReportFailure);
        ReplaceRecipients=new(_=>ReplaceSelectedRecipientsAsync(),ReportFailure);
        AddChecked=new(_=>AddCheckedRecipientsAsync(),ReportFailure);RemoveMissing=new(_=>RemoveMissingRecipientsAsync(),ReportFailure);
        Rename=new(_=>RenameSelectedAsync(NameInput),ReportFailure);Delete=new(_=>DeleteSelectedAsync(),ReportFailure);
        Review=new(_=>ReviewSelectedAsync(),ReportFailure);Queue=new(_=>QueueSelectedAsync(),ReportFailure);
        Cancel=new(_=>CancelWork(false));
    }

    public Task LoadAsync(CancellationToken ct=default)
    {
        Guid? selected=SelectedBundle?.Id;long selectionEpoch=_selectionEpoch;
        return StartAsync(async token=>
        {
            var snapshot=await _store.LoadAsync(token);
            return ()=>PublishSnapshot(snapshot,selectionEpoch==_selectionEpoch?selected:SelectedBundle?.Id,"Bundles loaded. Select one to review its current members.");
        },ct);
    }
    public Task SaveCheckedAsync(string name,CancellationToken ct=default)
    {
        if(!CanManage)return Task.CompletedTask;
        RecipientBundle draft;
        try{draft=new RecipientBundle(Guid.NewGuid(),name,_selection()).Validated();}
        catch(ArgumentException){Status="Check at least one account on the current page and use a readable bundle name of 1–80 characters. Links and credentials do not belong in names.";return Task.CompletedTask;}
        long selectionEpoch=_selectionEpoch;
        return StartAsync(async token=>
        {
            var saved=await _store.SaveAsync(draft,token);var snapshot=await _store.LoadAsync(token);
            return ()=>PublishSnapshot(snapshot,selectionEpoch==_selectionEpoch?saved.Id:SelectedBundle?.Id,"Bundle saved. Its recipients are references to saved profiles; no launch was requested.");
        },ct);
    }
    public Task ReplaceSelectedRecipientsAsync(CancellationToken ct=default)
    {
        if(!CanUseSelected||SelectedBundle is not{ } selected)return Task.CompletedTask;
        RecipientBundle draft;
        try{draft=(selected with{ProfileIds=_selection()}).Validated();}
        catch(ArgumentException){Status="Check at least one account on the current page before replacing this bundle's recipients.";return Task.CompletedTask;}
        return StartAsync(async token=>
        {
            var saved=await _store.SaveAsync(draft,token);var snapshot=await _store.LoadAsync(token);
            return ()=>PublishSnapshot(snapshot,SelectedBundle?.Id??saved.Id,"Bundle recipients replaced explicitly. Existing queued or running work keeps its original recipients.");
        },ct);
    }
    public Task AddCheckedRecipientsAsync(CancellationToken ct=default)
    {
        if(!CanUseSelected||SelectedBundle is not{ } selected)return Task.CompletedTask;
        var checkedIds=_selection().ToArray();var known=selected.ProfileIds.ToHashSet();
        var added=checkedIds.Where(id=>!known.Contains(id)).ToArray();
        if(added.Length==0){Status="The checked accounts are already in this bundle, or no accounts are checked.";return Task.CompletedTask;}
        RecipientBundle draft;
        try{draft=(selected with{ProfileIds=selected.ProfileIds.Concat(added).ToArray()}).Validated();}
        catch(ArgumentException){Status="This bundle can contain up to 1,000 distinct saved recipients. Existing recipients were preserved.";return Task.CompletedTask;}
        return StartAsync(async token=>
        {
            var saved=await _store.SaveAsync(draft,token);var snapshot=await _store.LoadAsync(token);
            return ()=>PublishSnapshot(snapshot,SelectedBundle?.Id??saved.Id,"Checked accounts added to the bundle. Earlier recipients and their order were retained.");
        },ct);
    }
    public Task RemoveMissingRecipientsAsync(CancellationToken ct=default)
    {
        if(!CanUseSelected||SelectedBundle is not{ } selected)return Task.CompletedTask;
        Guid[] ids=selected.ProfileIds.ToArray();long selectionEpoch=_selectionEpoch;
        return StartAsync(async token=>
        {
            var members=ValidateMembers(ids,await _readMembers(ids,token));token.ThrowIfCancellationRequested();
            var remaining=members.Where(member=>!member.IsMissing).Select(member=>member.ProfileId).ToArray();
            if(remaining.Length==ids.Length)return ()=>{if(selectionEpoch==_selectionEpoch)PublishMembers(members);Status="All bundle profiles still exist. Nothing was removed.";};
            if(remaining.Length==0)return ()=>{if(selectionEpoch==_selectionEpoch)PublishMembers(members);Status="Every profile in this bundle is missing. Remove the bundle explicitly, or replace its recipients with checked accounts.";};
            var saved=await _store.SaveAsync((selected with{ProfileIds=remaining}).Validated(),token);var snapshot=await _store.LoadAsync(token);
            return ()=>PublishSnapshot(snapshot,SelectedBundle?.Id??saved.Id,"Missing profile references removed explicitly after a fresh lookup. Saved accounts and existing requests were not removed.");
        },ct);
    }
    public Task RenameSelectedAsync(string name,CancellationToken ct=default)
    {
        if(!CanUseSelected||SelectedBundle is not{ } selected)return Task.CompletedTask;
        RecipientBundle draft;
        try{draft=(selected with{Name=RecipientBundle.ValidateName(name)}).Validated();}catch(ArgumentException){Status="Use a readable bundle name of 1–80 characters, without links or credentials.";return Task.CompletedTask;}
        return StartAsync(async token=>
        {
            var saved=await _store.RenameAsync(selected.Id,selected.Revision,selected.ChangeId,draft.Name,token);
            var snapshot=await _store.LoadAsync(token);
            return ()=>PublishSnapshot(snapshot,SelectedBundle?.Id??saved.Id,"Bundle renamed. Recipient references are unchanged.");
        },ct);
    }
    public Task DeleteSelectedAsync(CancellationToken ct=default)
    {
        if(!CanUseSelected||SelectedBundle is not{ } selected)return Task.CompletedTask;
        return StartAsync(async token=>
        {
            await _store.DeleteAsync(selected.Id,selected.Revision,selected.ChangeId,token);var snapshot=await _store.LoadAsync(token);
            return ()=>PublishSnapshot(snapshot,SelectedBundle?.Id==selected.Id?null:SelectedBundle?.Id,"Bundle removed. Saved accounts, queued requests and running clients were not removed.");
        },ct);
    }
    public Task ReviewSelectedAsync(CancellationToken ct=default)
    {
        if(!CanUseSelected||SelectedBundle is not{ } selected)return Task.CompletedTask;
        var ids=selected.ProfileIds.ToArray();long selectionEpoch=_selectionEpoch;
        return StartAsync(async token=>
        {
            var members=ValidateMembers(ids,await _readMembers(ids,token));
            return ()=>
            {
                if(selectionEpoch!=_selectionEpoch)return;
                PublishMembers(members);Status=$"{selected.Name}: {members.Count(m=>!m.IsMissing)} saved profiles, {members.Count(m=>m.IsMissing)} missing. Missing references are retained; names and IDs are last-known metadata.";
            };
        },ct);
    }
    public Task QueueSelectedAsync(CancellationToken ct=default)
    {
        if(!CanQueueSelected||SelectedBundle is not{ } selected||_destination() is not{ } destination)return Task.CompletedTask;
        Guid[] ids=selected.ProfileIds.ToArray();long selectionEpoch=_selectionEpoch;
        return StartAsync(async token=>
        {
            var members=ValidateMembers(ids,await _readMembers(ids,token));token.ThrowIfCancellationRequested();
            if(members.Any(m=>m.IsMissing))return ()=>
            {
                if(selectionEpoch==_selectionEpoch)PublishMembers(members);
                Status="This bundle contains missing profiles. No launch request was made. Review its members and save a replacement bundle explicitly.";
            };
            string message=await _queue(destination,members,token);
            return ()=>{if(selectionEpoch==_selectionEpoch)PublishMembers(members);Status=selected.Name+" · "+message;};
        },ct);
    }

    private void PublishSnapshot(RecipientBundleSnapshot snapshot,Guid? selected,string message)
    {
        if(snapshot.Revision<1||snapshot.Bundles.Count>100||snapshot.Bundles.Sum(b=>(long)b.ProfileIds.Count)>10000)throw new InvalidDataException();
        var rows=snapshot.Bundles.Select(b=>b.Validated()).ToArray();
        if(rows.Any(b=>b.Revision<1||b.Revision>snapshot.Revision||b.ChangeId==Guid.Empty)||rows.Select(b=>b.Id).Distinct().Count()!=rows.Length)throw new InvalidDataException();
        Array.Sort(rows,(a,b)=>{int compare=StringComparer.OrdinalIgnoreCase.Compare(a.Name,b.Name);return compare!=0?compare:a.Id.CompareTo(b.Id);});
        Bundles.Clear();foreach(var row in rows)Bundles.Add(row);SelectedBundle=rows.FirstOrDefault(b=>b.Id==selected);
        _available=true;Status=snapshot.Notice is null?message:"Bundles loaded from preserved recovery data. Original files were preserved; review the data folder before changing them.";
        RefreshAvailability();
    }
    private static IReadOnlyList<BundleMemberReview> ValidateMembers(Guid[] requested,IReadOnlyList<BundleMemberReview> members)
    {
        if(members.Count!=requested.Length)throw new InvalidDataException();
        for(int i=0;i<requested.Length;i++)
            if(members[i].ProfileId!=requested[i]||members[i].Profile is{ } profile&&profile.Id!=requested[i]
                ||members[i].IsMissing&&members[i].Binding is not null)throw new InvalidDataException();
        return Array.AsReadOnly(members.ToArray());
    }
    private void PublishMembers(IReadOnlyList<BundleMemberReview> members){Members.Clear();foreach(var row in members)Members.Add(row);}
    public void RefreshAvailability(){Raise(nameof(CanManage));Raise(nameof(CanUseSelected));Raise(nameof(CanQueueSelected));Raise(nameof(SelectedSummary));}
    private bool CanStart()=>_dispose is null&&!_lifetime.IsCancellationRequested&&_canUse();
    private Task StartAsync(Func<CancellationToken,Task<Action>> work,CancellationToken ct)
    {
        if(!CanManage)return Task.CompletedTask;ct.ThrowIfCancellationRequested();
        var owner=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _active=owner.Task;long generation=_generation;
        var stop=CancellationTokenSource.CreateLinkedTokenSource(ct,_lifetime.Token);_operationStop=stop;
        IsBusy=true;Status="Working with the saved recipient bundle…";
        _=RunOwnedAsync(work,generation,stop,owner);return owner.Task;
    }
    private async Task RunOwnedAsync(Func<CancellationToken,Task<Action>> work,long generation,CancellationTokenSource stop,TaskCompletionSource owner)
    {
        try
        {
            var publish=await work(stop.Token);
            if(!stop.IsCancellationRequested&&generation==_generation&&CanStart())publish();
            else if(generation==_generation&&CanStart())Status="Bundle work cancelled. A completed metadata save may already be durable; refresh before continuing.";
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested)
        {if(generation==_generation&&CanStart())Status="Bundle work cancelled. A completed save or request may already exist; review before retrying.";}
        catch(Exception error)
        {if(generation==_generation&&!stop.IsCancellationRequested&&CanStart())ReportFailure(error);else _failure(error);}
        finally
        {
            try{if(ReferenceEquals(_operationStop,stop))_operationStop=null;stop.Dispose();IsBusy=false;}
            finally{owner.TrySetResult();}
        }
    }
    private void ReportFailure(Exception error)
    {
        _available=false;Status="Bundle work could not complete. Existing files and last-known rows were preserved. Refresh and review before retrying.";
        _failure(error);RefreshAvailability();
    }
    public void CancelWork(bool clear=true)
    {
        _generation++;_operationStop?.Cancel();
        if(clear){Bundles.Clear();Members.Clear();SelectedBundle=null;NameInput="";_available=false;}
        Status="Bundle work stopped. Completed saves or requests may already exist; review before retrying.";RefreshAvailability();
    }
    public ValueTask DisposeAsync()=>new(_dispose??=StopAsync());
    private async Task StopAsync(){_lifetime.Cancel();CancelWork();await _active;_lifetime.Dispose();}
}
