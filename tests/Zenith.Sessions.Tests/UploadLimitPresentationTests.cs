using System.Reflection;
using System.Threading.Channels;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// R-instances-18: Sessions > Upload limit. Apply and Remove each run through the control (Windows PowerShell as administrator
// in production, a probe here), never on their own; Panic, Lock and exit stop a waiting operation; the section shows Zenith's
// own record of what it applied in this Windows session, because reading the policy back needs administrator rights.
public sealed partial class ClientPreviewPresentationTests
{
    [Fact]
    public Task ApplyUploadLimitPassesTheTypedRateToTheControlAndShowsTheConfirmedRecord()=>OnUi(async ()=>
    {
        var control=new UploadLimitControlProbe();
        await using var shell=await ReadyUploadLimit(new PriorityDesktopMonitor(1),control);
        Assert.Equal("1000",shell.UploadLimitRate); // A prefilled rate; nothing is applied until the click.
        Assert.True(shell.CanChangeUploadLimit);
        Assert.Equal("No upload limit applied by Zenith in this Windows session. Reading Windows' policy list needs administrator rights, so Zenith shows only its own record.",shell.CurrentUploadLimitText);
        shell.UploadLimitRate="2,500";
        shell.ApplyUploadLimit.Execute(null);await shell.ApplyUploadLimit.Execution;
        var call=await control.NextAsync();
        Assert.Equal(UploadLimitAction.Apply,call.Action);Assert.Equal(2500,call.KilobitsPerSecond);
        Assert.Equal(0,control.RemoveCalls);
        Assert.Equal("Windows confirmed the upload limit: all Roblox clients together may send at most 2,500 kbit/s until Windows restarts or you choose Remove.",shell.UploadLimitStatus);
        Assert.StartsWith("Upload limit applied by Zenith in this Windows session: 2,500 kbit/s for all Roblox clients together, confirmed by Windows at ",shell.CurrentUploadLimitText,StringComparison.Ordinal);
        Assert.EndsWith(" Windows drops it when it restarts; Remove lifts it now. This is Zenith's own record, not a readback: another administrator may have changed it since.",shell.CurrentUploadLimitText);
        Assert.Contains(shell.Events,row=>row.EndsWith("Upload limit of 2,500 kbit/s applied for all Roblox clients together.",StringComparison.Ordinal));
        Assert.Equal("",shell.ErrorNotice);
        Assert.False(shell.IsChangingUploadLimit);Assert.True(shell.CanChangeUploadLimit);
    });

    [Theory]
    [InlineData("50")]
    [InlineData("")]
    [InlineData("fast")]
    [InlineData("10000001")]
    public Task AnInvalidUploadLimitRateIsRefusedWithThePlainRequirementBeforeTheControlIsCalled(string text)=>OnUi(async ()=>
    {
        var control=new UploadLimitControlProbe();
        await using var shell=await ReadyUploadLimit(new PriorityDesktopMonitor(1),control);
        shell.UploadLimitRate=text;
        shell.ApplyUploadLimit.Execute(null);await shell.ApplyUploadLimit.Execution;
        Assert.Equal(UploadLimitPolicy.RateRequirement,shell.UploadLimitStatus);
        Assert.Equal("Enter a whole number of kilobits per second from 100 to 10,000,000.",shell.UploadLimitStatus);
        Assert.Equal(0,control.ApplyCalls);Assert.Equal(0,control.RemoveCalls);
    });

    [Fact]
    public Task StartupShowsTheUploadLimitRecordTheControlLoadedAndRemoveClearsIt()=>OnUi(async ()=>
    {
        var control=new UploadLimitControlProbe{Record=new(UploadLimitAction.Apply,1000,DateTimeOffset.UtcNow.AddMinutes(-5),true)};
        await using var shell=await ReadyUploadLimit(new PriorityDesktopMonitor(1),control);
        Assert.Equal(1,control.LoadCalls);
        Assert.StartsWith("Upload limit applied by Zenith in this Windows session: 1,000 kbit/s",shell.CurrentUploadLimitText,StringComparison.Ordinal);
        // The box offers the recorded rate, so Apply again means the same limit unless the user types another.
        Assert.Equal("1000",shell.UploadLimitRate);
        shell.RemoveUploadLimit.Execute(null);await shell.RemoveUploadLimit.Execution;
        var call=await control.NextAsync();
        Assert.Equal(UploadLimitAction.Remove,call.Action);
        Assert.Equal("Windows confirmed that Zenith's upload limit policies are gone. Zenith no longer limits what Roblox clients send.",shell.UploadLimitStatus);
        Assert.StartsWith("No upload limit applied by Zenith in this Windows session.",shell.CurrentUploadLimitText,StringComparison.Ordinal);
        Assert.Contains(shell.Events,row=>row.EndsWith("Upload limit removed; Windows confirmed Zenith's policies are gone.",StringComparison.Ordinal));
    });

