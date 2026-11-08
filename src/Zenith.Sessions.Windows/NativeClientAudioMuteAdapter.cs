using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

internal sealed record ClientAudioProcessSnapshot(int ProcessId, long StartUtcTicks, bool Running, string ImagePath, string UserSid);

/// <param name="Sessions">How many live audio sessions of the process were found.</param>
/// <param name="Mismatch">True when a session no longer read the expected state, so nothing was written.</param>
/// <param name="Accepted">How many sessions Windows accepted the new state for.</param>
/// <param name="HResult">0, or the Core Audio error that stopped the operation.</param>
internal readonly record struct ClientAudioNativeWrite(int Sessions, bool Mismatch, int Accepted, int HResult);

internal interface IClientAudioNativeApi
{
    string? CurrentUserSid { get; }
    SafeProcessHandle Open(int processId, out int error);
    bool ReadProcess(SafeProcessHandle process, out ClientAudioProcessSnapshot? snapshot);
    /// <summary>The mute state of each live audio session of the process on every active output device; null on a Core Audio error.</summary>
    bool[]? ReadSessions(int processId, out int hresult);
    /// <summary>
    /// Sets every live session of the process to <paramref name="desired"/>, only while every one still reads <paramref name="expected"/>;
    /// a null <paramref name="expected"/> sets every session whatever it reads.
    /// </summary>
    ClientAudioNativeWrite SetSessions(int processId, bool? expected, bool desired);
}

/// <summary>
/// The client's own Windows audio sessions, through the public Core Audio API that Volume mixer uses (IAudioSessionManager2,
/// IAudioSessionControl2.GetProcessId, ISimpleAudioVolume). No client file, setting or memory is read or written. Each operation
/// holds one exact process handle with query-limited and synchronize rights only: while it is open Windows cannot give the PID to
/// another process, so the sessions matched by PID are that instance's own. Identity is checked before and after each read and write.
/// </summary>
internal sealed class NativeClientAudioMuteAdapter : IClientAudioAdapter
{
    private const int AccessDeniedResult = unchecked((int)0x80070005);
    private readonly IClientAudioNativeApi _native;
    private readonly string _clientImage;

    public NativeClientAudioMuteAdapter() : this(new NativeClientAudioApi()) { }
    /// <summary>The production Windows calls for a test-owned helper whose executable is <paramref name="clientImage"/>.</summary>
    internal NativeClientAudioMuteAdapter(string clientImage) : this(new NativeClientAudioApi(), clientImage) { }
    /// <param name="clientImage">The only executable name accepted; tests name their own helper.</param>
    internal NativeClientAudioMuteAdapter(IClientAudioNativeApi native, string clientImage = "RobloxPlayerBeta.exe")
    {
        _native = native;
        _clientImage = clientImage;
    }

    public ClientAudioOpen Open(SessionObservation session)
    {
        string? user = _native.CurrentUserSid;
        if (string.IsNullOrEmpty(user)) return new(ClientAudioMuteStatus.AccessDenied);
        var process = _native.Open(session.ProcessId, out int error);
        if (process.IsInvalid)
        {
            process.Dispose();
            // ERROR_INVALID_PARAMETER: no process exists for that PID. Denied or unknown errors prove no exit.
            return error == 87 ? new(ClientAudioMuteStatus.InvalidClient, Retired: true)
                : new(error == 5 ? ClientAudioMuteStatus.AccessDenied : ClientAudioMuteStatus.NotConfirmed);
        }
        return new(ClientAudioMuteStatus.Unchanged, new Handle(_native, process, session, user, _clientImage));
    }

    private static ClientAudioMuteStatus ErrorStatus(int hresult)
        => hresult == AccessDeniedResult ? ClientAudioMuteStatus.AccessDenied : ClientAudioMuteStatus.NotConfirmed;

