using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Zenith.Sessions.Capabilities.Infrastructure;

public sealed record ClientFile(string Variant, string VersionDirectory, string Executable, string? FileVersion);
public sealed record ProtocolObservation(string Scheme, string Scope, bool Registered, string? Problem);
public sealed record WindowObservation(string Handle, int ProcessId, bool OwnerRevalidated, bool Visible,
    bool Minimized, bool Foreground, int ClientWidth, int ClientHeight, int ScreenX, int ScreenY);
public sealed record ProcessObservation(int ProcessId, DateTimeOffset? StartedUtc, string ImageName,
    string? FileVersion, bool StillSameProcessInstance, IReadOnlyList<WindowObservation> Windows, string? Problem);
public sealed record EnvironmentObservation(string OperatingSystem, string Architecture, string Runtime,
    bool UserInteractive, IReadOnlyList<ClientFile> ClientFiles, IReadOnlyList<ProtocolObservation> Protocols,
    IReadOnlyList<ProcessObservation> Processes, bool Win32LogDirectoryPresent, IReadOnlyList<string> Problems);

/// <summary>Bounded metadata discovery only. Never reads command lines, logs, credentials or process memory.</summary>
public static class WindowsDiscovery
{
    public static EnvironmentObservation Capture(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return new(RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(),
                RuntimeInformation.FrameworkDescription, Environment.UserInteractive, [], [], [], false,
                ["Windows discovery is unavailable on this operating system."]);
        return CaptureWindows(ct);
    }