    [Fact]
    public Task AnUncertainOrUnreadableUploadLimitRecordIsSaidPlainlyAtStartup()=>OnUi(async ()=>
    {
        var uncertain=new UploadLimitControlProbe{Record=new(UploadLimitAction.Remove,1000,DateTimeOffset.UtcNow,false)};
        await using(var shell=await ReadyUploadLimit(new PriorityDesktopMonitor(1),uncertain))
            Assert.StartsWith("Zenith does not know whether its upload limit of 1,000 kbit/s is in force: the last Remove ended without Windows' confirmation",shell.CurrentUploadLimitText,StringComparison.Ordinal);
        var unreadable=new UploadLimitControlProbe{Unreadable=true};
        await using(var shell=await ReadyUploadLimit(new PriorityDesktopMonitor(1),unreadable))
        {
            Assert.Equal("Zenith could not read its record of the upload limit (upload-limit.json in the data folder), so it cannot rule out an older helper. New changes are blocked; review the record for recovery.",shell.CurrentUploadLimitText);
            Assert.True(shell.IsReady);
        }
    });

    [Fact]
    public Task SampleModeWithoutAControlOffersNoUploadLimitChange()=>OnUi(async ()=>
    {
        await using var shell=await ReadyUploadLimit(new PriorityDesktopMonitor(1),null,sample:true);
        Assert.False(shell.CanChangeUploadLimit);
        shell.ApplyUploadLimit.Execute(null);await shell.ApplyUploadLimit.Execution;
        shell.RemoveUploadLimit.Execute(null);await shell.RemoveUploadLimit.Execution;
        Assert.False(shell.IsChangingUploadLimit);
        Assert.Equal("Choose a rate and Apply upload limit, or Remove. Windows asks for administrator approval first.",shell.UploadLimitStatus);
    });

    [Theory]
    [InlineData("panic")]
    [InlineData("lock")]
    public Task PanicAndLockStopAWaitingUploadLimitOperation(string stop)=>OnUi(async ()=>
    {
        var control=new UploadLimitControlProbe{Defer=true};
        await using var shell=await ReadyUploadLimit(new PriorityDesktopMonitor(1),control);
        shell.ApplyUploadLimit.Execute(null);
        var call=await control.NextAsync();
        Assert.True(shell.IsChangingUploadLimit);Assert.False(shell.CanChangeUploadLimit);
        Assert.Equal("Waiting for the administrator prompt and Windows PowerShell…",shell.UploadLimitStatus);
        if(stop=="panic") shell.Panic.Execute(null); else shell.Lock.Execute(null);
        Assert.Equal(1,control.CancelCalls);
        Assert.True(call.Token.IsCancellationRequested);
        call.Reply.TrySetResult(new(UploadLimitAction.Apply,UploadLimitStatus.Cancelled,1000,0,null));
        await shell.ApplyUploadLimit.Execution;
        Assert.Equal("Upload limit wait stopped by Panic or Lock. No unresolved helper remains; review the current result before retrying.",shell.UploadLimitStatus);
        Assert.False(shell.IsChangingUploadLimit);
    });

