using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class ShellQueueRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-shell-queue-recovery-" + Guid.NewGuid().ToString("N"));
    private string QueuePath => Path.Combine(_root, "launch-queue.json");

    [Fact]
    public Task ActualShellKeepsAccountsVisibleAndShowsBackupReviewWithoutStartingRecoveredClients() => OnUi(async () =>
    {
        var profile = await CreateProfile(); var at = DateTimeOffset.UtcNow;
        var operation = new LaunchOperation(new(Guid.NewGuid(), profile.Id, profile.Revision, 123, 42, 0, at.AddMinutes(-2), 7),
            LaunchState.ClientStartedUnverified, LaunchProblem.ClientStartedUnverified, at,
            new(900001, at.AddMinutes(-1).UtcTicks, at), new(4 * CapacityPolicy.GiB, 5 * CapacityPolicy.GiB));
        byte[] backup = JsonSerializer.SerializeToUtf8Bytes(new { Version = 7, Queue = new LaunchQueueSnapshot(false, [operation]) });
        await File.WriteAllBytesAsync(QueuePath + ".bak", backup);
        await using var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new NoNativeMonitor());
        await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice); Assert.True(shell.QueueReady, shell.QueueStatus);
        Assert.Equal(profile.Id, Assert.Single(shell.Accounts).Profile.Id);
        Assert.Contains("backup", shell.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(shell.Events, message => message.Contains("no client was restarted", StringComparison.OrdinalIgnoreCase));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (shell.Launches.Count == 0) await Task.Delay(5, stop.Token);
        // CAP-14-03: the receipted, budgeted started client keeps its state and stays reviewable.
        var row = Assert.Single(shell.Launches); Assert.Equal(LaunchState.ClientStartedUnverified, row.Operation.State); Assert.True(row.CanReview);
        Assert.Equal(operation.ProcessReceipt, row.Operation.ProcessReceipt); Assert.Equal(operation.ResourceBudget, row.Operation.ResourceBudget);
        Assert.Contains("Paused", shell.QueueStatus); Assert.Equal(backup, await File.ReadAllBytesAsync(QueuePath + ".bak"));
        using var store = new LaunchQueueStore(_root); var repaired = await store.LoadAsync(default);
        Assert.True(repaired.Paused); Assert.Equal(LaunchState.ClientStartedUnverified, Assert.Single(repaired.Operations).State);
        Assert.Empty(shell.Sessions);
    });

    [Theory]
    [InlineData("corrupt backup")]
    [InlineData("{\"Version\":99,\"Queue\":{}}")]
    public Task UnreadableOrFutureOrphanQueueCannotHideTheAccountLibraryOrBecomeQueueReady(string content) => OnUi(async () =>
    {
        var profile = await CreateProfile(); await File.WriteAllTextAsync(QueuePath + ".bak", content);
        await using var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new NoNativeMonitor());
        await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice); Assert.False(shell.QueueReady);
        Assert.Equal(profile.Id, Assert.Single(shell.Accounts).Profile.Id); Assert.Contains("unavailable", shell.QueueStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(shell.Launches); Assert.Empty(shell.Sessions); Assert.False(File.Exists(QueuePath));
        Assert.Equal(content, await File.ReadAllTextAsync(QueuePath + ".bak"));
    });

    [Fact]
    public Task InterruptedWriteBehindAValidPrimaryCanBeSetAsideAndTheQueueBecomesReady() => OnUi(async () =>
    {
        // A crash between CreateNew and File.Replace leaves launch-queue.json.tmp behind the last
        // committed queue. Every later start used to stay blocked with a generic message.
        await CreateProfile(); var at = DateTimeOffset.UtcNow;
        var queued = new LaunchOperation(new(Guid.NewGuid(), Guid.NewGuid(), 1, 123, 42, 0, at.AddMinutes(-2), 7),
            LaunchState.Queued, LaunchProblem.None, at, ResourceBudget: new(4 * CapacityPolicy.GiB, 5 * CapacityPolicy.GiB));
        byte[] primary = JsonSerializer.SerializeToUtf8Bytes(new { Version = 7, Queue = new LaunchQueueSnapshot(true, [queued]) });
        await File.WriteAllBytesAsync(QueuePath, primary);
        byte[] interrupted = "{\"Version\":7,\"Queue\":"u8.ToArray(); await File.WriteAllBytesAsync(QueuePath + ".tmp", interrupted);
        await using var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new NoNativeMonitor());
        await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice); Assert.False(shell.QueueReady);
        Assert.Contains("unavailable", shell.QueueStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("launch-queue.json.tmp", shell.QueueStatus); Assert.True(shell.CanSetAsideInterruptedQueueWrite);

        shell.SetAsideInterruptedQueueWrite.Execute(null); await shell.SetAsideInterruptedQueueWrite.Execution;

        Assert.True(shell.QueueReady, shell.QueueStatus + " / " + shell.Notice); Assert.False(shell.CanSetAsideInterruptedQueueWrite);
        Assert.False(File.Exists(QueuePath + ".tmp"));
        string kept = Assert.Single(Directory.GetFiles(_root, "launch-queue.json.interrupted-*"));
        Assert.Equal(interrupted, await File.ReadAllBytesAsync(kept)); Assert.Contains(Path.GetFileName(kept), shell.Notice);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (shell.Launches.Count == 0) await Task.Delay(5, stop.Token);
        var row = Assert.Single(shell.Launches); Assert.Equal(queued.Intent, row.Operation.Intent);
        Assert.Equal(LaunchState.Blocked, row.Operation.State); Assert.Equal(LaunchProblem.Interrupted, row.Operation.Problem);
        Assert.Contains("Paused", shell.QueueStatus); Assert.Empty(shell.Sessions);
        Assert.Equal(primary, await File.ReadAllBytesAsync(QueuePath + ".bak")); // The committed queue rotated normally.
    });

    [Fact]
    public Task AnOrphanInterruptedWriteIsNamedButNotOfferedForSetAside() => OnUi(async () =>
    {
        await CreateProfile(); Directory.CreateDirectory(_root);
        byte[] interrupted = "{\"Version\":7,\"Queue\":"u8.ToArray(); await File.WriteAllBytesAsync(QueuePath + ".tmp", interrupted);
        await using var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new NoNativeMonitor());
        await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice); Assert.False(shell.QueueReady);
        Assert.Contains("launch-queue.json.tmp", shell.QueueStatus); Assert.Contains("Open data folder", shell.QueueStatus);
        Assert.False(shell.CanSetAsideInterruptedQueueWrite);
        shell.SetAsideInterruptedQueueWrite.Execute(null); await shell.SetAsideInterruptedQueueWrite.Execution;
        Assert.False(shell.QueueReady); Assert.Equal(interrupted, await File.ReadAllBytesAsync(QueuePath + ".tmp"));
        Assert.Empty(Directory.GetFiles(_root, "launch-queue.json.interrupted-*")); Assert.False(File.Exists(QueuePath));
    });

    private async Task<AccountProfile> CreateProfile()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        return await repository.SaveAsync(AccountProfile.Create("Retained synthetic account") with { Notes = "Preserve metadata" }, default);
    }
    private sealed class NoNativeMonitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var at = DateTimeOffset.UtcNow;
            return Task.FromResult(new DesktopSnapshot(new(at, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 10 * CapacityPolicy.GiB, 10), [],
                ProcessObservedUtc: at, ProcessRevision: 1, ProcessInventoryComplete: true));
        }
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => throw new InvalidOperationException("No native action expected.");
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => throw new InvalidOperationException("No native action expected.");
    }
    private static Task OnUi(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.InvokeShutdown(); }
            }); Dispatcher.Run();
        }) { IsBackground = true }; thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-shell-queue-recovery-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
