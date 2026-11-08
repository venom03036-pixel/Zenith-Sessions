using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientPreviewPresentationTests
{
    [Fact]
    public Task DataEncryptionRequiresAnExplicitCurrentReviewAndDoesNotRunOnStartup() => OnUi(async () =>
    {
        var control = new EncryptionProbe(); await using var shell = await ReadyEncryption(control);
        Assert.Equal(0, control.Calls); shell.EncryptDataFolder.Execute(null); await shell.EncryptDataFolder.Execution;
        Assert.Equal(0, control.Calls);
        var stale = shell.ReviewDataEncryption(); shell.Panic.Execute(null);
        shell.EncryptDataFolder.Execute(stale); await shell.EncryptDataFolder.Execution; Assert.Equal(0, control.Calls);
        var accepted = shell.ReviewDataEncryption();
        shell.EncryptDataFolder.Execute(accepted); await control.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        control.Reply.SetResult(new(DataFolderEncryptionState.Partial, 2, 1, DataFolderEncryptionProblem.AccessOrSharing));
        await shell.EncryptDataFolder.Execution; Assert.Contains("incomplete", shell.DataEncryptionStatus); Assert.True(shell.CanEncryptDataFolder);
        shell.EncryptDataFolder.Execute(accepted); await shell.EncryptDataFolder.Execution; Assert.Equal(1, control.Calls);
    });

    [Theory]
    [InlineData("panic")]
    [InlineData("lock")]
    [InlineData("cancel")]
    public Task DataEncryptionStopsTheOwnedWorkAndDoesNotPublishLateSuccess(string action) => OnUi(async () =>
    {
        var control = new EncryptionProbe(); await using var shell = await ReadyEncryption(control);
        shell.EncryptDataFolder.Execute(shell.ReviewDataEncryption()); await control.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (action == "lock") shell.LockDashboard(); else if (action == "panic") shell.Panic.Execute(null); else shell.CancelDataEncryption.Execute(null);
        Assert.True(control.Token.IsCancellationRequested); Assert.False(shell.CanEncryptDataFolder); Assert.True(shell.IsEncryptingDataFolder);
        control.Reply.SetResult(new(DataFolderEncryptionState.Verified, 2, 0, DataFolderEncryptionProblem.None));
        await shell.EncryptDataFolder.Execution;
        Assert.Contains("stopped", shell.DataEncryptionStatus); Assert.DoesNotContain("verified", shell.DataEncryptionStatus);
        Assert.False(shell.IsEncryptingDataFolder);
    });

    [Fact]
    public Task DataEncryptionShutdownJoinsTheActualNonCancellableNativeOwner() => OnUi(async () =>
    {
        var control = new EncryptionProbe(); var shell = await ReadyEncryption(control);
        shell.EncryptDataFolder.Execute(shell.ReviewDataEncryption()); await control.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task shutdown = shell.DisposeAsync().AsTask();
        Assert.True(control.Token.IsCancellationRequested); Assert.False(shutdown.IsCompleted);
        control.Reply.SetResult(new(DataFolderEncryptionState.Stopped, 1, 0, DataFolderEncryptionProblem.None));
        await shutdown;
    });

    [Fact]
    public Task SampleModeNeverOffersRealEfsEvenIfTheCommandIsInvokedDirectly() => OnUi(async () =>
    {
        await using var shell = new ShellViewModel(_root, true, Dispatcher.CurrentDispatcher, new PriorityDesktopMonitor(0),
            new NoPublicDirectory(), new NoServerDirectory(), previewProvider: new PreviewProvider());
        await shell.InitializeAsync(); Assert.False(shell.CanEncryptDataFolder); Assert.Null(shell.ReviewDataEncryption());
        shell.EncryptDataFolder.Execute(null); await shell.EncryptDataFolder.Execution;
        Assert.Contains("not checked", shell.DataEncryptionStatus);
    });

    [Fact]
    public Task APanicDuringCommandNotificationInvalidatesTheAlreadyReviewedRequest() => OnUi(async () =>
    {
        var control = new EncryptionProbe(); await using var shell = await ReadyEncryption(control);
        var review = shell.ReviewDataEncryption();
        shell.EncryptDataFolder.CanExecuteChanged += (_, _) => shell.Panic.Execute(null);
        shell.EncryptDataFolder.Execute(review); await shell.EncryptDataFolder.Execution;
        Assert.Equal(0, control.Calls);
    });

    private async Task<ShellViewModel> ReadyEncryption(EncryptionProbe control)
    {
        var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new PriorityDesktopMonitor(0),
            new NoPublicDirectory(), new NoServerDirectory(), previewProvider: new PreviewProvider(), dataEncryption: control);
        try { await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice); return shell; }
        catch { await shell.DisposeAsync(); throw; }
    }
    private sealed class EncryptionProbe : IDataFolderEncryption
    {
        internal int Calls; internal CancellationToken Token;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<DataFolderEncryptionResult> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DataFolderEncryptionResult> EncryptAsync(CancellationToken cancellationToken)
        { Calls++; Token = cancellationToken; Entered.TrySetResult(); return Reply.Task; }
    }
}
