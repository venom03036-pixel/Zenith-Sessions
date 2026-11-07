using MacroSuite.Infrastructure.Windows;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Windows;

/// <summary>Source-only policy tests; the native fixture below uses only the retained public API.</summary>
public sealed class Win32WindowActivationTests
{
    private static readonly nint Target=42;
    [Fact]
    public void Invalid_or_foreign_thread_window_never_receives_focus_work()
    {
        var api=new FakeApi();Assert.False(Win32WindowActivation.TryActivate(0,api));Assert.Empty(api.Calls);
        api.Owned=false;Assert.False(Win32WindowActivation.TryActivate(Target,api));Assert.Equal(["owned"],api.Calls);
    }
    [Fact]
    public void Foreground_acceptance_focuses_only_the_owned_window_without_changing_topmost()
    {
        var api=new FakeApi { AcceptRequest=1 };Assert.True(Win32WindowActivation.TryActivate(Target,api));
        Assert.Equal(["owned","foreground-request","active-focus"],api.Calls);
    }
    [Fact]
    public void Already_foreground_window_only_restores_active_keyboard_focus()
    {
        var api=new FakeApi { ForegroundWindow=Target };Assert.True(Win32WindowActivation.TryActivate(Target,api));
        Assert.Equal(["owned","active-focus"],api.Calls);
    }
    [Fact]
    public void Denied_request_raises_once_restores_non_topmost_and_does_not_claim_focus()
    {
        var api=new FakeApi();Assert.False(Win32WindowActivation.TryActivate(Target,api));
        Assert.False(api.Topmost);Assert.Equal(["owned","foreground-request","read-topmost","topmost-on","foreground-request","topmost-off"],api.Calls);
    }
    [Fact]
    public void Accepted_fallback_clears_only_its_temporary_topmost_state_before_focusing()
    {
        var api=new FakeApi { AcceptRequest=2 };Assert.True(Win32WindowActivation.TryActivate(Target,api));Assert.False(api.Topmost);
        Assert.Equal("topmost-off",api.Calls[^2]);Assert.Equal("active-focus",api.Calls[^1]);
    }
    [Fact]
    public void Existing_topmost_preference_survives_foreground_fallback()
    {
        var api=new FakeApi { Topmost=true,AcceptRequest=2 };Assert.True(Win32WindowActivation.TryActivate(Target,api));
        Assert.True(api.Topmost);Assert.DoesNotContain("topmost-off",api.Calls);
    }
    [Fact]
    public void Failed_fallback_unwinds_temporary_topmost_even_when_request_throws()
    {
        var api=new FakeApi { ThrowRequest=2 };Assert.Throws<IOException>(()=>Win32WindowActivation.TryActivate(Target,api));
        Assert.False(api.Topmost);Assert.Equal("topmost-off",api.Calls[^1]);Assert.DoesNotContain("active-focus",api.Calls);
    }
    [Fact]
    public void Foreground_must_still_belong_to_target_after_focus_operation()
    {
        var api=new FakeApi { AcceptRequest=1,LoseForegroundDuringFocus=true };Assert.False(Win32WindowActivation.TryActivate(Target,api));
        Assert.Contains("active-focus",api.Calls);
    }
    private sealed class FakeApi : Win32WindowActivation.IWindowActivationApi
    {
        public readonly List<string> Calls=[];
        public bool Owned=true,Topmost,LoseForegroundDuringFocus;
        public int AcceptRequest,ThrowRequest;
        private int _requests;
        public nint ForegroundWindow { get; set; }=17;
        public bool IsOwnedByCurrentThread(nint window) { Assert.Equal(Target,window);Calls.Add("owned");return Owned; }
        public bool IsTopmost(nint window) { Assert.Equal(Target,window);Calls.Add("read-topmost");return Topmost; }
        public void RequestForeground(nint window)
        {
            Assert.Equal(Target,window);Calls.Add("foreground-request");_requests++;
            if(_requests==ThrowRequest) throw new IOException("Synthetic activation failure.");
            if(_requests==AcceptRequest) ForegroundWindow=window;
        }
        public void SetTopmost(nint window,bool value) { Assert.Equal(Target,window);Topmost=value;Calls.Add(value?"topmost-on":"topmost-off"); }
        public void SetActiveAndFocus(nint window) { Assert.Equal(Target,window);Calls.Add("active-focus");if(LoseForegroundDuringFocus) ForegroundWindow=17; }
    }
}
