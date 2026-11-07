using System.ComponentModel;
using System.Runtime.InteropServices;
using MacroSuite.Infrastructure.Windows;
using Xunit;
using Xunit.Abstractions;

namespace MacroSuite.Infrastructure.Tests.Windows;

/// <summary>Actual User32 refusal on an inactive owned desktop. Never switches the user's desktop or synthesizes input.</summary>
[Collection("InteractiveDesktop")]
public sealed class ProductionWin32WindowActivationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_restore_unminimizes_hidden_HWND_and_preserves_pinned_maximize_intent(bool maximized)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            nint desktop = 0, window = 0, original = GetThreadDesktop(GetCurrentThreadId());
            try
            {
                desktop = CreateDesktopW("ZenithRestoreTest_" + Guid.NewGuid().ToString("N"), 0, 0, 0, 0x01ff, 0);
                Assert.True(desktop != 0, new Win32Exception(Marshal.GetLastWin32Error()).Message);
                Assert.True(SetThreadDesktop(desktop), new Win32Exception(Marshal.GetLastWin32Error()).Message);
                window = CreateWindowExW(0, "STATIC", "Zenith private native state fixture", 0x10cf0000, 30, 40, 240, 160, 0, 0, 0, 0);
                Assert.NotEqual(0, window);
                if (maximized) ShowWindow(window, 3);
                ShowWindow(window, 6); ShowWindow(window, 0); ShowWindow(window, 5);
                Assert.True(IsIconic(window));
                Assert.False(Win32WindowActivation.IsReady(window));
                // A foreign thread must not mutate or synchronize this thread's HWND.
                Assert.False(Task.Run(() => Win32WindowActivation.RestoreToState(window, maximized)).GetAwaiter().GetResult());
                Assert.True(IsIconic(window));
                Assert.True(Win32WindowActivation.RestoreToState(window, maximized));
                Assert.False(IsIconic(window)); Assert.True(IsWindowVisible(window));
                Assert.True(Win32WindowActivation.IsReady(window)); Assert.Equal(maximized, IsZoomed(window));
                Assert.True(GetWindowRect(window, out var restored));
                Assert.True(restored.Right > restored.Left && restored.Bottom > restored.Top);
                Assert.True(restored.Left > -30000 && restored.Top > -30000);
                output.WriteLine($"Native hidden/iconic state restored on private inactive desktop; maximized intent={maximized}, ready=true. Foreground permission is intentionally not asserted.");
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally
            {
                if (window != 0) DestroyWindow(window);
                SetThreadDesktop(original); if (desktop != 0) CloseDesktop(desktop);
            }
        }) { IsBackground = true };
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)), "The native restore fixture did not shut down.");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inactive_private_desktop_reports_refused_focus_and_preserves_native_topmost(bool topmost)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            nint desktop=0,first=0,second=0,original=GetThreadDesktop(GetCurrentThreadId());
            try
            {
                desktop=CreateDesktopW("ZenithActivationTest_"+Guid.NewGuid().ToString("N"),0,0,0,0x01ff,0);
                Assert.True(desktop!=0,new Win32Exception(Marshal.GetLastWin32Error()).Message);
                Assert.True(SetThreadDesktop(desktop),new Win32Exception(Marshal.GetLastWin32Error()).Message);
                first=CreateWindowExW(0,"STATIC","Zenith private foreground fixture",0x10cf0000,10,10,200,100,0,0,0,0);
                second=CreateWindowExW(0,"STATIC","Zenith private restore fixture",0x10cf0000,20,20,200,100,0,0,0,0);
                Assert.True(first!=0 && second!=0,new Win32Exception(Marshal.GetLastWin32Error()).Message);
                if(topmost) Assert.True(SetWindowPos(second,-1,0,0,0,0,0x13));
                Assert.False(SetForegroundWindow(first),"An inactive private desktop unexpectedly accepted foreground activation; this refusal fixture needs review.");
                SetActiveWindow(first);SetFocus(first);
                bool activated=Win32WindowActivation.TryActivate(second);nint foreground=GetForegroundWindow();
                Assert.Equal(foreground==second,activated);Assert.False(activated);
                Assert.NotEqual(second,foreground);
                // User32 may update this thread's active window even while refusing
                // foreground permission. Only the foreground HWND proves activation.
                Assert.Equal(topmost,(GetWindowLongW(second,-20)&8)!=0);
                output.WriteLine($"Private inactive desktop: foreground accepted={activated}; topmost before/after={topmost}/{(GetWindowLongW(second,-20)&8)!=0}. This verifies actual refusal/cleanup, not positive foreground permission.");
                completion.TrySetResult();
            }
            catch(Exception ex) { completion.TrySetException(ex); }
            finally
            {
                if(second!=0) DestroyWindow(second);if(first!=0) DestroyWindow(first);
                SetThreadDesktop(original);if(desktop!=0) CloseDesktop(desktop);
            }
        });
        // Plain Win32 windows need no COM apartment. STA initialization can create an
        // OLE helper HWND before this delegate and prevent SetThreadDesktop entirely.
        thread.IsBackground=true;thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.True(thread.Join(TimeSpan.FromSeconds(2)),"The private desktop test thread did not shut down.");
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern nint CreateDesktopW(string name,nint device,nint mode,uint flags,uint access,nint attributes);
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetThreadDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern nint CreateWindowExW(uint extendedStyle,string className,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint data);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetActiveWindow();
    [DllImport("user32.dll")] private static extern nint SetActiveWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint window,int index);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window,int command);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRectangle { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window,out NativeRectangle rectangle);
}
