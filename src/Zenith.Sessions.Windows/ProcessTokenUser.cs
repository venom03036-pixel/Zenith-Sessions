using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Zenith.Sessions.Windows;

/// <summary>Reads the user SID of an already-open process handle with TOKEN_QUERY only.</summary>
internal static class ProcessTokenUser
{
    public static string? Read(SafeProcessHandle process)
    {
        if (!Native.OpenProcessToken(process, 0x0008, out var token)) return null; // TOKEN_QUERY only.
        using (token)
        {
            Native.GetTokenInformation(token, 1, 0, 0, out uint bytes); // TokenUser.
            if (bytes is 0 or > 65536) return null;
            nint buffer = Marshal.AllocHGlobal((int)bytes);
            try
            {
                if (!Native.GetTokenInformation(token, 1, buffer, bytes, out uint written) || written > bytes) return null;
                nint sid = Marshal.ReadIntPtr(buffer);
                return sid == 0 ? null : new SecurityIdentifier(sid).Value;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    private static class Native
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, nint buffer, uint bytes, out uint written);
    }
}
