using System.Text.Json;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    [Fact]
    public Task PrivateTrialReviewBindsSelectedSavedRecordAndDoesNotExposeItsCode()=>OnUi(async()=>
    {
        const string secret="12345678901234567890";
        using var store=new PrivateServerStore(_root);using var code=new ProtectedPrivateServerCode(secret);
        var saved=await store.AddAsync("Weekend trial",42,55,null,null,code,default);
        var child=new NativeEvidenceChild();await using var shell=await NativeEvidenceShellAsync(child,new NativeEvidenceMonitor());
        await shell.ShareLink.LoadSavedAsync();shell.ShareLink.SelectedSaved=Assert.Single(shell.ShareLink.Saved);
        Assert.True(shell.CanRunPrivateNativeTrial);
        var stale=Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CapturePrivateNativeTrialReview());
        Assert.Contains("Weekend trial",stale.SelectionSummary);Assert.Contains("7890",stale.SelectionSummary);
        Assert.DoesNotContain(secret,JsonSerializer.Serialize(stale));
        shell.ShareLink.SelectedSaved=null;shell.StartNativeTrial.Execute(stale);await shell.StartNativeTrial.Execution;
        Assert.Equal(0,child.Preparations);Assert.Empty(shell.Launches);
        shell.ShareLink.SelectedSaved=Assert.Single(shell.ShareLink.Saved);
        var review=Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CapturePrivateNativeTrialReview());
        shell.StartNativeTrial.Execute(review);await shell.StartNativeTrial.Execution;
        await NativeEvidenceStartedAsync(shell);
        var request=(await child.Entered.Task).OriginalIntent;
        Assert.Equal(new PrivateServerReference(saved.Id,saved.Revision),request.PrivateServer);
        Assert.Equal(RobloxServerKind.Private,request.RequestedServerKind);Assert.Null(request.RequestedJobId);
        Assert.Equal(1,child.Starts);Assert.False(shell.CanRecordNativeLaunchVerification);
        shell.StartNativeTrial.Execute(review);await shell.StartNativeTrial.Execution;Assert.Equal(1,child.Starts);
    });

    [Fact]
    public Task RemovingSavedPrivateServerInvalidatesItsReviewedTrial()=>OnUi(async()=>
    {
        using var store=new PrivateServerStore(_root);using var code=new ProtectedPrivateServerCode("12345678901234567890");
        await store.AddAsync("Weekend trial",42,55,null,null,code,default);
        var child=new NativeEvidenceChild();await using var shell=await NativeEvidenceShellAsync(child,new NativeEvidenceMonitor());
        await shell.ShareLink.LoadSavedAsync();shell.ShareLink.SelectedSaved=Assert.Single(shell.ShareLink.Saved);
        var review=Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CapturePrivateNativeTrialReview());
        shell.ShareLink.Remove.Execute(null);await shell.ShareLink.Remove.Execution;
        Assert.Empty(shell.ShareLink.Saved);shell.StartNativeTrial.Execute(review);await shell.StartNativeTrial.Execution;
        Assert.Empty(shell.Launches);Assert.Equal(0,child.Preparations);
    });

    [Fact]
    public Task RemovingSavedPrivateServerStopsAPreparationThatIgnoresCancellation()=>OnUi(async()=>
    {
        using var store=new PrivateServerStore(_root);using var code=new ProtectedPrivateServerCode("12345678901234567890");
        await store.AddAsync("Weekend trial",42,55,null,null,code,default);
        var child=new NativeEvidenceChild{PreparationGate=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        await using var shell=await NativeEvidenceShellAsync(child,new NativeEvidenceMonitor());
        await shell.ShareLink.LoadSavedAsync();shell.ShareLink.SelectedSaved=Assert.Single(shell.ShareLink.Saved);
        var review=Assert.IsType<ShellViewModel.NativeTrialReview>(shell.CapturePrivateNativeTrialReview());
        shell.StartNativeTrial.Execute(review);await shell.StartNativeTrial.Execution;
        await child.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { shell.ShareLink.Remove.Execute(null);await shell.ShareLink.Remove.Execution; }
        finally { child.PreparationGate.SetResult(); }
        await shell.DisposeAsync();
        Assert.Empty(shell.ShareLink.Saved);Assert.Equal(0,child.Starts);
    });
}
