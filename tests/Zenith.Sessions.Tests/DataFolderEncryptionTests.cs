using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class DataFolderEncryptionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Zenith-Efs-Test-" + Guid.NewGuid().ToString("N"));
    public DataFolderEncryptionTests() { Directory.CreateDirectory(_root); Write("accounts.db"); }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private string Write(string relative)
    { string path = Path.Combine(_root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "synthetic metadata; never a real account"); return path; }

    [Fact]
    public async Task ExplicitApplicationMarksDirectoriesBeforeTheirChildrenAndDoesNotRewriteAnyBytes()
    {
        string file = Write("vault\\record.bin"), database = Write("accounts.db");
        byte[] before = File.ReadAllBytes(file);
        var adapter = new FakeEncryption(); var service = new WindowsDataFolderEncryption(_root, adapter);
        var result = await service.EncryptAsync(default);
        Assert.Equal(DataFolderEncryptionState.Verified, result.State); Assert.Equal(4, result.VerifiedEntries);
        Assert.Equal(_root, adapter.Calls[0]);
        Assert.True(adapter.Calls.IndexOf(Path.GetDirectoryName(file)!) < adapter.Calls.IndexOf(file));
        Assert.Equal(before, File.ReadAllBytes(file)); Assert.True(File.Exists(database));
        await service.EncryptAsync(default); Assert.Equal(4, adapter.Calls.Count);
    }

    [Fact]
    public async Task UnsupportedWindowsIsUnavailableWithoutAStoredSuccessFlag()
    {
        Write("accounts.db");
        var adapter = new FakeEncryption { Before = _ => throw new PlatformNotSupportedException("sensitive path") };
        var result = await new WindowsDataFolderEncryption(_root, adapter).EncryptAsync(default);
        Assert.Equal(DataFolderEncryptionState.Unavailable, result.State); Assert.Equal(DataFolderEncryptionProblem.Unsupported, result.Problem);
        Assert.Equal(0, result.VerifiedEntries); Assert.DoesNotContain("sensitive", result.ToString());
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task SharingFailureLeavesSuccessfulEncryptionInPlaceAndRetryCompletesIt()
    {
        string file = Write("accounts.db");
        var adapter = new FakeEncryption { Before = path => { if (path == file) throw new Win32Exception(32); } };
        var service = new WindowsDataFolderEncryption(_root, adapter);
        var partial = await service.EncryptAsync(default);
        Assert.Equal(DataFolderEncryptionState.Partial, partial.State); Assert.True(partial.FailedEntries > 0);
        Assert.Contains(_root, adapter.Encrypted); Assert.DoesNotContain(file, adapter.Encrypted);
        adapter.Before = null;
        Assert.Equal(DataFolderEncryptionState.Verified, (await service.EncryptAsync(default)).State);
        Assert.Equal(1, adapter.Calls.Count(path => path == _root));
    }

    [Fact]
    public async Task ANoOpOrNewUnencryptedFileCannotReportVerified()
    {
        string file = Write("accounts.db");
        var adapter = new FakeEncryption { Before = path => { if (path == file) Write("new.bin"); } };
        var result = await new WindowsDataFolderEncryption(_root, adapter).EncryptAsync(default);
        Assert.Equal(DataFolderEncryptionState.Partial, result.State); Assert.Equal(DataFolderEncryptionProblem.Changed, result.Problem);
        adapter = new FakeEncryption { Apply = false };
        Assert.Equal(DataFolderEncryptionState.Partial, (await new WindowsDataFolderEncryption(_root, adapter).EncryptAsync(default)).State);
    }

    [Fact]
    public async Task CancellationStopsBetweenNativeCallsAndNeverDecryptsCompletedEntries()
    {
        Write("accounts.db"); using var stop = new CancellationTokenSource();
        var adapter = new FakeEncryption { Before = _ => stop.Cancel() };
        var result = await new WindowsDataFolderEncryption(_root, adapter).EncryptAsync(stop.Token);
        Assert.Equal(DataFolderEncryptionState.Stopped, result.State); Assert.Single(adapter.Calls); Assert.Contains(_root, adapter.Encrypted);
        var unused = new FakeEncryption();
        Assert.Equal(DataFolderEncryptionState.Stopped, (await new WindowsDataFolderEncryption(_root, unused).EncryptAsync(stop.Token)).State);
        Assert.Empty(unused.Calls);
    }

    [Fact]
    public async Task OverlappingCallIsRefusedUntilTheActualNativeCallReturns()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var adapter = new FakeEncryption { Before = _ => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } };
        var service = new WindowsDataFolderEncryption(_root, adapter); var active = service.EncryptAsync(default);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try { Assert.Equal(DataFolderEncryptionState.Busy, (await service.EncryptAsync(default)).State); }
        finally { release.Set(); }
        Assert.Equal(DataFolderEncryptionState.Verified, (await active).State);
    }

    [Fact]
    public async Task HardLinksAreRejectedBeforeAnyEncryptionCanChangeAnotherNamedFile()
    {
        string source = Write("source.bin"), link = Path.Combine(_root, "link.bin");
        Assert.True(CreateHardLink(link, source, 0), new Win32Exception(Marshal.GetLastWin32Error()).Message);
        var adapter = new FakeEncryption(); var result = await new WindowsDataFolderEncryption(_root, adapter).EncryptAsync(default);
        Assert.Equal(DataFolderEncryptionState.Rejected, result.State); Assert.Equal(DataFolderEncryptionProblem.UnsafePath, result.Problem); Assert.Empty(adapter.Calls);
    }

    [Fact]
    public async Task DepthLimitRejectsTheWholeInventoryBeforeMutation()
    {
        string path = _root; for (int i = 0; i <= WindowsDataFolderEncryption.MaximumDepth; i++) { path = Path.Combine(path, "d"); Directory.CreateDirectory(path); }
        var adapter = new FakeEncryption(); var result = await new WindowsDataFolderEncryption(_root, adapter).EncryptAsync(default);
        Assert.Equal(DataFolderEncryptionState.Rejected, result.State); Assert.Equal(DataFolderEncryptionProblem.Limit, result.Problem); Assert.Empty(adapter.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JunctionWithinTheTreeOrAtTheSelectedRootIsRejectedBeforeEncryption(bool selectJunction)
    {
        string actual = Path.Combine(_root, "actual"), alias = Path.Combine(_root, "alias");
        Write("actual\\accounts.db");
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        string script = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path " + Quote(alias) + " -Target " + Quote(actual) + " | Out-Null";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32/WindowsPowerShell/v1.0/powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start)!;
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, process.ExitCode); }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } }
        try
        {
            var adapter = new FakeEncryption(); var result = await new WindowsDataFolderEncryption(selectJunction ? alias : _root, adapter).EncryptAsync(default);
            Assert.Equal(DataFolderEncryptionState.Rejected, result.State); Assert.Equal(DataFolderEncryptionProblem.UnsafePath, result.Problem);
            Assert.Empty(adapter.Calls); Assert.True(File.Exists(Path.Combine(actual, "accounts.db")));
        }
        finally { Directory.Delete(alias, false); }
    }

    [Fact]
    public async Task ReplacementDuringTheNativeCallIsReportedAsIncompleteEvenIfTheNewPathReadsEncrypted()
    {
        string file = Write("vault\\record.bin");
        var adapter = new FakeEncryption { Before = path =>
        {
            if (path != file) return;
            File.Move(file, file + ".moved");
            File.WriteAllText(file, "replacement fixture");
        } };
        var result = await new WindowsDataFolderEncryption(_root, adapter).EncryptAsync(default);
        Assert.Equal(DataFolderEncryptionState.Partial, result.State);
        Assert.Equal(DataFolderEncryptionProblem.Changed, result.Problem);
    }

    private sealed class FakeEncryption : WindowsDataFolderEncryption.IFileEncryption
    {
        internal readonly HashSet<string> Encrypted = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<string> Calls = [];
        internal Action<string>? Before;
        internal bool Apply = true;
        public bool IsEncrypted(string path) => Encrypted.Contains(path);
        public void Encrypt(string path) { Calls.Add(path); Before?.Invoke(path); if (Apply) Encrypted.Add(path); }
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string path, string existing, nint security);
}
