using FluentAssertions;
using MacroSuite.App.Services;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Services;

public sealed class TransactionalHotkeyTests
{
    private sealed class Registration : IHotkeyRegistrationApi
    {
        public HashSet<uint> Refused { get; } = [];
        public Dictionary<int, uint> Active { get; } = [];
        public bool Register(IntPtr window, int id, uint modifiers, uint key)
        { if (Refused.Contains(key)) return false; Active[id] = key; return true; }
        public bool Unregister(IntPtr window, int id) => Active.Remove(id);
    }

    [Fact]
    public void FailedRemap_PreservesOldRegistrationsAndCallbacks_AndReleasesTentativeAcquisitions()
    {
        var native = new Registration();
        using var service = new GlobalHotkeyService((IntPtr)1, native);
        var calls = new List<string>();
        service.TryReplaceBindings([new("record", 0, 0x75, () => calls.Add("old"), "Record", true)], out _).Should().BeTrue();
        native.Refused.Add(0x77);
        service.TryReplaceBindings([
            new("play", 0, 0x73, () => calls.Add("new-play"), "Play", true),
            new("record", 0, 0x77, () => calls.Add("new-record"), "Record", true)], out var result).Should().BeFalse();
        result.Last().Availability.Should().Be("Local-only");
        native.Active.Values.Should().Equal(0x75);
        service.Trigger(0, 0x75).Should().BeTrue();
        service.Trigger(0, 0x73).Should().BeFalse();
        calls.Should().Equal("old");
    }

    [Fact]
    public void Swap_ReusesAcquiredCombinations_AndCommitsCallbacksTogether()
    {
        var native = new Registration(); using var service = new GlobalHotkeyService((IntPtr)1, native);
        var calls = new List<string>();
        service.TryReplaceBindings([new("record", 0, 0x75, () => calls.Add("old-record"), "Record"),
            new("stop", 0, 0x77, () => calls.Add("old-stop"), "Stop")], out _).Should().BeTrue();
        service.TryReplaceBindings([new("record", 0, 0x77, () => calls.Add("record"), "Record", true),
            new("stop", 0, 0x75, () => calls.Add("stop"), "Stop", true)], out var states).Should().BeTrue();
        states.Should().OnlyContain(state => state.Availability == "Global");
        native.Active.Should().HaveCount(2);
        service.Trigger(0, 0x77); service.Trigger(0, 0x75);
        calls.Should().Equal("record", "stop");
    }

    [Fact]
    public void StartupFallback_IsExplicitlyLocalOnly_AndCanLaterAcquireGlobalRegistration()
    {
        var native = new Registration(); native.Refused.Add(0x75);
        using var service = new GlobalHotkeyService((IntPtr)1, native);
        var request = new HotkeyBindingRequest("record", 0, 0x75, () => { }, "Record");
        service.TryReplaceBindings([request], out var states).Should().BeTrue();
        states.Single().Availability.Should().Be("Local-only");
        native.Active.Should().BeEmpty();
        native.Refused.Clear();
        service.TryReplaceBindings([request with { RequireGlobal = true }], out states).Should().BeTrue();
        states.Single().Availability.Should().Be("Global");
    }

    [Fact]
    public void DuplicateProposal_NeverChangesEffectiveBindings()
    {
        var native = new Registration(); using var service = new GlobalHotkeyService((IntPtr)1, native);
        service.TryReplaceBindings([new("record", 0, 0x75, () => { }, "Record")], out _).Should().BeTrue();
        service.TryReplaceBindings([new("record", 0, 0x77, () => { }, "Record"), new("stop", 0, 0x77, () => { }, "Stop")], out _).Should().BeFalse();
        native.Active.Values.Should().Equal(0x75);
    }
}
