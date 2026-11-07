using System.Diagnostics;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class ClientWindowTests
{
    private static SessionObservation Invalid()=>new(Environment.ProcessId,1,"Synthetic",0,"Synthetic",false,false,0,0,null,DateTimeOffset.UtcNow);
    [Theory]
    [InlineData(ClientWindowAction.Minimize)]
    [InlineData(ClientWindowAction.Restore)]
    public async Task InvalidWindowsNeverReportACompletedChange(ClientWindowAction action)
    {
        using var monitor=new WindowsDesktopMonitor(); var result=await monitor.ChangeAsync(Invalid(),action,default);
        Assert.False(result.Applied); Assert.Contains("changed",result.Explanation);
    }
    [Fact]
    public async Task CancellationPreventsAnyWindowOperation()
    {
        using var monitor=new WindowsDesktopMonitor(); using var stop=new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>monitor.ChangeAsync(Invalid(),ClientWindowAction.Minimize,stop.Token));
    }
    [Fact]
    public async Task ReusedOrWrongProcessIdentityCannotBeFocusedClosedOrChanged()
    {
        using var current=Process.GetCurrentProcess();
        var stale=Invalid() with { ProcessStartUtcTicks=current.StartTime.ToUniversalTime().Ticks-1,Window=(nint)1 };
        using var monitor=new WindowsDesktopMonitor();
        Assert.False(await monitor.FocusAsync(stale,default)); Assert.False(await monitor.RequestCloseAsync(stale,default));
        Assert.False((await monitor.ChangeAsync(stale,ClientWindowAction.Restore,default)).Applied);
    }
}