    private sealed class Handle(IClientAudioNativeApi native, SafeProcessHandle process, SessionObservation expected,
        string user, string clientImage) : IClientAudioHandle
    {
        /// <summary>Null while the handle is still the exact running client; otherwise the refusal.</summary>
        private ClientAudioRead? Refusal()
        {
            if (!native.ReadProcess(process, out var current) || current is null) return new(ClientAudioMuteStatus.NotConfirmed);
            if (!current.Running || current.ProcessId != expected.ProcessId || current.StartUtcTicks != expected.ProcessStartUtcTicks)
                return new(ClientAudioMuteStatus.InvalidClient, Retired: current.ProcessId == expected.ProcessId
                    && (!current.Running || current.StartUtcTicks > 0 && current.StartUtcTicks != expected.ProcessStartUtcTicks));
            if (!string.Equals(current.UserSid, user, StringComparison.Ordinal)) return new(ClientAudioMuteStatus.AccessDenied);
            if (!string.Equals(Path.GetFileName(current.ImagePath), clientImage, StringComparison.OrdinalIgnoreCase))
                return new(ClientAudioMuteStatus.InvalidClient);
            return null;
        }

        public ClientAudioRead Read()
        {
            if (Refusal() is { } before) return before;
            var sessions = native.ReadSessions(expected.ProcessId, out int hresult);
            // The sessions are matched by PID; this confirms the instance did not exit while they were read.
            if (Refusal() is { } after) return after;
            if (sessions is null) return new(ErrorStatus(hresult));
            if (sessions.Length == 0) return new(ClientAudioMuteStatus.NoAudioSession);
            bool muted = sessions[0];
            for (int index = 1; index < sessions.Length; index++)
                if (sessions[index] != muted) return new(ClientAudioMuteStatus.Conflict);
            return new(ClientAudioMuteStatus.Unchanged, muted);
        }

        public ClientAudioWrite Change(bool? expectedMuted, bool desired, Func<bool> stopped)
        {
            var current = Read();
            bool? now = current.Muted;
            // Turning the sound on also starts from a partly muted client (Read's Conflict) and sets every session on. A mute refuses
            // one, and needs the whole client still in the state the caller read.
            bool partly = !desired && current.Status == ClientAudioMuteStatus.Conflict;
            if (!partly && (current.Status != ClientAudioMuteStatus.Unchanged || now is null))
                return new(false, current.Status, now, current.Retired);
            if (desired ? now != expectedMuted : now is not null && expectedMuted is not null && now != expectedMuted)
                return new(false, ClientAudioMuteStatus.Conflict, now);
            if (stopped()) return new(false, ClientAudioMuteStatus.Cancelled, now);
            // Windows has no compare-and-set for a mute. A mute compares every session again in the same enumeration it writes from;
            // turning the sound on sets every session whatever it reads, so a partly muted client ends all on. Either way all of
            // them are written, so Stop cannot leave the client half changed. A change in between can still race.
            var write = native.SetSessions(expected.ProcessId, desired ? expectedMuted : null, desired);
            bool accepted = write.Accepted > 0;
            if (Refusal() is { } after) return new(accepted, after.Status, Retired: after.Retired);
            if (write.Mismatch) return new(false, ClientAudioMuteStatus.Conflict);
            if (write.Sessions == 0) return new(false, ClientAudioMuteStatus.NoAudioSession);
            if (write.HResult != 0 || write.Accepted != write.Sessions) return new(accepted, ErrorStatus(write.HResult));
            return new(true, ClientAudioMuteStatus.Applied, desired);
        }

        public void Dispose() => process.Dispose();
    }
}

/// <summary>The production Core Audio and process calls behind <see cref="NativeClientAudioMuteAdapter"/>.</summary>
internal sealed class NativeClientAudioApi : IClientAudioNativeApi
{
    // Bounds for one enumeration; more than this is reported as a failure rather than a partial answer.
    private const int MaximumDevices = 64, MaximumSessionsPerDevice = 512;
    private const int RenderFlow = 0, ActiveState = 0x1, AllContexts = 0x17, ExpiredSession = 2; // eRender, DEVICE_STATE_ACTIVE, CLSCTX_ALL.
    private const int NoInterface = unchecked((int)0x80004002), TooMany = unchecked((int)0x80004005); // E_NOINTERFACE, E_FAIL.
    // Passed as the event context of every change, so a Volume mixer listener can tell Zenith's changes apart.
    private static readonly Guid ZenithEventContext = new("3c1f6a52-8d0e-4b7a-9f61-0d2a7e5b9c14");

