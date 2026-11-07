using System.Windows.Interop;
using FluentAssertions;
using MacroSuite.App.Services;
using MacroSuite.Infrastructure.Windows;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Services;

public sealed class GlobalHotkeyServiceTests
{
    private static void RunOnStaThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(10000).Should().BeTrue("STA thread timed out.");
        error.Should().BeNull();
    }

    [Fact]
    public void Trigger_DebouncesExactCombination_AndPreservesArrivalOrder()
    {
        RunOnStaThread(() =>
        {
            using var source = new HwndSource(new HwndSourceParameters("DispatchOrder") { Width = 0, Height = 0 });
            using var service = new GlobalHotkeyService(source.Handle);
            var actions = new List<string>();
            service.Register(NativeMethods.MOD_CONTROL, 0x7A, () => actions.Add("play"));
            service.Register(NativeMethods.MOD_ALT, 0x7A, () => actions.Add("stop"));

            service.Trigger(NativeMethods.MOD_CONTROL, 0x7A).Should().BeTrue();
            service.Trigger(NativeMethods.MOD_ALT, 0x7A).Should().BeTrue();
            service.Trigger(NativeMethods.MOD_CONTROL, 0x7A).Should().BeTrue();

            actions.Should().Equal("play", "stop");
        });
    }

    [Fact]
    public void Trigger_ReRegistrationDoesNotInheritDebounce_AndDisposePreventsDispatch()
    {
        RunOnStaThread(() =>
        {
            using var source = new HwndSource(new HwndSourceParameters("DispatchLifetime") { Width = 0, Height = 0 });
            using var service = new GlobalHotkeyService(source.Handle);
            int count = 0;
            int id = service.Register(NativeMethods.MOD_CONTROL, 0x7A, () => count++);
            service.Trigger(NativeMethods.MOD_CONTROL, 0x7A);
            service.Unregister(id);
            service.Register(NativeMethods.MOD_CONTROL, 0x7A, () => count++);
            service.Trigger(NativeMethods.MOD_CONTROL, 0x7A);
            service.Dispose();
            service.Trigger(NativeMethods.MOD_CONTROL, 0x7A).Should().BeFalse();
            count.Should().Be(2);
        });
    }

    [Fact]
    public void FormatHotkeyName_FormatsModifiersAndKeyCorrectly()
    {
        GlobalHotkeyService.FormatHotkeyName(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x41)
            .Should().Be("Ctrl+Alt+A");

        GlobalHotkeyService.FormatHotkeyName(NativeMethods.MOD_SHIFT | NativeMethods.MOD_WIN, 0x71)
            .Should().Be("Shift+Win+F2");
    }

    [Fact]
    public void Construction_WithoutValidHwnd_Throws()
    {
        var act = () => new GlobalHotkeyService(IntPtr.Zero);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TryRegister_DuplicateInternalCombo_DetectsConflictAndFiresEvent()
    {
        RunOnStaThread(() =>
        {
            var parameters = new HwndSourceParameters("TestHwnd") { Width = 0, Height = 0 };
            using var source = new HwndSource(parameters);
            using var service = new GlobalHotkeyService(source.Handle);

            HotkeyRegistrationResult? detectedConflict = null;
            service.HotkeyConflictDetected += res => detectedConflict = res;

            // First registration (F11 = 0x7A, Ctrl = MOD_CONTROL)
            var res1 = service.TryRegister(NativeMethods.MOD_CONTROL, 0x7A, () => { }, "Action 1");

            // Attempt duplicate registration of exact same combo
            var res2 = service.TryRegister(NativeMethods.MOD_CONTROL, 0x7A, () => { }, "Action 2");

            res2.Status.Should().Be(HotkeyRegistrationStatus.ConflictAlreadyBoundInApp);
            res2.ErrorMessage.Should().Contain("Action 1");
            res2.IsSuccess.Should().BeFalse();

            detectedConflict.Should().NotBeNull();
            detectedConflict!.Status.Should().Be(HotkeyRegistrationStatus.ConflictAlreadyBoundInApp);
        });
    }

    [Fact]
    public void Unregister_AllowsReRegistrationOfCombo()
    {
        RunOnStaThread(() =>
        {
            var parameters = new HwndSourceParameters("TestHwnd") { Width = 0, Height = 0 };
            using var source = new HwndSource(parameters);
            using var service = new GlobalHotkeyService(source.Handle);

            var res1 = service.TryRegister(NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT, 0x79, () => { }, "First");
            if (res1.IsSuccess)
            {
                service.Unregister(res1.HotkeyId);

                // Now re-registering should succeed without conflict
                var res2 = service.TryRegister(NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT, 0x79, () => { }, "Second");
                res2.Status.Should().Be(HotkeyRegistrationStatus.Success);
            }
        });
    }

    [Fact]
    public void UnregisterAll_ClearsRegisteredCombos()
    {
        RunOnStaThread(() =>
        {
            var parameters = new HwndSourceParameters("TestHwnd") { Width = 0, Height = 0 };
            using var source = new HwndSource(parameters);
            using var service = new GlobalHotkeyService(source.Handle);

            service.TryRegister(NativeMethods.MOD_CONTROL, 0x76, () => { }, "First");
            service.UnregisterAll();

            // Should be able to register again
            var res = service.TryRegister(NativeMethods.MOD_CONTROL, 0x76, () => { }, "Re-registered");
            res.Status.Should().NotBe(HotkeyRegistrationStatus.ConflictAlreadyBoundInApp);
        });
    }

    [Fact]
    public void Dispose_PreventsFurtherRegistrations()
    {
        RunOnStaThread(() =>
        {
            var parameters = new HwndSourceParameters("TestHwnd") { Width = 0, Height = 0 };
            using var source = new HwndSource(parameters);
            var service = new GlobalHotkeyService(source.Handle);
            service.Dispose();

            var act = () => service.TryRegister(0, 0x70, () => { });
            act.Should().Throw<ObjectDisposedException>();
        });
    }
}
