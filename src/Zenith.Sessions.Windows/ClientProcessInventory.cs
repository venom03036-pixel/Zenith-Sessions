using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>One client process from a system process snapshot. The sizes are the snapshot's own values.</summary>
/// <param name="NameIndex">Index of the matched client name in the source's name list.</param>
internal readonly record struct ClientProcessEntry(int ProcessId, int NameIndex, long VirtualBytes, long WorkingSetBytes, long PrivateBytes);

internal enum ClientProcessReadOutcome { Unavailable, Exited, Read }

/// <summary>What the sampler read from one client process through one handle.</summary>
/// <param name="StartUtcTicks">The process start time in UTC ticks; 0 when it was not read.</param>
internal readonly record struct ClientProcessReading(ClientProcessReadOutcome Outcome, long StartUtcTicks = 0,
    TimeSpan ProcessorTime = default, ProcessMemoryReading? Memory = null, ProcessIoCounters? Io = null)
{
    public static ClientProcessReading Unavailable => new(ClientProcessReadOutcome.Unavailable);
}

/// <summary>The native process reads of one process sample. The native source is the only implementation outside tests.</summary>
internal interface IClientProcessSource
{
    /// <summary>
    /// Takes one system process snapshot and appends every process whose image name is a client name, in snapshot order.
    /// Throws <see cref="InvalidOperationException"/> or <see cref="Win32Exception"/> when Windows' process list is unavailable.
    /// </summary>
    void Snapshot(List<ClientProcessEntry> clients, CancellationToken ct);

    /// <summary>Opens one query-limited handle to the process and reads its start time, processor time, memory, I/O and exit state.</summary>
    ClientProcessReading Read(int processId, CancellationToken ct);
}

/// <summary>
/// One NtQuerySystemInformation process snapshot per sample (the call Process.GetProcessesByName makes once per name) and one
/// PROCESS_QUERY_LIMITED_INFORMATION handle per client. Read-only: no process memory contents, no window and no message.
/// The caller serializes calls. The snapshot buffer is owned here, reused between samples and capped.
/// </summary>
internal sealed class NativeClientProcessSource : IClientProcessSource
{
    private const int SystemProcessInformation = 5, InitialBufferBytes = 512 * 1024, MaximumBufferBytes = 64 * 1024 * 1024, Attempts = 4;
    private const int InfoLengthMismatch = unchecked((int)0xC0000004);
    private const uint QueryLimitedInformation = 0x1000, StillActive = 259;
    private static readonly int EntryBytes = Marshal.SizeOf<Native.ProcessInformation>();
    private readonly string[] _names;
    private readonly int _initialBufferBytes;
    private byte[] _buffer = [];

    public NativeClientProcessSource(params string[] names) : this(InitialBufferBytes, names) { }

    /// <summary>Test seam: a small <paramref name="initialBufferBytes"/> exercises the grow-and-retry path.</summary>
    internal NativeClientProcessSource(int initialBufferBytes, params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentOutOfRangeException.ThrowIfLessThan(initialBufferBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(initialBufferBytes, MaximumBufferBytes);
        if (names.Length == 0 || Array.Exists(names, string.IsNullOrWhiteSpace))
            throw new ArgumentException("Client process names must be non-empty.", nameof(names));
        _names = (string[])names.Clone();
        _initialBufferBytes = initialBufferBytes;
    }

    public void Snapshot(List<ClientProcessEntry> clients, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ct.ThrowIfCancellationRequested();
        int length = Query();
        ct.ThrowIfCancellationRequested();
        // The array is pinned, so the image-name pointers Windows wrote stay valid offsets from its first element.
        nint origin = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, 0);
        ReadOnlySpan<byte> data = _buffer.AsSpan(0, length);
        int offset = 0;
        while (true)
        {
            if (data.Length - offset < EntryBytes) throw new InvalidOperationException("Windows returned an incomplete process list.");
            var entry = MemoryMarshal.Read<Native.ProcessInformation>(data[offset..]);
            int nameIndex = NameIndex(data, origin, entry.ImageName);
            long processId = entry.UniqueProcessId;
            if (nameIndex >= 0 && processId is > 0 and <= int.MaxValue)
                clients.Add(new((int)processId, nameIndex, Bytes(entry.VirtualSize), Bytes(entry.WorkingSetSize), Bytes(entry.PrivatePageCount)));
            if (entry.NextEntryOffset == 0) return;
            if (entry.NextEntryOffset >= (uint)(data.Length - offset)) throw new InvalidOperationException("Windows returned an incomplete process list.");
            offset += (int)entry.NextEntryOffset;
        }
    }

