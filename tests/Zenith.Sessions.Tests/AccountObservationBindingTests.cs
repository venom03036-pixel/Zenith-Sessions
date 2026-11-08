using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    [Fact]
    public Task BoundAccountListPreservesPrivateDetailsAcrossItsOwnCompletedReload()=>OnUi(async()=>
    {
        var data=new ObservationData();
        await using var shell=await HealthShell(new HealthSignIn(),new HealthClient(),linked:2,accountData:data);
        shell.SelectedAccount=HealthRow(shell,1);
        var list=BindObservationAccountList(shell);bool sawTransientDeselect=false;
        list.SelectionChanged+=(_,_)=>{if(list.SelectedItem is null)sawTransientDeselect=true;};
        shell.RefreshPrivateDetails.Execute(null);await shell.RefreshPrivateDetails.Execution;
        await Dispatcher.CurrentDispatcher.InvokeAsync(()=>{},DispatcherPriority.DataBind);
        Assert.True(sawTransientDeselect); // Actual WPF reset, not a manual SelectedAccount assignment.
        Assert.Equal(7,data.Calls.Length);Assert.False(shell.IsAuthorizing);
        Assert.Same(shell.SelectedAccount,list.SelectedItem);Assert.Equal(1001,shell.SelectedAccount!.KnownUserId);
        Assert.Contains("Robux: 938572614",shell.PrivateAccountDetails);
        Assert.Contains("Premium: Unknown",shell.PrivateAccountDetails);
        // A new sign-in generation for the same profile invalidates the old observation.
        long generation=shell.SelectedAccount.AuthorizationGeneration!.Value;
        shell.RefreshAuthorization.Execute(null);await shell.RefreshAuthorization.Execution;
        Assert.True(shell.SelectedAccount!.AuthorizationGeneration>generation);
        Assert.DoesNotContain("938572614",shell.PrivateAccountDetails);
        BindingOperations.ClearAllBindings(list);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task BoundAccountListActualSelectionChangeCancelsAndClearsPrivateRead(bool deselect)=>OnUi(async()=>
    {
        var data=new ObservationData{Hold=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        await using var shell=await HealthShell(new HealthSignIn(),new HealthClient(),linked:2,accountData:data);
        shell.SelectedAccount=HealthRow(shell,1);var list=BindObservationAccountList(shell);
        shell.RefreshPrivateDetails.Execute(null);await data.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            list.SelectedItem=deselect?null:HealthRow(shell,2);
            Assert.True(data.LastToken.IsCancellationRequested);
            if(deselect)Assert.Null(shell.SelectedAccount);else Assert.Equal(1002,shell.SelectedAccount!.KnownUserId);
        }
        finally {data.Hold.TrySetResult();await shell.RefreshPrivateDetails.Execution;}
        Assert.Single(data.Calls);Assert.DoesNotContain("938572614",shell.PrivateAccountDetails);
        BindingOperations.ClearAllBindings(list);
    });

    [Fact]
    public Task BoundAccountListFinalMissingSelectionClearsRetainedPrivateDetails()=>OnUi(async()=>
    {
        var accounts=new AccountRepository(_root);
        await using var shell=await HealthShell(new HealthSignIn(),new HealthClient(),linked:2,accounts:accounts,accountData:new ObservationData());
        shell.SelectedAccount=HealthRow(shell,1);var list=BindObservationAccountList(shell);
        shell.RefreshPrivateDetails.Execute(null);await shell.RefreshPrivateDetails.Execution;
        Assert.Contains("938572614",shell.PrivateAccountDetails);
        var selected=shell.SelectedAccount!.Profile;
        await accounts.DeleteAsync(selected.Id,selected.Revision,default);
        shell.Refresh.Execute(null);await shell.Refresh.Execution;
        Assert.Null(shell.SelectedAccount);Assert.Null(list.SelectedItem);
        Assert.DoesNotContain("938572614",shell.PrivateAccountDetails);
        BindingOperations.ClearAllBindings(list);
    });

    private static ListBox BindObservationAccountList(ShellViewModel shell)
    {
        var list=new ListBox{DataContext=shell};
        list.SetBinding(ItemsControl.ItemsSourceProperty,new Binding(nameof(ShellViewModel.Accounts)));
        // The same default TwoWay SelectedItem binding used by MainWindow.AccountList.
        list.SetBinding(Selector.SelectedItemProperty,new Binding(nameof(ShellViewModel.SelectedAccount)));
        Assert.Same(shell.SelectedAccount,list.SelectedItem);
        return list;
    }
}
