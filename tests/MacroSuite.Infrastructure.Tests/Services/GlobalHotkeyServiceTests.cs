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

            // Whether Windows grants Alt+Shift+F10 depends on the other apps running on this PC:
            // Success when it is free, LocalOnly when another app already owns it. Either way,
            // Unregister must free the combo here and release any global registration, so the
            // second attempt gets the same result as the first.
            var res1 = service.TryRegister(NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT, 0x79, () => { }, "First");
            res1.Status.Should().BeOneOf(HotkeyRegistrationStatus.Success, HotkeyRegistrationStatus.LocalOnly);
            service.Unregister(res1.HotkeyId).Should().BeTrue();

            var res2 = service.TryRegister(NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT, 0x79, () => { }, "Second");
            res2.Status.Should().Be(res1.Status);
        });
    }

    private sealed class FakeRegistration : IHotkeyRegistrationApi
    {
        public HashSet<uint> Refused { get; } = [];
        public Dictionary<int, uint> Active { get; } = [];
        public List<int> Released { get; } = [];
        public bool Register(IntPtr window, int id, uint modifiers, uint key)
        { if (Refused.Contains(key)) return false; Active[id] = key; return true; }
        public bool Unregister(IntPtr window, int id) { Released.Add(id); return Active.Remove(id); }
    }

    [Theory]
    [InlineData(true, HotkeyRegistrationStatus.Success)]
    [InlineData(false, HotkeyRegistrationStatus.LocalOnly)]
    public void Unregister_ReleasesOnlyWhatWindowsGranted_AndReRegistrationGetsTheSameAvailability(
        bool windowsGrantsCombo, HotkeyRegistrationStatus expected)
    {
        var native = new FakeRegistration();
        if (!windowsGrantsCombo) native.Refused.Add(0x79);
        using var service = new GlobalHotkeyService((IntPtr)1, native);
        const uint modifiers = NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT;

        var first = service.TryRegister(modifiers, 0x79, () => { }, "First");
        first.Status.Should().Be(expected);
        service.Unregister(first.HotkeyId).Should().BeTrue("the binding was removed from this app");

        var second = service.TryRegister(modifiers, 0x79, () => { }, "Second");
        second.Status.Should().Be(expected);
        service.UnregisterAll();

        native.Active.Should().BeEmpty();
        native.Released.Should().Equal(windowsGrantsCombo ? [first.HotkeyId, second.HotkeyId] : Array.Empty<int>(),
            "only registrations Windows granted are released");
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
