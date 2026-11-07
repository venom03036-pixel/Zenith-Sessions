using System.Buffers.Binary;
using FluentAssertions;
using MacroSuite.Infrastructure.Input;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Input;

public sealed class RawMouseCaptureTests
{
    [Fact]
    public void NativeReceiver_CreatesAndReleasesItsOwnRegistrationWithoutEmittingInput()
    {
        // Exercise the actual Win32 layouts and ownership cleanup on this thread.
        // No hooks, message loop, synthesized input, or other application are used.
        int movements = 0;
        Exception? failure = null;
        for (int i = 0; i < 3; i++)
        {
            using var receiver = new RawMouseCapture((_, _, _) => movements++, error => failure = error);
        }
        movements.Should().Be(0);
        failure.Should().BeNull();
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void NativeParser_PreservesSignedAxes_WithoutCursorCoordinates(int pointerSize)
    {
        var packet = MousePacket(pointerSize, -273, 109);
        RawMousePacketParser.Parse(packet, pointerSize, out int dx, out int dy).Should().Be(RawMousePacketResult.RelativeMovement);
        dx.Should().Be(-273);
        dy.Should().Be(109);
        packet = MousePacket(pointerSize, int.MinValue, int.MaxValue);
        RawMousePacketParser.Parse(packet, pointerSize, out dx, out dy).Should().Be(RawMousePacketResult.RelativeMovement);
        dx.Should().Be(int.MinValue);
        dy.Should().Be(int.MaxValue);
    }

    [Fact]
    public void NativeParser_RejectsAbsoluteDevices_AndMalformedMouseData()
    {
        var packet = MousePacket(8, 32768, 65535);
        packet[24] = 1;
        RawMousePacketParser.Parse(packet, 8, out _, out _).Should().Be(RawMousePacketResult.AbsoluteDevice);
        RawMousePacketParser.Parse(packet[..^1], 8, out _, out _).Should().Be(RawMousePacketResult.Invalid);
        RawMousePacketParser.Parse(packet[..8], 8, out _, out _).Should().Be(RawMousePacketResult.Invalid);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 1000);
        RawMousePacketParser.Parse(packet, 8, out _, out _).Should().Be(RawMousePacketResult.Invalid);
    }

    [Fact]
    public void NativeParser_IgnoresZeroMotion_NonMouseData_AndPlaybackTag()
    {
        RawMousePacketParser.Parse(MousePacket(8, 0, 0), 8, out _, out _).Should().Be(RawMousePacketResult.Ignored);
        var packet = MousePacket(8, 100, -50);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(44), LowLevelHookService.PlaybackSentinelTag);
        RawMousePacketParser.Parse(packet, 8, out _, out _).Should().Be(RawMousePacketResult.Ignored);
        packet[0] = 1;
        RawMousePacketParser.Parse(packet, 8, out _, out _).Should().Be(RawMousePacketResult.Ignored);
    }

    [Fact]
    public void CaptureRouting_UsesExactlyOneMotionSource_AndKeepsButtonsAndWheel()
    {
        LowLevelHookService.ShouldCaptureLegacyMouseMessage(false, 0x0200).Should().BeTrue();
        LowLevelHookService.ShouldCaptureLegacyMouseMessage(true, 0x0200).Should().BeFalse();
        foreach (uint message in new uint[] { 0x0201, 0x0202, 0x0204, 0x0205, 0x020A, 0x020E })
            LowLevelHookService.ShouldCaptureLegacyMouseMessage(true, message).Should().BeTrue();
    }

    [Fact]
    public void Registration_UsesBackgroundMouseOnly_LeavesLegacyDeliveryEnabled_AndReleasesOnce()
    {
        var api = new FakeRegistrationApi();
        var lease = new RawMouseRegistrationLease(api, new IntPtr(42));
        api.Calls.Should().ContainSingle();
        api.Calls[0].UsagePage.Should().Be(1);
        api.Calls[0].Usage.Should().Be(2);
        api.Calls[0].Flags.Should().Be(0x100); // INPUTSINK, not NOLEGACY or CAPTUREMOUSE
        lease.Dispose();
        lease.Dispose();
        api.Calls.Should().HaveCount(2);
        api.Calls[1].Flags.Should().Be(1);
        api.Calls[1].Target.Should().Be(IntPtr.Zero);
        api.Registrations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public void Registration_RefusesExistingMouseOrGenericDesktopOwner(int usage)
    {
        var api = new FakeRegistrationApi { Registrations = [new() { UsagePage = 1, Usage = (ushort)usage, Target = new IntPtr(99) }] };
        Action register = () => _ = new RawMouseRegistrationLease(api, new IntPtr(42));
        register.Should().Throw<InvalidOperationException>().WithMessage("*already owns mouse Raw Input*");
        api.Calls.Should().BeEmpty();
        api.Registrations[0].Target.Should().Be(new IntPtr(99));
    }

    [Fact]
    public void Registration_DoesNotRemoveAReplacementOwner()
    {
        var api = new FakeRegistrationApi();
        using (var lease = new RawMouseRegistrationLease(api, new IntPtr(42)))
            api.Registrations = [new() { UsagePage = 1, Usage = 2, Target = new IntPtr(99) }];
        api.Calls.Should().ContainSingle();
        api.Registrations[0].Target.Should().Be(new IntPtr(99));
    }

    [Fact]
    public void Registration_FailureIsVisible_AndDoesNotClaimOwnership()
    {
        var api = new FakeRegistrationApi { FailRegister = true };
        Action register = () => _ = new RawMouseRegistrationLease(api, new IntPtr(42));
        register.Should().Throw<InvalidOperationException>().WithMessage("native registration failed");
        api.Registrations.Should().BeEmpty();
    }

    [Fact]
    public void RawCapture_DefaultsOff_AndCanBeConfiguredWithoutNativeCapture()
    {
        using var recorder = new LowLevelHookService();
        recorder.UseRawMouseInput.Should().BeFalse();
        recorder.UseRawMouseInput = true;
        recorder.UseRawMouseInput.Should().BeTrue();
        // StartAsync is deliberately not called: this test never installs desktop hooks.
    }

    private static byte[] MousePacket(int pointerSize, int dx, int dy)
    {
        int header = 8 + 2 * pointerSize;
        var bytes = new byte[header + 24];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(header + 12), dx);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(header + 16), dy);
        return bytes;
    }

    private sealed class FakeRegistrationApi : IRawMouseRegistrationApi
    {
        internal RawInputDeviceRegistration[] Registrations = [];
        internal readonly List<RawInputDeviceRegistration> Calls = [];
        internal bool FailRegister;
        public RawInputDeviceRegistration[] GetRegistrations() => Registrations;
        public void Register(RawInputDeviceRegistration device)
        {
            Calls.Add(device);
            if (FailRegister) throw new InvalidOperationException("native registration failed");
            Registrations = device.Flags == 1 ? [] : [device];
        }
    }
}