    [SupportedOSPlatform("windows")]
    private static EnvironmentObservation CaptureWindows(CancellationToken ct)
    {
        var problems = new List<string>();
        var files = new List<ClientFile>();
        var protocols = new List<ProtocolObservation>();
        var processes = new List<ProcessObservation>();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local) || !Path.IsPathFullyQualified(local))
            return new(RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(),
                RuntimeInformation.FrameworkDescription, Environment.UserInteractive, [], [], [], false,
                ["LocalApplicationData is unavailable; client absence cannot be established."]);
        if (local.Contains("\\Packages\\sandbox.", StringComparison.OrdinalIgnoreCase))
            problems.Add("LocalApplicationData is redirected by process isolation; missing client files here do not establish absence from the real user profile.");
        string versions = Path.Combine(local, "Roblox", "Versions");
        try
        {
            if (Directory.Exists(versions))
            {
                int count = 0;
                foreach (string directory in Directory.EnumerateDirectories(versions))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++count > 128) { problems.Add("Installed-version discovery reached its 128-directory bound."); break; }
                    foreach (string name in new[] { "RobloxPlayerBeta.exe", "RobloxPlayer.exe", "RobloxPlayerLauncher.exe" })
                    {
                        string path = Path.Combine(directory, name);
                        if (File.Exists(path)) files.Add(new("Win32 files (authentication and authenticity unverified)",
                            Path.GetFileName(directory), name, ReadFileVersion(path)));
                    }
                }
            }
        }
        catch (Exception ex) when (IsMetadataFailure(ex)) { problems.Add($"Installed-version discovery unavailable: {ex.GetType().Name}."); }

        foreach (string scheme in new[] { "roblox-player", "roblox" })
        {
            ct.ThrowIfCancellationRequested();
            foreach (bool user in new[] { true, false })
            {
                try
                {
                    using var key = (user ? Registry.CurrentUser : Registry.LocalMachine)
                        .OpenSubKey($"Software\\Classes\\{scheme}\\shell\\open\\command", writable: false);
                    // Existence is enough. Do not read or persist command strings, which are not proof of launch authority.
                    protocols.Add(new(scheme, user ? "CurrentUser" : "LocalMachine", key != null, null));
                }
                catch (Exception ex) when (IsMetadataFailure(ex))
                { protocols.Add(new(scheme, user ? "CurrentUser" : "LocalMachine", false, ex.GetType().Name)); }
            }
        }

        foreach (string name in new[] { "RobloxPlayerBeta", "RobloxPlayer", "Roblox" })
        {
            ct.ThrowIfCancellationRequested();
            Process[] found;
            try { found = Process.GetProcessesByName(name); }
            catch (Exception ex) when (IsMetadataFailure(ex))
            { problems.Add($"Process discovery for {name} unavailable: {ex.GetType().Name}."); continue; }
            try
            {
                foreach (Process process in found)
                {
                    ct.ThrowIfCancellationRequested();
                    if (processes.Count >= 128) { problems.Add("Process discovery reached its 128-process bound."); break; }
                    int id = process.Id;
                    DateTimeOffset? start = null;
                    try
                    {
                        start = process.StartTime.ToUniversalTime();
                        string? image = QueryImagePath(id);
                        var windows = ReadWindows(id);
                        process.Refresh();
                        bool same = !process.HasExited && process.StartTime.ToUniversalTime() == start.Value.UtcDateTime;
                        processes.Add(new(id, start, image == null ? name : Path.GetFileName(image),
                            image == null ? null : ReadFileVersion(image), same, windows, same ? null : "Process exited or changed during discovery."));
                    }
                    catch (Exception ex) when (IsMetadataFailure(ex))
                    { processes.Add(new(id, start, name, null, false, [], ex.GetType().Name)); }
                }
            }
            finally { foreach (Process process in found) process.Dispose(); }
        }
        return new(RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription, Environment.UserInteractive, files, protocols, processes,
            Directory.Exists(Path.Combine(local, "Roblox", "logs")), problems);
    }

    private static bool IsMetadataFailure(Exception ex) => ex is IOException or UnauthorizedAccessException
        or InvalidOperationException or Win32Exception or System.Security.SecurityException;

    private static string? ReadFileVersion(string path)
    {
        try
        {
            string? version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return version is { Length: <= 100 } && version.All(c => char.IsDigit(c) || c is '.' or ',' or ' ')
                ? version : null;
        }
        catch (Exception ex) when (IsMetadataFailure(ex)) { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static string? QueryImagePath(int pid)
    {
        using SafeProcessHandle handle = Native.OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION only.
        if (handle.IsInvalid) return null;
        var text = new StringBuilder(32768);
        int length = text.Capacity;
        return Native.QueryFullProcessImageName(handle, 0, text, ref length) ? text.ToString() : null;
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<WindowObservation> ReadWindows(int pid)
    {
        var windows = new List<WindowObservation>();
        nint foreground = Native.GetForegroundWindow();
        bool completed = Native.EnumWindows((window, _) =>
        {
            Native.GetWindowThreadProcessId(window, out uint owner);
            if (owner != pid || windows.Count >= 64) return true;
            bool hasRect = Native.GetClientRect(window, out var rectangle);
            var origin = new Native.Point();
            bool hasOrigin = Native.ClientToScreen(window, ref origin);
            Native.GetWindowThreadProcessId(window, out uint secondOwner);
            bool valid = Native.IsWindow(window) && secondOwner == owner;
            windows.Add(new($"0x{window:X}", pid, valid, Native.IsWindowVisible(window), Native.IsIconic(window),
                window == foreground, hasRect ? rectangle.Right - rectangle.Left : 0,
                hasRect ? rectangle.Bottom - rectangle.Top : 0, hasOrigin ? origin.X : 0, hasOrigin ? origin.Y : 0));
            return true;
        }, 0);
        if (!completed) throw new Win32Exception(Marshal.GetLastWin32Error());
        return windows;
    }

    // Read-only APIs only; there is intentionally no SendInput, activation, capture or shell-launch import here.
    [SupportedOSPlatform("windows")]
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
        public delegate bool WindowCallback(nint window, nint parameter);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] public static extern bool QueryFullProcessImageName(SafeProcessHandle handle, uint flags, StringBuilder name, ref int size);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool EnumWindows(WindowCallback callback, nint parameter);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsIconic(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetClientRect(nint window, out Rect rectangle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ClientToScreen(nint window, ref Point point);
    }
}
