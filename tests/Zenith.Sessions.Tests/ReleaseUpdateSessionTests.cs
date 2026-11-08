using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;
public sealed class ReleaseUpdateSessionTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-update-owner-"+Guid.NewGuid().ToString("N"));
    private static ReleaseUpdateSettings Settings(bool daily=false)=>new("https://updates.example.test/feed.json",new string('A',64),daily);
    [Fact]
    public async Task DefaultStateNeverStartsAFeedOrWritesConfiguration()
    {
        var backend=new Backend();await using var session=new ReleaseUpdateSession(_root,backend);
        await session.InitializeAsync(default);await session.CheckNowAsync(default);
        Assert.Equal(0,backend.Calls);Assert.False(Directory.Exists(_root));Assert.Null(session.State.Pending);
    }
    [Fact]
    public async Task ConcurrentChecksCoalesceAndCancellationCannotPublishLateReadyState()
    {
        var backend=new Backend{Gate=new(TaskCreationOptions.RunContinuationsAsynchronously)};await using var session=new ReleaseUpdateSession(_root,backend);
        await session.InitializeAsync(default);await session.ConfigureAsync(Settings(),default);
        var first=session.CheckNowAsync(default);await backend.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(first,session.CheckNowAsync(default));session.CancelWork();backend.Gate.SetResult();await first;
        Assert.Equal(1,backend.Calls);Assert.Null(session.State.Pending);Assert.Contains("stopped",session.Status);
    }
    [Fact]
    public async Task ApprovalRequiresIdleQueueAndSurvivesOnlyUntilExplicitStartupConsumption()
    {
        var backend=new Backend{Stage=Path.Combine(_root,"Updates","update-test")};await using var session=new ReleaseUpdateSession(_root,backend);
        await session.InitializeAsync(default);await session.ConfigureAsync(Settings(),default);await session.CheckNowAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>session.ApproveNextStartAsync(false,default));
        await session.ApproveNextStartAsync(true,default);
        Assert.True(ReleaseUpdateSession.ReadState(Path.Combine(_root,"release-updates.json")).InstallOnNextStart);
        string app=Path.Combine(_root,"app");Directory.CreateDirectory(app);File.WriteAllText(Path.Combine(app,"Install-ZenithSessions.ps1"),"fixture");File.WriteAllText(Path.Combine(app,"Zenith.Sessions.ReleaseTool.exe"),"fixture");
        ProcessStartInfo? launch=null;
        Assert.True(PendingReleaseUpdateHandoff.Offer(_root,app,version=>version=="2.0.0.0",start=>launch=start));
        Assert.NotNull(launch);Assert.Contains("-WaitForProcessStartTicks",launch.ArgumentList);Assert.Contains("-NoProfile",launch.ArgumentList);
        Assert.False(ReleaseUpdateSession.ReadState(Path.Combine(_root,"release-updates.json")).InstallOnNextStart);
        Assert.False(PendingReleaseUpdateHandoff.Offer(_root,app,_=>throw new Exception("must not repeat"),_=>throw new Exception("must not launch")));
    }
    [Fact]
    public async Task DecliningStartupKeepsThePendingReleaseAndNeverLaunchesAnInstaller()
    {
        var state=new ReleaseUpdateState(Settings(),Pending:new(Path.Combine(_root,"Updates","update-test"),"2.0.0.0"),InstallOnNextStart:true);
        Directory.CreateDirectory(_root);await File.WriteAllTextAsync(Path.Combine(_root,"release-updates.json"),JsonSerializer.Serialize(state));
        Assert.False(PendingReleaseUpdateHandoff.Offer(_root,_root,_=>false,_=>throw new Exception("must not launch")));
        var saved=ReleaseUpdateSession.ReadState(Path.Combine(_root,"release-updates.json"));Assert.False(saved.InstallOnNextStart);Assert.NotNull(saved.Pending);
    }
    [Fact]
    public async Task DailyLoopUsesOneDayCadenceAndPanicPausesFutureChecks()
    {
        var clock=new Clock();var backend=new Backend();await using var session=new ReleaseUpdateSession(_root,backend,time:clock);
        await session.InitializeAsync(default);await session.ConfigureAsync(Settings(true),default);
        await backend.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var timer=await clock.Next();Assert.Equal(TimeSpan.FromDays(1),timer.Delay);Assert.Equal(1,backend.Calls);
        await session.DiscardPendingAsync(default);session.CancelWork();clock.Fire(timer);
        timer=await clock.Next();Assert.Equal(1,backend.Calls);Assert.Equal(TimeSpan.FromDays(1),timer.Delay);
        await session.CheckNowAsync(default);Assert.Equal(2,backend.Calls);
    }
    [Fact]
    public async Task DisposalCancelsAndWaitsForTheOwnedCheckBeforeReleasingItsState()
    {
        var backend=new Backend{Gate=new(TaskCreationOptions.RunContinuationsAsynchronously)};var session=new ReleaseUpdateSession(_root,backend);
        await session.InitializeAsync(default);await session.ConfigureAsync(Settings(),default);var check=session.CheckNowAsync(default);
        await backend.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var dispose=session.DisposeAsync().AsTask();Assert.False(dispose.IsCompleted);backend.Gate.SetResult();
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));await check;Assert.Null(session.State.Pending);
    }
    [Fact]
    public async Task DiscardOnlyDeletesAnOwnedStageAndPreservesPotentialUserData()
    {
        string stage=Path.Combine(_root,"Updates","update-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        await File.WriteAllTextAsync(Path.Combine(stage,"signed-update.json"),"fixture");
        var backend=new Backend{Stage=stage};await using var session=new ReleaseUpdateSession(_root,backend);
        await session.InitializeAsync(default);await session.ConfigureAsync(Settings(),default);await session.CheckNowAsync(default);
        string userFile=Path.Combine(stage,"accounts.db");await File.WriteAllTextAsync(userFile,"preserve");
        await Assert.ThrowsAsync<IOException>(()=>session.DiscardPendingAsync(default));Assert.True(File.Exists(userFile));Assert.NotNull(session.State.Pending);
        File.Delete(userFile);await session.DiscardPendingAsync(default);Assert.False(Directory.Exists(stage));Assert.Null(session.State.Pending);
    }
    [Theory][InlineData("http://example.test/feed")][InlineData("https://secret@example.test/feed")][InlineData("https://example.test/feed?token=x")]
    public void FeedConfigurationRefusesUnsafeOrCredentialBearingAddresses(string feed)
        =>Assert.Throws<ArgumentException>(()=>ReleaseUpdateSession.Validate(new(feed,new string('A',64),true)));
    private sealed class Backend:IReleaseUpdateBackend
    {
        public int Calls;public string Stage="synthetic-stage";public TaskCompletionSource? Gate;
        public Channel<bool> Started=Channel.CreateUnbounded<bool>();
        public async Task<StagedUpdate> StageAsync(ReleaseUpdateSettings settings,CancellationToken ct)
        {Interlocked.Increment(ref Calls);Started.Writer.TryWrite(true);if(Gate is not null)await Gate.Task;return new(Stage,"2.0.0.0");}
    }
    private sealed class Clock:TimeProvider
    {
        private readonly Channel<Timer> _timers=Channel.CreateUnbounded<Timer>();private DateTimeOffset _now=new(2026,11,8,0,0,0,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()=>_now;
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period){var timer=new Timer(callback,state,dueTime);_timers.Writer.TryWrite(timer);return timer;}
        public Task<Timer> Next()=>_timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public void Fire(Timer timer){_now+=timer.Delay;if(!timer.Disposed)timer.Callback(timer.State);}
        internal sealed class Timer(TimerCallback callback,object? state,TimeSpan delay):ITimer
        {public TimerCallback Callback=callback;public object? State=state;public TimeSpan Delay=delay;public bool Disposed;public bool Change(TimeSpan dueTime,TimeSpan period)=>false;public void Dispose()=>Disposed=true;public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}}
    }
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
