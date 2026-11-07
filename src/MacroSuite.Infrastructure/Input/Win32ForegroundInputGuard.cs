using System.Diagnostics;
using System.Runtime.InteropServices;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Input;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.Infrastructure.Input;

/// <summary>
/// Cached foreground-process safety check for the single input pipeline (Rule 3, Rule 4, Rule 7).
/// Lookup failures fail open so games and anti-cheat-protected titles are not blocked.
/// Known shells, scripting hosts, and higher-integrity windows fail closed.
/// </summary>
public sealed class Win32ForegroundInputGuard : IForegroundInputGuard
{
    private static readonly long CacheDurationTicks = 50L * Stopwatch.Frequency / 1000L;

    private readonly object _cacheLock = new();
    private readonly int _selfIntegrityRid;
    private long _cacheTicks;
    private bool _cacheAllowed = true;
    private string? _cacheReason;
    private uint _evalPid;
    private bool _evalPidValid;

    public Win32ForegroundInputGuard()
    {
        _selfIntegrityRid = TryReadIntegrityRid(NativeMethods.GetCurrentProcess(), ownsProcessHandle: false, out int rid)
            ? rid
            : ForegroundProcessPolicy.MediumIntegrityRid;
    }

    public bool TryAllowSynthesizedInput(out string? refusalReason)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_cacheLock)
        {
            if (now - _cacheTicks < CacheDurationTicks)
            {
                refusalReason = _cacheReason;
                return _cacheAllowed;
            }

            bool allowed = Evaluate(out refusalReason);
            _cacheTicks = now;
            _cacheAllowed = allowed;
            _cacheReason = refusalReason;
            return allowed;
        }
    }

    private bool Evaluate(out string? refusalReason)
    {
        refusalReason = null;
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            _evalPidValid = false;
            return true;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
        {
            _evalPidValid = false;
            return true;
        }

        if (_evalPidValid && pid == _evalPid)
        {
            refusalReason = _cacheReason;
            return _cacheAllowed;
        }

        IntPtr process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
        {
            _evalPidValid = false;
            return true;
        }

        try
        {
            string? processName = QueryProcessName(process);
            if (ForegroundProcessPolicy.IsBlockedProcessName(processName))
            {
                refusalReason =
                    $"Refusing synthesized input because the foreground process '{processName}' is a blocked shell or admin tool.";
                _evalPid = pid;
                _evalPidValid = true;
                return false;
            }

            if (TryReadIntegrityRid(process, ownsProcessHandle: true, out int rid)
                && ForegroundProcessPolicy.IsHigherIntegrity(rid, _selfIntegrityRid))
            {
                refusalReason =
                    "Refusing synthesized input because the foreground window is at a higher integrity level.";
                _evalPid = pid;
                _evalPidValid = true;
                return false;
            }

            _evalPid = pid;
            _evalPidValid = true;
            return true;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    private static string? QueryProcessName(IntPtr process)
    {
        Span<char> buffer = stackalloc char[1024];
        uint size = (uint)buffer.Length;
        if (!NativeMethods.QueryFullProcessImageNameW(process, 0, buffer, ref size) || size == 0)
            return null;

        int length = (int)size;
        if (length > buffer.Length)
            length = buffer.Length;

        ReadOnlySpan<char> path = buffer[..length];
        int slash = path.LastIndexOf('\\');
        ReadOnlySpan<char> file = slash >= 0 ? path[(slash + 1)..] : path;
        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            file = file[..^4];
        return file.IsEmpty ? null : file.ToString();
    }

    private static bool TryReadIntegrityRid(IntPtr processHandle, bool ownsProcessHandle, out int rid)
    {
        _ = ownsProcessHandle;
        rid = 0;
        if (!NativeMethods.OpenProcessToken(processHandle, NativeMethods.TOKEN_QUERY, out IntPtr token))
            return false;

        try
        {
            NativeMethods.GetTokenInformation(
                token,
                NativeMethods.TokenIntegrityLevel,
                IntPtr.Zero,
                0,
                out uint length);
            if (length == 0)
                return false;

            IntPtr buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!NativeMethods.GetTokenInformation(
                        token,
                        NativeMethods.TokenIntegrityLevel,
                        buffer,
                        length,
                        out _))
                {
                    return false;
                }

                IntPtr sid = Marshal.ReadIntPtr(buffer);
                if (sid == IntPtr.Zero)
                    return false;

                IntPtr countPtr = NativeMethods.GetSidSubAuthorityCount(sid);
                if (countPtr == IntPtr.Zero)
                    return false;

                byte count = Marshal.ReadByte(countPtr);
                if (count == 0)
                    return false;

                IntPtr ridPtr = NativeMethods.GetSidSubAuthority(sid, (uint)(count - 1));
                if (ridPtr == IntPtr.Zero)
                    return false;

                rid = Marshal.ReadInt32(ridPtr);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }
}
