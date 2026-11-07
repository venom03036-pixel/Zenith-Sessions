using System.ComponentModel;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private IRecipientBundleStore _recipientBundleStore=null!;
    public RecipientBundlesViewModel RecipientBundles {get;private set;}=null!;
    private void InitializeRecipientBundles(IRecipientBundleStore? store)
    {
        _recipientBundleStore=store??new RecipientBundleStore(DataRoot);
        var reader=new BundleRecipientReader(_accounts,_vault);
        RecipientBundles=new(_recipientBundleStore,()=>Accounts.Where(row=>row.IsChecked).Select(row=>row.Profile.Id).ToArray(),
            reader.ReadAsync,()=>ServerBrowser.ReadyDestination,async (destination,members,ct)=>
            {
                if(!CanUseDirectories()||!QueueReady)return "The launch queue is unavailable. No request was made.";
                long epoch=_directoryGeneration;
                var snapshot=members.Select(member=>(Profile:member.Profile!,member.Binding)).ToArray();
                return await QueueFrozenRecipientsAsync(destination,snapshot,epoch,ct);
            },CanUseDirectories,error=>_diagnostics.Record(ManagerEventKind.OperationFailed,error.GetType()));
        ServerBrowser.PropertyChanged+=BundleDestinationChanged;
    }
    private void BundleDestinationChanged(object? sender,PropertyChangedEventArgs args)
    {if(args.PropertyName==nameof(ServerBrowserViewModel.CanUseDestination))RecipientBundles.RefreshAvailability();}
    private async Task DisposeRecipientBundlesAsync()
    {
        ServerBrowser.PropertyChanged-=BundleDestinationChanged;
        await RecipientBundles.DisposeAsync();
        await Task.WhenAll(RecipientBundles.Refresh.Execution,RecipientBundles.SaveChecked.Execution,RecipientBundles.ReplaceRecipients.Execution,RecipientBundles.AddChecked.Execution,RecipientBundles.RemoveMissing.Execution,
            RecipientBundles.Rename.Execution,RecipientBundles.Delete.Execution,RecipientBundles.Review.Execution,RecipientBundles.Queue.Execution);
        (_recipientBundleStore as IDisposable)?.Dispose();
    }
}