    public ClientProcessReading Read(int processId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (processId <= 0) return ClientProcessReading.Unavailable;
        // Microsoft documents PROCESS_QUERY_LIMITED_INFORMATION for GetProcessId, GetExitCodeProcess, GetProcessTimes,
        // GetProcessMemoryInfo and GetProcessIoCounters. The open handle pins this process instance, so every read below
        // belongs to the same process and its start time cannot change while it is held.
        using var handle = Native.OpenProcess(QueryLimitedInformation, false, processId);
        if (handle.IsInvalid || Native.GetProcessId(handle) != (uint)processId) return ClientProcessReading.Unavailable;
        if (!Running(handle)) return new(ClientProcessReadOutcome.Exited);
        // Exit FILETIME is undefined for a running process, so it is not read.
        if (!Native.GetProcessTimes(handle, out long created, out _, out long kernel, out long user)
            || created <= 0 || created > DateTime.MaxValue.Ticks - DateTime.FromFileTimeUtc(0).Ticks
            || kernel < 0 || user < 0 || kernel > long.MaxValue - user) return ClientProcessReading.Unavailable;
        long start = DateTime.FromFileTimeUtc(created).Ticks;
        ct.ThrowIfCancellationRequested();
        var memory = ProcessMemoryReader.ReadCounters(handle);
        var io = ProcessIoReader.Read(handle);
        ct.ThrowIfCancellationRequested();
        // A client that ended during the reads gives no row, as the earlier Refresh/HasExited re-check did.
        return Running(handle) ? new(ClientProcessReadOutcome.Read, start, TimeSpan.FromTicks(kernel + user), memory, io)
            : new(ClientProcessReadOutcome.Exited, start);
    }

    private static bool Running(SafeProcessHandle handle) => Native.GetExitCodeProcess(handle, out uint code) && code == StillActive;

    private int Query()
    {
        if (_buffer.Length == 0) _buffer = GC.AllocateUninitializedArray<byte>(_initialBufferBytes, pinned: true);
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            int status = Native.NtQuerySystemInformation(SystemProcessInformation, Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, 0),
                (uint)_buffer.Length, out uint needed);
            if (status >= 0) return needed is > 0 && needed <= (uint)_buffer.Length ? (int)needed : _buffer.Length;
            if (status != InfoLengthMismatch) throw new Win32Exception(unchecked((int)Native.RtlNtStatusToDosError(status)));
            // The list can grow between the two calls: leave headroom so the retry normally fits.
            long next = needed > (uint)_buffer.Length ? needed + needed / 4L : _buffer.Length * 2L;
            if (next > MaximumBufferBytes) throw new InvalidOperationException("Windows' process list is larger than the sampling limit.");
            _buffer = GC.AllocateUninitializedArray<byte>((int)next, pinned: true);
        }
        throw new InvalidOperationException("Windows' process list kept growing while it was read.");
    }

    /// <summary>The name rule of Process.GetProcessesByName: the file name without ".exe", compared ignoring case.</summary>
    private int NameIndex(ReadOnlySpan<byte> data, nint origin, Native.UnicodeString image)
    {
        if (image.Length == 0 || image.Buffer == 0 || (image.Length & 1) != 0) return -1;
        long offset = (long)image.Buffer - origin;
        if (offset < 0 || offset > data.Length - image.Length) return -1;
        ReadOnlySpan<char> name = MemoryMarshal.Cast<byte, char>(data.Slice((int)offset, image.Length));
        name = name[(name.LastIndexOf('\\') + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        for (int index = 0; index < _names.Length; index++)
            if (name.Equals(_names[index], StringComparison.OrdinalIgnoreCase)) return index;
        return -1;
    }

    private static long Bytes(nuint value) => (ulong)value > long.MaxValue ? long.MaxValue : (long)value;

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct UnicodeString { internal ushort Length, MaximumLength; internal nint Buffer; }

        // SYSTEM_PROCESS_INFORMATION as winternl.h documents it; only the named fields are read.
        // https://learn.microsoft.com/windows/win32/api/winternl/nf-winternl-ntquerysysteminformation
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation
        {
            internal uint NextEntryOffset, NumberOfThreads;
            internal long Reserved1A, Reserved1B, Reserved1C, Reserved1D, Reserved1E, Reserved1F; // BYTE Reserved1[48]
            internal UnicodeString ImageName;
            internal int BasePriority;
            internal nint UniqueProcessId, Reserved2;
            internal uint HandleCount, SessionId;
            internal nint Reserved3;
            internal nuint PeakVirtualSize, VirtualSize;
            internal uint Reserved4;
            internal nuint PeakWorkingSetSize, WorkingSetSize;
            internal nint Reserved5;
            internal nuint QuotaPagedPoolUsage;
            internal nint Reserved6;
            internal nuint QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivatePageCount;
            internal long Reserved7A, Reserved7B, Reserved7C, Reserved7D, Reserved7E, Reserved7F; // LARGE_INTEGER Reserved7[6]
        }

        [DllImport("ntdll.dll", ExactSpelling = true)]
        internal static extern int NtQuerySystemInformation(int informationClass, nint information, uint length, out uint returnLength);
        [DllImport("ntdll.dll", ExactSpelling = true)] internal static extern uint RtlNtStatusToDosError(int status);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetProcessId(SafeProcessHandle process);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    }
}