    [Fact]
    public Task CancelStopsTheWaitAndAFailedRunIsAnErrorAgainstTheUploadLimitSubsystem()=>OnUi(async ()=>
    {
        var control=new UploadLimitControlProbe{Defer=true};
        await using var shell=await ReadyUploadLimit(new PriorityDesktopMonitor(1),control);
        shell.ApplyUploadLimit.Execute(null);
        var call=await control.NextAsync();
        shell.CancelUploadLimit.Execute(null);
        Assert.True(call.Token.IsCancellationRequested);Assert.Equal(1,control.CancelCalls);
        call.Reply.TrySetResult(new(UploadLimitAction.Apply,UploadLimitStatus.Cancelled,1000,0,null));
        await shell.ApplyUploadLimit.Execution;
        Assert.StartsWith("The upload limit wait was stopped.",shell.UploadLimitStatus,StringComparison.Ordinal);
        Assert.Equal("",shell.ErrorNotice);
        // A refused run is an error with the typed subsystem; a declined prompt is the user's own choice and only a status line.
        control.Defer=false;control.Outcome=UploadLimitStatus.Refused;control.ExitCode=1;
        shell.ApplyUploadLimit.Execute(null);await shell.ApplyUploadLimit.Execution;await control.NextAsync();
        Assert.Equal("Windows PowerShell could not change the upload limit (exit code 1). Whether a limit is in force is now uncertain; choose Apply upload limit or Remove again to be sure.",shell.ErrorNotice);
        Assert.Equal(shell.ErrorNotice,shell.UploadLimitStatus);
        var diagnostics=(ManagerDiagnostics)typeof(ShellViewModel).GetField("_diagnostics",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(shell)!;
        Assert.Contains(diagnostics.GetSnapshot().Events,row=>row.Kind==ManagerEventKind.OperationFailed && row.Subsystem==ManagerSubsystem.UploadLimit);
        control.Outcome=UploadLimitStatus.Declined;
        shell.RemoveUploadLimit.Execute(null);await shell.RemoveUploadLimit.Execution;await control.NextAsync();
        Assert.Equal("The administrator prompt was declined, so nothing was changed. Applying or removing an upload limit needs administrator approval.",shell.UploadLimitStatus);
        Assert.Equal(1,diagnostics.GetSnapshot().Events.Count(row=>row.Kind==ManagerEventKind.OperationFailed && row.Subsystem==ManagerSubsystem.UploadLimit));
    });

    [Fact]
    public void TheUploadLimitSectionIsOnSessionsWithItsButtonsBoundAndTheGuideExplainsIt()
    {
        using var source=typeof(ClientPreviewPresentationTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        var document=XDocument.Load(source);
        XNamespace presentation="http://schemas.microsoft.com/winfx/2006/xaml/presentation",xaml="http://schemas.microsoft.com/winfx/2006/xaml";
        var tools=document.Descendants(presentation+"Expander").Single(element=>(string?)element.Attribute(xaml+"Name")=="UploadLimitTools");
        Assert.Equal("Upload limit",(string?)tools.Attribute("Header"));
        var buttons=tools.Descendants(presentation+"Button").ToArray();
        var apply=Assert.Single(buttons,button=>(string?)button.Attribute(xaml+"Name")=="ApplyUploadLimitButton");
        Assert.Equal("{Binding ApplyUploadLimit}",(string?)apply.Attribute("Command"));Assert.Equal("{Binding CanChangeUploadLimit}",(string?)apply.Attribute("IsEnabled"));
        var remove=Assert.Single(buttons,button=>(string?)button.Attribute(xaml+"Name")=="RemoveUploadLimitButton");
        Assert.Equal("{Binding RemoveUploadLimit}",(string?)remove.Attribute("Command"));Assert.Equal("Remove",(string?)remove.Attribute("Content"));
        Assert.Contains(buttons,button=>(string?)button.Attribute("Command")=="{Binding CancelUploadLimit}" && (string?)button.Attribute("IsEnabled")=="{Binding IsChangingUploadLimit}");
        var box=Assert.Single(tools.Descendants(presentation+"TextBox"));
        Assert.Equal("UploadLimitRateBox",(string?)box.Attribute(xaml+"Name"));
        Assert.Equal("{Binding UploadLimitRate, UpdateSourceTrigger=PropertyChanged}",(string?)box.Attribute("Text"));
        var texts=tools.Descendants(presentation+"TextBlock").Select(text=>(string?)text.Attribute("Text")).ToArray();
        Assert.Contains("{Binding CurrentUploadLimitText}",texts);Assert.Contains("{Binding UploadLimitStatus}",texts);
        Assert.Contains(texts,text=>text?.Contains("administrator prompt",StringComparison.Ordinal)==true && text.Contains("ends when Windows restarts",StringComparison.Ordinal));
        // The Guide has its own section, and settings search lands on the rate box, which changes nothing by itself.
        var guide=document.Descendants(presentation+"TextBlock").Single(text=>ProductionWindowStrings.Resolve((string?)text.Attribute("Text"))=="Upload limit" && (string?)text.Attribute("Style")=="{StaticResource SectionTitle}");
        string explanation=(string)guide.ElementsAfterSelf(presentation+"TextBlock").First().Attribute("Text")!;
        Assert.Contains("In Sessions, open Upload limit.",explanation,StringComparison.Ordinal);
        Assert.Contains("Get-NetQosPolicy -PolicyStore ActiveStore",explanation,StringComparison.Ordinal);
        Assert.Equal(new SettingsSearchDestination("UploadLimitTools","UploadLimitRateBox"),SettingsSearchDestinations.For(SettingsSearchTarget.UploadLimit));
        Assert.Equal("Sessions",SettingsSearchCatalog.PageOf(SettingsSearchTarget.UploadLimit));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ALateNoStartReconciliationRefreshesTheCurrentRecordAndFinalStatus(bool beforeCancelResult)=>OnUi(async ()=>
    {
        var control=new UploadLimitControlProbe{Defer=true};
        await using var shell=await ReadyUploadLimit(new PriorityDesktopMonitor(1),control);
        shell.ApplyUploadLimit.Execute(null);var call=await control.NextAsync();
        shell.CancelUploadLimit.Execute(null);
        int recordChanges=0;shell.PropertyChanged+=(_,args)=>{if(args.PropertyName==nameof(shell.CurrentUploadLimitText))recordChanges++;};
        var declined=new UploadLimitResult(UploadLimitAction.Apply,UploadLimitStatus.Declined,1000,0,null);
        if(!beforeCancelResult)
        {
            call.Reply.SetResult(new(UploadLimitAction.Apply,UploadLimitStatus.Cancelled,1000,0,null));
            await shell.ApplyUploadLimit.Execution;
        }
        control.Reconcile(new(control.OperationRevision,declined));
        await Dispatcher.CurrentDispatcher.InvokeAsync(()=>{},DispatcherPriority.Background);
        if(beforeCancelResult)
        {
            call.Reply.SetResult(new(UploadLimitAction.Apply,UploadLimitStatus.Cancelled,1000,0,null));
            await shell.ApplyUploadLimit.Execution;
        }
        Assert.Equal(declined.Explanation,shell.UploadLimitStatus);Assert.True(recordChanges>0);
        Assert.StartsWith("No upload limit applied by Zenith",shell.CurrentUploadLimitText);
    });

    [Fact]
    public Task AQueuedReconciliationCannotReplaceANewerOperationStatus()=>OnUi(async ()=>
    {
        var control=new UploadLimitControlProbe();
        await using var shell=await ReadyUploadLimit(new PriorityDesktopMonitor(1),control);
        control.Reconcile(new(control.OperationRevision,new(UploadLimitAction.Apply,UploadLimitStatus.Declined,1000,0,null)));
        shell.UploadLimitRate="4500";shell.ApplyUploadLimit.Execute(null);await shell.ApplyUploadLimit.Execution;
        await Dispatcher.CurrentDispatcher.InvokeAsync(()=>{},DispatcherPriority.Background);
        Assert.Contains("Windows confirmed the upload limit",shell.UploadLimitStatus);
        Assert.Contains("4,500 kbit/s",shell.CurrentUploadLimitText);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AnUnresolvedUploadLimitOwnerDisablesMutationsUntilReconciled(bool unreadable)=>OnUi(async ()=>
    {
        var control=new UploadLimitControlProbe{Unreadable=unreadable,Record=unreadable?null:new(UploadLimitAction.Apply,1000,DateTimeOffset.UtcNow,false,HelperUnresolved:true)};
        await using var shell=await ReadyUploadLimit(new PriorityDesktopMonitor(1),control);
        Assert.False(shell.CanChangeUploadLimit);
        shell.ApplyUploadLimit.Execute(null);await shell.ApplyUploadLimit.Execution;
        shell.RemoveUploadLimit.Execute(null);await shell.RemoveUploadLimit.Execution;
        Assert.Equal(0,control.ApplyCalls+control.RemoveCalls);
        if(unreadable) Assert.Contains("cannot rule out an older helper",shell.CurrentUploadLimitText);
        else Assert.Equal(UploadLimitResult.PendingHelper,shell.CurrentUploadLimitText);
        int changed=0;shell.PropertyChanged+=(_,a)=>{if(a.PropertyName==nameof(shell.CanChangeUploadLimit))changed++;};
        control.Record=null;control.Unreadable=false;
        control.Reconcile(new(control.OperationRevision,new(UploadLimitAction.Apply,UploadLimitStatus.Declined,1000,0,null)));
        await Dispatcher.CurrentDispatcher.InvokeAsync(()=>{},DispatcherPriority.Background);
        Assert.True(shell.CanChangeUploadLimit);Assert.True(changed>0);
    });

    private async Task<ShellViewModel> ReadyUploadLimit(PriorityDesktopMonitor monitor,UploadLimitControlProbe? control,bool sample=false)
    {
        var capture=new PreviewProvider();
        var shell=new ShellViewModel(_root,sample,Dispatcher.CurrentDispatcher,monitor,
            new NoPublicDirectory(),new NoServerDirectory(),previewProvider:capture,uploadLimit:control);
        try
        {
            await shell.InitializeAsync();await Refresh(shell);Assert.True(shell.IsReady,shell.Notice);
            return shell;
        }
        catch{await shell.DisposeAsync();throw;}
    }

    private sealed record UploadLimitCall(UploadLimitAction Action,int KilobitsPerSecond,CancellationToken Token)
    {internal TaskCompletionSource<UploadLimitResult> Reply {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);}

    private sealed class UploadLimitControlProbe:IUploadLimitControl
    {
        public event Action<UploadLimitReconciliation>? Reconciled;
        public long OperationRevision { get; private set; }
        internal void Reconcile(UploadLimitReconciliation result) => Reconciled?.Invoke(result);
        private readonly Channel<UploadLimitCall> _calls=Channel.CreateBounded<UploadLimitCall>(4);
        internal bool Defer,Unreadable;
        internal UploadLimitStatus Outcome=UploadLimitStatus.Confirmed;
        internal int ExitCode,ApplyCalls,RemoveCalls,CancelCalls,LoadCalls;
        internal UploadLimitRecord? Record;
        public UploadLimitRecord? Current=>Record;
        public bool RecordUnreadable=>Unreadable;
        public Task<UploadLimitRecord?> LoadRecordAsync(CancellationToken ct){LoadCalls++;return Task.FromResult(Record);}
        public Task<UploadLimitResult> ApplyAsync(int kilobitsPerSecond,CancellationToken ct){ApplyCalls++;return RunAsync(new(UploadLimitAction.Apply,kilobitsPerSecond,ct));}
        public Task<UploadLimitResult> RemoveAsync(CancellationToken ct){RemoveCalls++;return RunAsync(new(UploadLimitAction.Remove,0,ct));}
        private async Task<UploadLimitResult> RunAsync(UploadLimitCall call)
        {
            OperationRevision++;
            Assert.True(_calls.Writer.TryWrite(call));
            if(Defer) return await call.Reply.Task;
            switch(Outcome)
            {
                case UploadLimitStatus.Confirmed:
                    Record=call.Action==UploadLimitAction.Apply?new(UploadLimitAction.Apply,call.KilobitsPerSecond,DateTimeOffset.UtcNow,true):null;Unreadable=false;break;
                case UploadLimitStatus.Refused or UploadLimitStatus.NotConfirmed or UploadLimitStatus.TimedOut or UploadLimitStatus.Cancelled:
                    Record=new(call.Action,call.Action==UploadLimitAction.Apply?call.KilobitsPerSecond:Record?.KilobitsPerSecond??0,DateTimeOffset.UtcNow,false);break;
            }
            return new(call.Action,Outcome,call.KilobitsPerSecond,ExitCode,Record);
        }
        public void CancelOperations()=>CancelCalls++;
        internal Task<UploadLimitCall> NextAsync()=>_calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