    public string? CurrentUserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
    }

    public SafeProcessHandle Open(int processId, out int error)
    {
        // Query limited and synchronize only. The sound change goes through the Windows audio service, not this handle.
        var handle = Native.OpenProcess(0x1000 | 0x00100000, false, processId);
        error = Marshal.GetLastWin32Error();
        return handle;
    }

    public bool ReadProcess(SafeProcessHandle process, out ClientAudioProcessSnapshot? snapshot)
    {
        snapshot = null;
        uint pid = Native.GetProcessId(process);
        if (pid == 0 || pid > int.MaxValue || ProcessInstanceIdentity.StartTicks(process) is not { } start) return false;
        uint wait = Native.WaitForSingleObject(process, 0);
        if (wait == 0) { snapshot = new((int)pid, start, false, "", ""); return true; }
        if (wait != 258) return false;
        var image = new StringBuilder(1024);
        uint length = (uint)image.Capacity;
        if (!Native.QueryFullProcessImageName(process, 0, image, ref length)) return false;
        string? sid = ProcessTokenUser.Read(process);
        if (sid is null) return false;
        // A signalled process is conclusively retired. A failed wait remains unknown.
        wait = Native.WaitForSingleObject(process, 0);
        if (wait is not (0 or 258)) return false;
        snapshot = new((int)pid, start, wait == 258, image.ToString(), sid);
        return true;
    }

    public bool[]? ReadSessions(int processId, out int hresult)
    {
        bool[]? states = null;
        hresult = WithSessions(processId, volumes =>
        {
            var read = new bool[volumes.Count];
            for (int index = 0; index < read.Length; index++)
            {
                int result = volumes[index].GetMute(out read[index]);
                if (result != 0) return result;
            }
            states = read;
            return 0;
        });
        return hresult == 0 ? states : null;
    }

    public ClientAudioNativeWrite SetSessions(int processId, bool? expected, bool desired)
    {
        int sessions = 0, accepted = 0;
        bool mismatch = false;
        int hresult = WithSessions(processId, volumes =>
        {
            sessions = volumes.Count;
            if (expected is { } state)
                foreach (var volume in volumes)
                {
                    int read = volume.GetMute(out bool muted);
                    if (read != 0) return read;
                    if (muted != state) { mismatch = true; return 0; }
                }
            var context = ZenithEventContext;
            foreach (var volume in volumes)
            {
                int set = volume.SetMute(desired, ref context);
                if (set != 0) return set;
                accepted++;
            }
            return 0;
        });
        return new(sessions, mismatch, accepted, hresult);
    }

    /// <summary>
    /// Runs <paramref name="body"/> with the volume control of every live (not expired) session that belongs to exactly this
    /// process on every active output device. A session shared by several processes is not the client's own and is skipped.
    /// Every COM object is released before this returns.
    /// </summary>
    private static int WithSessions(int processId, Func<List<CoreAudio.ISimpleAudioVolume>, int> body)
    {
        var owned = new List<object>();
        var volumes = new List<CoreAudio.ISimpleAudioVolume>();
        try
        {
            object created = new CoreAudio.MMDeviceEnumerator();
            owned.Add(created);
            int result = ((CoreAudio.IMMDeviceEnumerator)created).EnumAudioEndpoints(RenderFlow, ActiveState, out var devices);
            if (result != 0) return result;
            owned.Add(devices);
            result = devices.GetCount(out uint deviceCount);
            if (result != 0) return result;
            if (deviceCount > MaximumDevices) return TooMany;
            for (uint device = 0; device < deviceCount; device++)
            {
                result = devices.Item(device, out var endpoint);
                if (result != 0) return result;
                owned.Add(endpoint);
                var managerId = typeof(CoreAudio.IAudioSessionManager2).GUID;
                result = endpoint.Activate(ref managerId, AllContexts, 0, out object manager);
                if (result != 0) return result;
                owned.Add(manager);
                result = ((CoreAudio.IAudioSessionManager2)manager).GetSessionEnumerator(out var sessions);
                if (result != 0) return result;
                owned.Add(sessions);
                result = sessions.GetCount(out int sessionCount);
                if (result != 0) return result;
                if (sessionCount is < 0 or > MaximumSessionsPerDevice) return TooMany;
                for (int index = 0; index < sessionCount; index++)
                {
                    result = sessions.GetSession(index, out object session);
                    if (result != 0) return result;
                    owned.Add(session);
                    if (session is not CoreAudio.IAudioSessionControl2 control) return NoInterface;
                    // AUDCLNT_S_NO_SINGLE_PROCESS is a success code other than S_OK: a shared session is skipped.
                    if (control.GetProcessId(out uint sessionProcess) != 0 || sessionProcess != (uint)processId) continue;
                    result = control.GetState(out int state);
                    if (result != 0) return result;
                    if (state == ExpiredSession) continue;
                    if (session is not CoreAudio.ISimpleAudioVolume volume) return NoInterface;
                    volumes.Add(volume);
                }
            }
            return body(volumes);
        }
        catch (COMException error) { return error.HResult; } // For example, the Windows audio service is not running.
        finally
        {
            for (int index = owned.Count - 1; index >= 0; index--) Marshal.ReleaseComObject(owned[index]);
        }
    }

    /// <summary>
    /// Core Audio declarations, each a prefix of its vtable in Windows SDK order (mmdeviceapi.h, audiopolicy.h); slots that are
    /// never called keep placeholder signatures only to hold their position.
    /// https://learn.microsoft.com/windows/win32/coreaudio/audio-sessions
    /// https://learn.microsoft.com/windows/win32/api/audiopolicy/nn-audiopolicy-iaudiosessionmanager2
    /// https://learn.microsoft.com/windows/win32/api/audiopolicy/nf-audiopolicy-iaudiosessioncontrol2-getprocessid
    /// https://learn.microsoft.com/windows/win32/api/audioclient/nn-audioclient-isimpleaudiovolume
    /// </summary>
    internal static class CoreAudio
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        internal class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid interfaceId, int context, nint activationParameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(nint sessionId, int flags, out nint control); // IAudioSessionManager; unused.
            [PreserveSig] int GetSimpleAudioVolume(nint sessionId, int flags, out nint volume); // IAudioSessionManager; unused.
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioSessionControl2
        {
            [PreserveSig] int GetState(out int state); // IAudioSessionControl.
            [PreserveSig] int GetDisplayName(out nint name); // Unused from here to GetSessionInstanceIdentifier.
            [PreserveSig] int SetDisplayName(nint name, nint context);
            [PreserveSig] int GetIconPath(out nint path);
            [PreserveSig] int SetIconPath(nint path, nint context);
            [PreserveSig] int GetGroupingParam(out Guid grouping);
            [PreserveSig] int SetGroupingParam(nint grouping, nint context);
            [PreserveSig] int RegisterAudioSessionNotification(nint client);
            [PreserveSig] int UnregisterAudioSessionNotification(nint client);
            [PreserveSig] int GetSessionIdentifier(out nint identifier); // IAudioSessionControl2.
            [PreserveSig] int GetSessionInstanceIdentifier(out nint identifier);
            [PreserveSig] int GetProcessId(out uint processId);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, nint context); // Unused.
            [PreserveSig] int GetMasterVolume(out float level); // Unused.
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }
    }

    private static class Native
    {
        // https://learn.microsoft.com/windows/win32/procthread/process-security-and-access-rights
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetProcessId(SafeProcessHandle process);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint length);
    }
}
