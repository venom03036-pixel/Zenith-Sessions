using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Xunit;

namespace Zenith.Sessions.Tests;

// Windows sign-out, restart or shutdown: WPF raises SessionEnding and then closes every window without the tray
// Exit path, so ShellViewModel.DisposeAsync never runs. The session-end fence must stop work and flush the log itself.
public sealed class SessionEndingShutdownTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-session-ending-tests-"+Guid.NewGuid().ToString("N"));
    private string Data=>Path.Combine(_root,"data");

    [Fact]
    public Task SessionEndingRecordsShutdownAndFlushesDiagnosticsWithoutFullDisposal()=>OnUi(async ()=>
    {
        var shell=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());
        try
        {
            await shell.InitializeAsync();
            shell.Fail(new IOException("queued before sign-out"));
            // Synchronous on the UI thread, as inside WM_QUERYENDSESSION: nothing else will flush the log afterwards.
            bool flushed=shell.PrepareForSessionEnd(TimeSpan.FromSeconds(3));
            string[] lines=ReadKinds();
            Assert.True(lines.Length>0 && lines[^1]=="Shutdown",
                "Session end did not write the Shutdown record. Logged kinds: "+string.Join(", ",lines));
            Assert.True(flushed,"The session-end fence reported that the diagnostics log was not flushed.");
            int failed=Array.LastIndexOf(lines,"OperationFailed");
            Assert.True(failed>=0 && failed<lines.Length-1,"The event queued before sign-out was lost or written after Shutdown.");
            Assert.True(shell.IsShuttingDown,"Work could still start after the session-end fence.");
        }
        finally { await shell.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)); }
        // The later full disposal (or a repeated session end) neither duplicates nor reopens the record.
        Assert.True(shell.PrepareForSessionEnd(TimeSpan.FromSeconds(3)));
        string[] after=ReadKinds();
        Assert.Equal(1,after.Count(k=>k=="Shutdown"));Assert.Equal("Shutdown",after[^1]);
    });

    private string[] ReadKinds()
    {
        string path=Path.Combine(Data,"diagnostics","manager.jsonl");
        if(!File.Exists(path)) return [];
        // Shared read: if the log was not closed, its writer may still hold the file and this must report that, not throw.
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        using var reader=new StreamReader(file);
        return reader.ReadToEnd().Split('\n').Select(l=>l.Trim()).Where(l=>l.Length>0)
            .Select(l=> { using var line=JsonDocument.Parse(l); return line.RootElement.GetProperty("Kind").GetString()!; }).ToArray();
    }
    private sealed class SyntheticMonitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)=>Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,64*CapacityPolicy.GiB,20*CapacityPolicy.GiB,20),[]));
        public Task<bool> FocusAsync(SessionObservation session,CancellationToken ct)=>Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session,CancellationToken ct)=>Task.FromResult(false);
    }
    private static Task OnUi(Func<Task> work)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(()=>_=RunAsync());
            async Task RunAsync(){try{await work();done.TrySetResult();}catch(Exception ex){done.TrySetException(ex);}finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}}
            try {Dispatcher.Run();}
            catch(Exception ex) {done.TrySetException(new Xunit.Sdk.XunitException($"A dispatcher fault escaped and ended the UI thread ({ex.GetType().Name})."));}
        }) {IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-session-ending-tests-",StringComparison.Ordinal))throw new InvalidOperationException("Unexpected test cleanup path.");
        if(Directory.Exists(full))Directory.Delete(full,true);
    }
}
