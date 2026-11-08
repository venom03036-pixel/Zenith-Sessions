using System;
using System.Threading;
using System.Threading.Tasks;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private bool _keepSignInBrowserData;
    private CancellationTokenSource? _signInBrowserDataStop;
    public bool KeepSignInBrowserData
    {
        get => _keepSignInBrowserData;
        set { if (Set(ref _keepSignInBrowserData, value)) PreferencesEdited(); }
    }
    public AsyncCommand ForgetSignInBrowserData { get; private set; } = null!;

    private void InitializeSignInBrowserData()
        => ForgetSignInBrowserData = new(_ => ForgetSignInBrowserDataAsync(), error => Fail(error, ManagerSubsystem.SignIn));

    private async Task ForgetSignInBrowserDataAsync()
    {
        if (!CanUseDirectories()) return;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        stop.CancelAfter(TimeSpan.FromSeconds(5)); _signInBrowserDataStop = stop;
        try
        {
            await Task.Run(() => SignInProfileDirectory.ForgetKept(DataRoot, stop.Token), stop.Token);
            if (!stop.IsCancellationRequested && CanUseDirectories())
                Notice = "Kept sign-in browser data forgotten. Saved accounts and their protected sessions are unchanged.";
        }
        finally { if (ReferenceEquals(_signInBrowserDataStop, stop)) _signInBrowserDataStop = null; }
    }
    private void StopSignInBrowserData() => _signInBrowserDataStop?.Cancel();
}
