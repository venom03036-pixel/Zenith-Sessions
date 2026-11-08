using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>Explicit, bounded EFS operation. Does not read file contents, rewrite records, decrypt or persist a success flag.</summary>
public sealed class WindowsDataFolderEncryption : IDataFolderEncryption
{
    internal const int MaximumEntries = 10000, MaximumDepth = 32;
    private readonly string _root;
    private readonly IFileEncryption _encryption;
    private int _busy;
    public WindowsDataFolderEncryption(string root) : this(root, new Efs()) { }
    internal WindowsDataFolderEncryption(string root, IFileEncryption encryption)
    { _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); _encryption = encryption; }

    public async Task<DataFolderEncryptionResult> EncryptAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return new(DataFolderEncryptionState.Busy, 0, 0, DataFolderEncryptionProblem.None);
        try { return await Task.Run(() => Apply(cancellationToken), CancellationToken.None).ConfigureAwait(false); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private DataFolderEncryptionResult Apply(CancellationToken ct)
    {
        int verified = 0, failed = 0;
        var problem = DataFolderEncryptionProblem.None;
        bool mutated = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            using var rootLease = Pin(_root, directory: true);
            CheckLibraryRoot();
            var paths = Inventory(ct); // Reject unsafe/oversized trees before any mutation.
            foreach (var entry in paths) // Breadth-first: every parent directory precedes its children.
            {
                ct.ThrowIfCancellationRequested();
                rootLease.CheckCurrent();
                try
                {
                    using var held = Pin(entry.Path, entry.Directory);
                    ct.ThrowIfCancellationRequested();
                    held.CheckCurrent();
                    if (!_encryption.IsEncrypted(entry.Path))
                    {
                        // Native EFS calls cannot be interrupted. Cancellation is observed before the next entry.
                        _encryption.Encrypt(entry.Path);
                        mutated = true;
                    }
                    held.CheckCurrent();
                    if (!_encryption.IsEncrypted(entry.Path)) throw new ChangedException();
                    verified++;
                }
                catch (Exception ex) when (Expected(ex))
                {
                    failed++; problem = Classify(ex);
                    if (problem is DataFolderEncryptionProblem.Unsupported or DataFolderEncryptionProblem.UnsafePath)
                        return new(mutated || verified > 0 ? DataFolderEncryptionState.Partial : DataFolderEncryptionState.Unavailable,
                            verified, failed, problem);
                }
            }
            ct.ThrowIfCancellationRequested();
            // Concurrent writers can create/replace entries. Re-inventory and read back; never infer success from a setting.
            verified = 0;
            foreach (var entry in Inventory(ct))
            {
                ct.ThrowIfCancellationRequested();
                rootLease.CheckCurrent();
                using var held = Pin(entry.Path, entry.Directory);
                if (_encryption.IsEncrypted(entry.Path)) verified++;
                else { failed++; problem = DataFolderEncryptionProblem.Changed; }
                held.CheckCurrent();
            }
            ct.ThrowIfCancellationRequested();
            rootLease.CheckCurrent();
            return new(failed == 0 ? DataFolderEncryptionState.Verified : DataFolderEncryptionState.Partial, verified, failed, problem);
        }
        catch (OperationCanceledException)
        { return new(DataFolderEncryptionState.Stopped, verified, failed, problem); }
        catch (Exception ex) when (Expected(ex))
        {
            problem = Classify(ex);
            return new(mutated || verified > 0 ? DataFolderEncryptionState.Partial :
                problem is DataFolderEncryptionProblem.UnsafePath or DataFolderEncryptionProblem.Limit ? DataFolderEncryptionState.Rejected : DataFolderEncryptionState.Unavailable,
                verified, failed + 1, problem);
        }
    }

    private void CheckLibraryRoot()
    {
        string canonical = Path.TrimEndingDirectorySeparator(DirectoryIdentity.Canonical(_root));
        string? drive = Path.GetPathRoot(canonical);
        if (drive is null || canonical == drive || canonical.StartsWith(@"\\", StringComparison.Ordinal)
            || Native.GetDriveType(drive) is not (2 or 3)) throw new UnsafePathException();
        // The operation may target an explicitly selected library, never a whole drive/profile/system-data folder.
        foreach (var special in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.DesktopDirectory })
        {
            string protectedPath = Environment.GetFolderPath(special);
            if (protectedPath.Length == 0) continue;
            protectedPath = DirectoryIdentity.Canonical(protectedPath);
            if (canonical.Equals(Path.TrimEndingDirectorySeparator(protectedPath), StringComparison.OrdinalIgnoreCase)
                || protectedPath.StartsWith(canonical + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new UnsafePathException();
        }
        using var user = WindowsIdentity.GetCurrent();
        var owner = new DirectoryInfo(_root).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
        if (user.User is null || !user.User.Equals(owner)) throw new UnsafePathException();
        // Initialized library marker; inspect only type/identity, not the database contents.
        using var database = Pin(Path.Combine(_root, "accounts.db"), directory: false);
    }

    private List<Entry> Inventory(CancellationToken ct)
    {
        var entries = new List<Entry> { new(_root, true, 0) };
        for (int i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var parent = entries[i];
            if (!parent.Directory) continue;
            using var held = Pin(parent.Path, directory: true);
            foreach (string child in Directory.EnumerateFileSystemEntries(parent.Path))
            {
                ct.ThrowIfCancellationRequested();
                if (entries.Count == MaximumEntries || parent.Depth == MaximumDepth) throw new LimitException();
                string full = Path.GetFullPath(child);
                if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new UnsafePathException();
                var attributes = File.GetAttributes(full);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new UnsafePathException();
                bool directory = (attributes & FileAttributes.Directory) != 0;
                using var childLease = Pin(full, directory);
                entries.Add(new(full, directory, parent.Depth + 1));
            }
        }
        return entries;
    }

    // Open ancestors and target without following reparse points; compare identities around the path-based EFS call.
    // These attribute-only handles do NOT prevent renames. EncryptFile requires exclusive access, so a data-read lease
    // would prevent encryption. This detects ordinary replacement, not an actively racing same-user attacker.
    // Reject hard links too: encrypting one would also alter data reached outside the selected root.
    private static PathLease Pin(string path, bool directory)
    {
        string? drive = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(drive) || drive.StartsWith(@"\\", StringComparison.Ordinal) || path == drive)
            throw new UnsafePathException();
        var lease = new PathLease();
        try
        {
            var chain = new Stack<string>();
            for (string? current = path; current is not null; current = Path.GetDirectoryName(current)) chain.Push(current);
            while (chain.TryPop(out string? item))
            {
                bool isDirectory = chain.Count > 0 || directory;
                var handle = Native.CreateFile(item, 0x80, 3, 0, 3, 0x00200000 | 0x02000000, 0);
                if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
                lease.Handles.Add(handle);
                if (!Native.GetFileInformationByHandle(handle, out var information)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if ((information.Attributes & (uint)FileAttributes.ReparsePoint) != 0
                    || ((information.Attributes & (uint)FileAttributes.Directory) != 0) != isDirectory
                    || !isDirectory && information.Links != 1) throw new UnsafePathException();
                lease.Entries.Add((item, isDirectory, information));
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static bool Expected(Exception ex) => ex is IOException or UnauthorizedAccessException or Win32Exception
        or PlatformNotSupportedException or NotSupportedException or System.Security.SecurityException;
    private static DataFolderEncryptionProblem Classify(Exception ex) => ex switch
    {
        UnsafePathException => DataFolderEncryptionProblem.UnsafePath,
        LimitException => DataFolderEncryptionProblem.Limit,
        ChangedException => DataFolderEncryptionProblem.Changed,
        PlatformNotSupportedException or NotSupportedException => DataFolderEncryptionProblem.Unsupported,
        Win32Exception { NativeErrorCode: 1 or 50 or 120 or 6004 or 6015 } => DataFolderEncryptionProblem.Unsupported,
        UnauthorizedAccessException or System.Security.SecurityException => DataFolderEncryptionProblem.AccessOrSharing,
        Win32Exception { NativeErrorCode: 5 or 32 or 33 } => DataFolderEncryptionProblem.AccessOrSharing,
        IOException io when (io.HResult & 0xffff) is 5 or 32 or 33 => DataFolderEncryptionProblem.AccessOrSharing,
        _ => DataFolderEncryptionProblem.Failure
    };
    private sealed record Entry(string Path, bool Directory, int Depth);
    private sealed class UnsafePathException : IOException;
    private sealed class LimitException : IOException;
    private sealed class ChangedException : IOException;
    private sealed class PathLease : IDisposable
    {
        internal List<SafeFileHandle> Handles { get; } = [];
        internal List<(string Path, bool Directory, Native.FileInformation Identity)> Entries { get; } = [];
        internal void CheckCurrent()
        {
            foreach (var entry in Entries)
            {
                using var current = Native.CreateFile(entry.Path, 0x80, 3, 0, 3, 0x00200000 | 0x02000000, 0);
                if (current.IsInvalid || !Native.GetFileInformationByHandle(current, out var information)) throw new ChangedException();
                if ((information.Attributes & (uint)FileAttributes.ReparsePoint) != 0
                    || ((information.Attributes & (uint)FileAttributes.Directory) != 0) != entry.Directory
                    || !entry.Directory && information.Links != 1) throw new UnsafePathException();
                if (entry.Identity.VolumeSerial != information.VolumeSerial || entry.Identity.IndexHigh != information.IndexHigh
                    || entry.Identity.IndexLow != information.IndexLow) throw new ChangedException();
            }
        }
        public void Dispose() { for (int i = Handles.Count - 1; i >= 0; i--) Handles[i].Dispose(); }
    }
    internal interface IFileEncryption { bool IsEncrypted(string path); void Encrypt(string path); }
    private sealed class Efs : IFileEncryption
    {
        public bool IsEncrypted(string path) => (File.GetAttributes(path) & FileAttributes.Encrypted) != 0;
        public void Encrypt(string path) => File.Encrypt(path);
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct FileInformation
        { public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
        [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetDriveType(string root);
    }
}
