using Microsoft.Win32.SafeHandles;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

/// <summary>The sound control and its adapter over a fake Core Audio and process API: no audio device or client is touched.</summary>
public sealed class ClientAudioMuteTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static SessionObservation Session(int pid) => new(pid, Now.AddMinutes(-1).UtcTicks, "Synthetic",
        0, "", false, false, 0, 0, null, Now);

    [Fact]
    public async Task MuteChangesEveryOwnSessionAndExitUnmutesOnlyTheClientZenithMuted()
    {
        var native = new Native(); native.Add(1, false, false); native.Add(2, true);
        var control = Control(native);
        var muted = await control.ChangeAsync([Session(1), Session(2)], ClientAudioMuteAction.Mute, default);
        Assert.Equal(new[] { ClientAudioMuteStatus.Applied, ClientAudioMuteStatus.Unchanged }, muted.Results.Select(row => row.Status));
        Assert.Equal(new[] { true, true }, native.Clients[1].Sessions);
        Assert.True(muted.Results[0].RestorePending);
        Assert.Equal(((bool?)false, (bool?)true), (muted.Results[0].Previous, muted.Results[0].Current));
        Assert.False(muted.Results[1].RestorePending);
        Assert.Equal(1, muted.PendingRestoreCount);
        Assert.All(native.Handles, handle => Assert.True(handle.IsClosed));

        await control.DisposeAsync();
        Assert.Equal(new[] { false, false }, native.Clients[1].Sessions);
        Assert.Equal(new[] { true }, native.Clients[2].Sessions); // Already muted before Zenith acted, so Exit leaves it muted.
        Assert.Equal(0, control.PendingRestoreCount);
        Assert.All(native.Handles, handle => Assert.True(handle.IsClosed));
    }

    [Fact]
    public async Task AProcessIdNowUsedByAnotherProcessIsRefusedAndNothingIsMuted()
    {
        var native = new Native(); native.Add(1, false);
        native.Clients[1].Start += TimeSpan.TicksPerSecond; // The PID belongs to a later process than the one observed.
        await using var control = Control(native);
        var row = Assert.Single((await control.ChangeAsync([Session(1)], ClientAudioMuteAction.Mute, default)).Results);
        Assert.Equal(ClientAudioMuteStatus.InvalidClient, row.Status);
        Assert.False(row.RestorePending);
        Assert.Equal(new[] { false }, native.Clients[1].Sessions);
        Assert.Equal(0, native.SetCalls);
    }

    [Fact]
    public async Task AClientThatExitsWhileItsSessionsAreReadIsNotChanged()
    {
        var native = new Native(); native.Add(1, false);
        native.AfterSessionRead = client => client.Running = false;
        await using var control = Control(native);
        var row = Assert.Single((await control.ChangeAsync([Session(1)], ClientAudioMuteAction.Mute, default)).Results);
        Assert.Equal(ClientAudioMuteStatus.InvalidClient, row.Status);
        Assert.Equal(0, native.SetCalls);
        Assert.Equal(0, control.PendingRestoreCount);
    }

    [Fact]
    public async Task AClientWithNoSoundYetOrOnlyPartlyMutedIsNotChanged()
    {
        var native = new Native(); native.Add(1); native.Add(2, true, false);
        await using var control = Control(native);
        var result = await control.ChangeAsync([Session(1), Session(2)], ClientAudioMuteAction.Mute, default);
        Assert.Equal(new[] { ClientAudioMuteStatus.NoAudioSession, ClientAudioMuteStatus.Conflict }, result.Results.Select(row => row.Status));
        Assert.Equal(0, native.SetCalls);
        Assert.Equal(0, result.PendingRestoreCount);
        Assert.Contains("no sound for this client yet", result.Results[0].Explanation);
    }

    [Fact]
    public async Task SoundTurnedOnOutsideZenithEndsOwnershipWithoutAnotherWrite()
    {
        var native = new Native(); native.Add(1, false);
        await using var control = Control(native);
        await control.ChangeAsync([Session(1)], ClientAudioMuteAction.Mute, default);
        native.Clients[1].Sessions[0] = false; // Unmuted in Volume mixer.
        var row = Assert.Single((await control.RestoreAllAsync(default)).Results);
        Assert.Equal(ClientAudioMuteStatus.Unchanged, row.Status);
        Assert.Equal(0, control.PendingRestoreCount);
        Assert.Equal(1, native.SetCalls);
    }

    [Fact]
    public async Task UnmuteTurnsOnAClientZenithDidNotMuteAndExitDoesNotMuteItAgain()
    {
        var native = new Native(); native.Add(1, true); // For example, started muted because Windows remembered a mute.
        var control = Control(native);
        var row = Assert.Single((await control.ChangeAsync([Session(1)], ClientAudioMuteAction.Unmute, default)).Results);
        Assert.Equal(ClientAudioMuteStatus.Applied, row.Status);
        Assert.Equal(((bool?)true, (bool?)false), (row.Previous, row.Current));
        Assert.False(row.RestorePending);
        await control.DisposeAsync();
        Assert.Equal(new[] { false }, native.Clients[1].Sessions);
        Assert.Equal(1, native.SetCalls);
    }

    [Fact]
    public async Task UnmuteOfAClientZenithMutedEndsItsOwnership()
    {
        var native = new Native(); native.Add(1, false);
        await using var control = Control(native);
        await control.ChangeAsync([Session(1)], ClientAudioMuteAction.Mute, default);
        var row = Assert.Single((await control.ChangeAsync([Session(1)], ClientAudioMuteAction.Unmute, default)).Results);
        Assert.Equal(ClientAudioMuteStatus.Applied, row.Status);
        Assert.Equal(0, control.PendingRestoreCount);
        Assert.Equal(new[] { false }, native.Clients[1].Sessions);
    }

    [Fact]
    public async Task AMuteChangedJustBeforeTheWriteIsAPreservedConflict()
    {
        var native = new Native(); native.Add(1, false, false);
        native.BeforeSet = client => client.Sessions[1] = true; // Changed in Volume mixer between the read and the write.
        await using var control = Control(native);
        var row = Assert.Single((await control.ChangeAsync([Session(1)], ClientAudioMuteAction.Mute, default)).Results);
        Assert.Equal(ClientAudioMuteStatus.Conflict, row.Status);
        Assert.Equal(new[] { false, true }, native.Clients[1].Sessions);
        Assert.Equal(0, control.PendingRestoreCount);
    }

    [Fact]
    public async Task StopBeforeTheWriteChangesNothing()
    {
        var native = new Native(); native.Add(1, false);
        await using var control = Control(native);
        native.AfterSessionRead = _ => control.CancelOperations();
        var result = await control.ChangeAsync([Session(1)], ClientAudioMuteAction.Mute, default);
        Assert.Equal(ClientAudioMuteStatus.Cancelled, Assert.Single(result.Results).Status);
        Assert.True(result.Cancelled);
        Assert.Equal(0, native.SetCalls);
        Assert.Equal(new[] { false }, native.Clients[1].Sessions);
    }

    [Fact]
    public async Task ARefusedUnmuteKeepsOwnershipAndExitReportsIt()
    {
        var native = new Native(); native.Add(1, false);
        var control = Control(native);
        await control.ChangeAsync([Session(1)], ClientAudioMuteAction.Mute, default);
        native.DenySet = true;
        var row = Assert.Single((await control.RestoreAllAsync(default)).Results);
        Assert.Equal(ClientAudioMuteStatus.AccessDenied, row.Status);
        Assert.True(row.RestorePending);
        var failure = await Assert.ThrowsAsync<ClientAudioMuteCleanupException>(async () => await control.DisposeAsync());
        Assert.Equal(1, failure.PendingRestoreCount);
        Assert.Equal(new[] { true }, native.Clients[1].Sessions);
    }

    private static WindowsClientAudioMuteControl Control(Native native) => new(new NativeClientAudioMuteAdapter(native));

    internal sealed class Client(int pid, bool[] sessions)
    {
        public int Pid = pid;
        public long Start = Session(pid).ProcessStartUtcTicks;
        public bool Running = true;
        public string Image = @"C:\owned-fixture\RobloxPlayerBeta.exe", User = "S-1-5-21-current";
        public bool[] Sessions = sessions;
    }

    private sealed class Native : IClientAudioNativeApi
    {
        public string? CurrentUserSid => "S-1-5-21-current";
        public readonly Dictionary<int, Client> Clients = [];
        public readonly List<SafeProcessHandle> Handles = [];
        private readonly Dictionary<SafeProcessHandle, Client> _handles = [];
        public Action<Client>? AfterSessionRead, BeforeSet;
        public bool DenySet;
        public int SetCalls;
        public void Add(int pid, params bool[] sessions) => Clients.Add(pid, new(pid, sessions));
        public SafeProcessHandle Open(int pid, out int error)
        {
            error = 0;
            var handle = new SafeProcessHandle((nint)(Handles.Count + 1000), ownsHandle: false);
            Handles.Add(handle);
            _handles.Add(handle, Clients[pid]);
            return handle;
        }
        public bool ReadProcess(SafeProcessHandle process, out ClientAudioProcessSnapshot? snapshot)
        {
            var client = _handles[process];
            snapshot = new(client.Pid, client.Start, client.Running, client.Image, client.User);
            return true;
        }
        public bool[]? ReadSessions(int processId, out int hresult)
        {
            hresult = 0;
            var client = Clients[processId];
            var states = client.Sessions.ToArray();
            AfterSessionRead?.Invoke(client);
            return states;
        }
        public ClientAudioNativeWrite SetSessions(int processId, bool expected, bool desired)
        {
            var client = Clients[processId];
            SetCalls++;
            BeforeSet?.Invoke(client);
            int count = client.Sessions.Length;
            if (client.Sessions.Any(muted => muted != expected)) return new(count, true, 0, 0);
            if (DenySet) return new(count, false, 0, unchecked((int)0x80070005));
            for (int index = 0; index < count; index++) client.Sessions[index] = desired;
            return new(count, false, count, 0);
        }
    }
}
