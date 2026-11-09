using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Zenith.MacroHost.Contracts;

namespace Zenith.MacroHost.Windows;

/// <summary>Authenticates the exact packaged Sessions process and its live direct player child.</summary>
public sealed class ForegroundPeerOwnership
{
    private readonly byte[] _peerDigest;

    public ForegroundPeerOwnership(string peerSha256)
    {
        if (peerSha256.Length != 64) throw new ArgumentException("A SHA-256 build identity is required.");
        _peerDigest = Convert.FromHexString(peerSha256);
    }

    public bool Authorizes(int peerId, ForegroundControlRequest request, CancellationToken ct)
    {
        if (peerId <= 0 || !request.IsValid || request.ProcessId == peerId || request.Window > nint.MaxValue)
            return false;
        using var peer = OpenProcess(0x00101000, false, (uint)peerId); // SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION
        using var child = OpenProcess(0x00101000, false, (uint)request.ProcessId);
        if (!Live(peer) || !Live(child) || !GetProcessTimes(peer, out long peerBirth, out _, out _, out _)
            || !GetProcessTimes(child, out long childBirth, out _, out _, out _)
            || childBirth != request.CreationFileTime || peerBirth <= 0 || peerBirth > childBirth
            || !WindowBelongsTo(request) || ParentOf(request.ProcessId) != peerId
            || !string.Equals(Path.GetFileName(Image(child)), "RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase))
            return false;
        ct.ThrowIfCancellationRequested();
        var path = Image(peer);
        if (path is null || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            return false;
        try
        {
            using var image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (image.Length is < 1 or > 64 * 1024 * 1024) return false;
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(image), _peerDigest)) return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        ct.ThrowIfCancellationRequested();
        return Live(peer) && Live(child) && WindowBelongsTo(request);
    }

    private static bool Live(SafeProcessHandle process) => !process.IsInvalid && WaitForSingleObject(process, 0) == 0x102;

    private static string? Image(SafeProcessHandle process)
    {
        var path = new StringBuilder(32768);
        uint length = (uint)path.Capacity;
        return QueryFullProcessImageName(process, 0, path, ref length) && length > 0 ? path.ToString() : null;
    }

    private static bool WindowBelongsTo(ForegroundControlRequest request)
    {
        var window = (nint)request.Window;
        return GetWindowThreadProcessId(window, out uint pid) != 0 && pid == request.ProcessId
            && IsWindowVisible(window) && !IsIconic(window);
    }

    private static int ParentOf(int pid)
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        var row = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Exe = "" };
        if (snapshot.IsInvalid || !Process32First(snapshot, ref row)) return 0;
        do
        {
            if (row.ProcessId == pid) return row.ParentId <= int.MaxValue ? (int)row.ParentId : 0;
        } while (Process32Next(snapshot, ref row));
        return 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint DefaultHeap;
        public uint ModuleId, Threads, ParentId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint process);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint process);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry row);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry row);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
}
