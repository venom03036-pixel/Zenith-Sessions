using System.Buffers;
using System.ComponentModel;
using System.Formats.Asn1;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// Explicit, read-only selected-file verification. One owned worker, no queued requests or persisted
/// trust cache. Native cache-only trust calls cannot be interrupted; cancellation and disposal join
/// their actual owner rather than abandon it. A result never authorizes a future client handoff.
/// The Roblox player must be signed by Roblox Corporation; a bootstrapper's own executable (R-launch-17)
/// must be validly signed by the certificate the user accepted for that product, read from
/// <see cref="IAcceptedBootstrapperPublisherSource"/> at verification time (none accepted without one).
/// </summary>
public sealed partial class WindowsClientExecutableTrust : IClientExecutableTrustVerifier
{
    public const long MaximumFileBytes = 512L * 1024 * 1024;
    public const int MaximumPathCharacters = 2048;
    private readonly object _gate = new();
    private readonly IClientAuthenticodePolicy _policy;
    private readonly IAcceptedBootstrapperPublisherSource? _acceptedPublishers;
    private readonly TimeProvider _time;
    private readonly long _maximumFileBytes;
    private readonly Func<SafeFileHandle, ClientExecutableFileSnapshot> _snapshot;
    private readonly CancellationTokenSource _lifetime = new();
    private Operation? _active;
    private Task? _disposeTask;
    private bool _disposed;

    public WindowsClientExecutableTrust(TimeProvider? time = null) : this(new WindowsAuthenticodePolicy(), time, MaximumFileBytes) { }
    /// <param name="acceptedPublishers">Where the publisher the user accepted for each bootstrapper is read (saved settings); null accepts none.</param>
    public WindowsClientExecutableTrust(IAcceptedBootstrapperPublisherSource? acceptedPublishers, TimeProvider? time = null)
        : this(new WindowsAuthenticodePolicy(), time, MaximumFileBytes, acceptedPublishers: acceptedPublishers) { }
    internal WindowsClientExecutableTrust(IClientAuthenticodePolicy policy, TimeProvider? time = null, long maximumFileBytes = MaximumFileBytes,
        Func<SafeFileHandle, ClientExecutableFileSnapshot>? snapshot = null, INativeHandoffProcessStarter? starter = null,
        IAcceptedBootstrapperPublisherSource? acceptedPublishers = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy)); _time = time ?? TimeProvider.System;
        if (maximumFileBytes is < 1 or > MaximumFileBytes) throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        _maximumFileBytes = maximumFileBytes; _snapshot = snapshot ?? Snapshot;
        _starter = starter ?? new WindowsNativeHandoffProcessStarter();
        _acceptedPublishers = acceptedPublishers;
    }

    public Task<ClientExecutableTrustResult> VerifyAsync(ClientInstallationCandidate candidate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate); ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null) return Task.FromResult(Result(candidate, ClientExecutableTrustStatus.Deferred, ClientExecutableTrustReason.Busy));
            var operation = new Operation(CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token));
            _active = operation;
            var worker = Task.Run(() =>
            {
                VerifiedExecutableLease? held = null;
                try
                {
                    var result = VerifyCore(candidate, operation.Stop.Token, out held);
                    operation.Stop.Token.ThrowIfCancellationRequested();
                    return result;
                }
                finally
                {
                    try { held?.Dispose(); }
                    finally { FinishOperation(operation); }
                }
            }, CancellationToken.None);
            operation.Worker = worker; return worker;
        }
    }

    private ClientExecutableTrustResult VerifyCore(ClientInstallationCandidate candidate, CancellationToken ct, out VerifiedExecutableLease? held)
    {
        held = null;
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return Result(candidate, ClientExecutableTrustStatus.Unknown, ClientExecutableTrustReason.UnsupportedPlatform);
        if (!Enum.IsDefined(candidate.Product) || !Enum.IsDefined(candidate.FileState))
            return Result(candidate, ClientExecutableTrustStatus.Rejected, ClientExecutableTrustReason.InvalidCandidate);
        if (!TryPath(candidate.ExecutablePath, out string path)) return Result(candidate, ClientExecutableTrustStatus.Rejected, ClientExecutableTrustReason.UnsafePath);
        bool bootstrapper = BootstrapperProducts.IsBootstrapper(candidate.Product);
        if (bootstrapper)
        {
            // R-launch-17: only the bootstrapper's own executable is evaluated, under its accepted-publisher policy below. A player
            // under its folder is a Roblox row with that bootstrapper's evidence, which the provider refuses; any other file is invalid.
            if (!BootstrapperProducts.IsOwnExecutable(candidate.Product, path))
                return Result(candidate, ClientExecutableTrustStatus.Rejected, ClientExecutableTrustReason.InvalidCandidate);
        }
        else
        {
            if (RobloxNativePlayerFiles.IsLauncher(path))
                return Result(candidate, ClientExecutableTrustStatus.Unknown, ClientExecutableTrustReason.BootstrapperNotEvaluated);
            if (!RobloxNativePlayerFiles.IsDirectPlayer(path))
                return Result(candidate, ClientExecutableTrustStatus.Rejected, ClientExecutableTrustReason.InvalidCandidate);
        }
        ClientExecutableFileSnapshot? before = null;
        string? hash = null;
        SafeFileHandle? handle = null; FileStream? file = null;
        try
        {
            CheckAncestors(path, ct);
            handle = OpenRead(path);
            before = _snapshot(handle);
            if (!before.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)) throw new UnsafeExecutablePathException();
            if (before.Length > _maximumFileBytes) return Result(candidate, ClientExecutableTrustStatus.Rejected, ClientExecutableTrustReason.FileTooLarge, before);
            if (before.Length <= 0) return Result(candidate, ClientExecutableTrustStatus.Rejected, ClientExecutableTrustReason.InvalidCandidate, before);
            file = new FileStream(handle, FileAccess.Read, 65536, isAsync: false);
            hash = Hash(file, before.Length, ct);
            file.Position = 0;
            NativeClientTrustEvidence evidence = _policy.Verify(handle, path, _time.GetUtcNow(), ct);
            ct.ThrowIfCancellationRequested();
            // The same handle is used by hashing and WinVerifyTrust. Also re-open the selected
            // path and repeat the digest: an ancestor replacement or existing writable mapping
            // must not let a different file inherit the original decision.
            file.Position = 0;
            if (Hash(file, before.Length, ct) != hash) return Result(candidate, ClientExecutableTrustStatus.Rejected, ClientExecutableTrustReason.FileChanged);
            CheckAncestors(path, ct);
            using var current = OpenRead(path);
            if (_snapshot(handle) != before || _snapshot(current) != before)
                return Result(candidate, ClientExecutableTrustStatus.Rejected, ClientExecutableTrustReason.FileChanged);
            ct.ThrowIfCancellationRequested();
            var reason = evidence.Outcome switch
            {
                NativeClientTrustOutcome.VerifiedCached => ClientExecutableTrustReason.Verified,
                NativeClientTrustOutcome.Unsigned => ClientExecutableTrustReason.Unsigned,
                NativeClientTrustOutcome.InvalidSignature => ClientExecutableTrustReason.InvalidSignature,
                NativeClientTrustOutcome.RevocationUnavailable => ClientExecutableTrustReason.RevocationUnavailable,
                NativeClientTrustOutcome.UntrustedChain => ClientExecutableTrustReason.UntrustedChain,
                NativeClientTrustOutcome.AccessDenied => ClientExecutableTrustReason.AccessDenied,
                NativeClientTrustOutcome.MetadataUnavailable => ClientExecutableTrustReason.MetadataUnavailable,
                _ => ClientExecutableTrustReason.VerificationUnavailable
            };
            ClientExecutableSigner? signer = ValidSigner(evidence.Signer) ? evidence.Signer : null;
            if (reason == ClientExecutableTrustReason.Verified && signer is null) reason = ClientExecutableTrustReason.MetadataUnavailable;
            if (reason == ClientExecutableTrustReason.Verified)
                reason = bootstrapper ? AcceptedPublisherReason(candidate.Product, signer!)
                    : string.Equals(signer!.Organization, "Roblox Corporation", StringComparison.OrdinalIgnoreCase) ? reason : ClientExecutableTrustReason.PublisherMismatch;
            ClientExecutableTrustStatus status = reason switch
            {
                ClientExecutableTrustReason.Verified => bootstrapper ? ClientExecutableTrustStatus.VerifiedAcceptedPublisher : ClientExecutableTrustStatus.VerifiedRobloxPublisher,
                ClientExecutableTrustReason.Unsigned or ClientExecutableTrustReason.InvalidSignature => ClientExecutableTrustStatus.Rejected,
                _ => ClientExecutableTrustStatus.Unknown
            };
            var result = Result(candidate, status, reason, before, hash, signer);
            if (status is ClientExecutableTrustStatus.VerifiedRobloxPublisher or ClientExecutableTrustStatus.VerifiedAcceptedPublisher)
            {
                held = new(file, before); file = null; handle = null;
            }
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (ChangedExecutableException) { return Result(candidate, ClientExecutableTrustStatus.Rejected, ClientExecutableTrustReason.FileChanged); }
        catch (UnsafeExecutablePathException) { return Result(candidate, ClientExecutableTrustStatus.Rejected, ClientExecutableTrustReason.UnsafePath); }
        catch (FileNotFoundException) { return Result(candidate, ClientExecutableTrustStatus.Rejected, before is null ? ClientExecutableTrustReason.MissingFile : ClientExecutableTrustReason.FileChanged); }
        catch (DirectoryNotFoundException) { return Result(candidate, ClientExecutableTrustStatus.Rejected, before is null ? ClientExecutableTrustReason.MissingFile : ClientExecutableTrustReason.FileChanged); }
        catch (Exception error) when (error is UnauthorizedAccessException or SecurityException)
        { return Result(candidate, ClientExecutableTrustStatus.Unknown, ClientExecutableTrustReason.AccessDenied); }
        catch (Win32Exception error)
        {
            var reason = error.NativeErrorCode is 2 or 3 ? ClientExecutableTrustReason.MissingFile
                : error.NativeErrorCode is 5 or 32 or 33 ? ClientExecutableTrustReason.AccessDenied : ClientExecutableTrustReason.MetadataUnavailable;
            return Result(candidate, ClientExecutableTrustStatus.Unknown, reason);
        }
        catch (Exception error) when (error is IOException or CryptographicException or ArgumentException)
        { return Result(candidate, ClientExecutableTrustStatus.Unknown, ClientExecutableTrustReason.MetadataUnavailable); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        { return Result(candidate, ClientExecutableTrustStatus.Unknown, ClientExecutableTrustReason.VerificationUnavailable); }
        finally { try { file?.Dispose(); } finally { handle?.Dispose(); } }
    }

    private ClientExecutableTrustResult Result(ClientInstallationCandidate candidate, ClientExecutableTrustStatus status, ClientExecutableTrustReason reason,
        ClientExecutableFileSnapshot? file = null, string? hash = null, ClientExecutableSigner? signer = null) =>
        new(candidate.Product, candidate.ExecutablePath ?? "", status, reason, _time.GetUtcNow(), file, hash, signer);

    // R-launch-17: a validly signed bootstrapper is Verified only when its certificate thumbprint is the one the user accepted for
    // that product, read from saved state now. No source, or none saved, means not accepted; a different signer is reported as
    // changed. A failing source leaves trust unknown (never accepted by default). Exception text is never kept.
    private ClientExecutableTrustReason AcceptedPublisherReason(ClientInstallationProduct product, ClientExecutableSigner signer)
    {
        AcceptedBootstrapperPublisher? accepted;
        try { accepted = _acceptedPublishers?.Find(product); }
        catch (Exception) { return ClientExecutableTrustReason.VerificationUnavailable; }
        if (accepted is null || !accepted.IsFor(product)) return ClientExecutableTrustReason.PublisherNotAccepted;
        return accepted.Matches(signer) ? ClientExecutableTrustReason.Verified : ClientExecutableTrustReason.AcceptedPublisherChanged;
    }

    private static bool ValidSigner(ClientExecutableSigner? signer) => signer is not null
        && signer.DisplayName is { Length: > 0 and <= 256 } && !signer.DisplayName.Any(char.IsControl)
        && signer.Organization is { Length: > 0 and <= 256 } && !signer.Organization.Any(char.IsControl)
        && signer.CertificateSha256 is { Length: 64 } && signer.CertificateSha256.AsSpan().IndexOfAnyExcept("0123456789ABCDEF") < 0
        && signer.NotBeforeUtc != default && signer.NotAfterUtc > signer.NotBeforeUtc;

    private static bool TryPath(string? supplied, out string path)
    {
        path = "";
        if (supplied is not { Length: > 0 and <= MaximumPathCharacters } || supplied.Any(char.IsControl)) return false;
        supplied = supplied.Replace('/', '\\');
        if (supplied.Length < 4 || !char.IsAsciiLetter(supplied[0]) || supplied[1] != ':' || supplied[2] != '\\'
            || supplied.AsSpan(2).Contains(':') || supplied.Contains('"')) return false;
        try
        {
            if (!Path.IsPathFullyQualified(supplied)) return false;
            path = Path.GetFullPath(supplied);
            return path.Length <= MaximumPathCharacters && path.Equals(supplied, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    private static void CheckAncestors(string path, CancellationToken ct)
    {
        uint drive = Native.GetDriveType(Path.GetPathRoot(path)!);
        if (drive is not (2 or 3 or 5 or 6)) throw new UnsafeExecutablePathException();
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            ct.ThrowIfCancellationRequested();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new UnsafeExecutablePathException();
        }
    }
    private static SafeFileHandle OpenRead(string path)
    {
        // Read sharing permits verification readers; no write/delete sharing permits file updates.
        var handle = Native.CreateFile(path, 0x80000000, 1, 0, 3, 0x00200000 | 0x08000000, 0);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error);
    }
    private static ClientExecutableFileSnapshot Snapshot(SafeFileHandle handle)
    {
        if (Native.GetFileType(handle) != 1 || !Native.GetFileInformationByHandle(handle, out var info)
            || !Native.GetFileInformationByHandleEx(handle, 18, out var identity, (uint)Marshal.SizeOf<Native.FileIdInfo>()))
            throw new IOException("The selected file identity could not be read.");
        if ((info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new UnsafeExecutablePathException();
        var path = new StringBuilder(32768);
        uint length = Native.GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
        if (length == 0 || length >= path.Capacity) throw new IOException("The selected file path could not be resolved.");
        string full = path.ToString();
        if (!full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) throw new UnsafeExecutablePathException();
        full = full[4..];
        if (!TryPath(full, out full) || (identity.Low == 0 && identity.High == 0)) throw new UnsafeExecutablePathException();
        ulong bytes = ((ulong)info.SizeHigh << 32) | info.SizeLow;
        if (bytes > long.MaxValue) throw new IOException("The executable size is unsupported.");
        return new(full, (long)bytes, FileTime(info.CreationHigh, info.CreationLow), FileTime(info.WriteHigh, info.WriteLow),
            identity.VolumeSerialNumber, identity.Low.ToString("X16") + identity.High.ToString("X16"));
    }
    private static DateTimeOffset FileTime(uint high, uint low) => DateTimeOffset.FromFileTime(unchecked((long)(((ulong)high << 32) | low))).ToUniversalTime();
    private static string Hash(FileStream file, long maximumBytes, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int count; long observed = 0;
            do
            {
                ct.ThrowIfCancellationRequested();
                count = file.Read(buffer, 0, (int)Math.Min(65536, maximumBytes - observed + 1));
                observed += count;
                if (observed > maximumBytes) throw new ChangedExecutableException();
                hash.AppendData(buffer, 0, count);
            } while (count != 0);
            if (observed != maximumBytes) throw new ChangedExecutableException();
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            return new(_disposeTask = StopAsync(_active));
        }
    }
    private async Task StopAsync(Operation? operation)
    {
        await Task.Yield();
        try
        {
            _lifetime.Cancel();
            if (operation is not null)
            {
                try { if (operation.Worker is { } work) await work.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                finally { operation.Prepared?.Dispose(); }
                await operation.Completion.Task.ConfigureAwait(false);
            }
        }
        finally { _lifetime.Dispose(); }
    }
    private void FinishOperation(Operation operation)
    {
        lock (_gate)
        {
            if (operation.Finished) return;
            operation.Finished = true;
            if (ReferenceEquals(_active, operation)) _active = null;
        }
        operation.Stop.Dispose(); operation.Completion.TrySetResult();
    }
    private sealed class Operation(CancellationTokenSource stop)
    {
        public CancellationTokenSource Stop { get; } = stop;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Worker; public PreparedNativeHandoffStart? Prepared; public bool Finished;
    }
    private sealed class UnsafeExecutablePathException : IOException { }
    private sealed class ChangedExecutableException : IOException { }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct FileInformation
        {
            public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct FileIdInfo { public ulong VolumeSerialNumber, Low, High; }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
        [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetDriveType(string root);
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetFileType(SafeFileHandle file);
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetFileInformationByHandleEx(SafeFileHandle file, uint kind, out FileIdInfo information, uint size);
        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    }
}

internal enum NativeClientTrustOutcome { VerifiedCached, Unsigned, InvalidSignature, RevocationUnavailable, UntrustedChain, AccessDenied, MetadataUnavailable, Unavailable }
internal sealed record NativeClientTrustEvidence(NativeClientTrustOutcome Outcome, ClientExecutableSigner? Signer = null);
internal interface IClientAuthenticodePolicy
{
    NativeClientTrustEvidence Verify(SafeFileHandle handle, string path, DateTimeOffset now, CancellationToken ct);
}

internal sealed class WindowsAuthenticodePolicy : IClientAuthenticodePolicy
{
    internal const uint ProviderFlags = 0x80 | 0x1000 | 0x2000; // chain except root, cache only, reject MD2/MD4
    private const int MaximumCertificates = 16, MaximumCertificateBytes = 65536;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public NativeClientTrustEvidence Verify(SafeFileHandle handle, string path, DateTimeOffset now, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        nint fileInfo = 0, pathText = 0, signatureSettings = 0; bool handleHeld = false, invoked = false;
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, RevocationChecks = 1,
            UnionChoice = 1, StateAction = 1, ProviderFlags = ProviderFlags };
        Guid action = GenericVerifyV2;
        try
        {
            handle.DangerousAddRef(ref handleHeld);
            pathText = Marshal.StringToCoTaskMemUni(path);
            fileInfo = Marshal.AllocCoTaskMem(Marshal.SizeOf<TrustFile>());
            Marshal.StructureToPtr(new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = pathText, File = handle.DangerousGetHandle() }, fileInfo, false);
            signatureSettings = Marshal.AllocCoTaskMem(Marshal.SizeOf<SignatureSettings>());
            Marshal.StructureToPtr(new SignatureSettings { Size = (uint)Marshal.SizeOf<SignatureSettings>(), Flags = 1 }, signatureSettings, false);
            data.SignatureSettings = signatureSettings; // WSS_VERIFY_SPECIFIC: bind extraction to primary signature zero.
            data.File = fileInfo; invoked = true;
            int status = WinVerifyTrust(-1, ref action, ref data);
            ct.ThrowIfCancellationRequested();
            if (status != 0) return new(Classify(status));
            nint provider = WTHelperProvDataFromStateData(data.State);
            nint signer = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signer == 0) return new(NativeClientTrustOutcome.MetadataUnavailable);
            var signerInfo = Marshal.PtrToStructure<ProviderSigner>(signer);
            if (signerInfo.Size < Marshal.SizeOf<ProviderSigner>() || signerInfo.Error != 0 || signerInfo.CertificateCount is < 1 or > MaximumCertificates)
                return new(NativeClientTrustOutcome.MetadataUnavailable);
            var certificates = new List<X509Certificate2>((int)signerInfo.CertificateCount);
            try
            {
                for (uint index = 0; index < signerInfo.CertificateCount; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    nint item = WTHelperGetProvCertFromChain(signer, index);
                    if (item == 0) return new(NativeClientTrustOutcome.MetadataUnavailable);
                    var providerCertificate = Marshal.PtrToStructure<ProviderCertificate>(item);
                    if (providerCertificate.Size < Marshal.SizeOf<ProviderCertificate>() || providerCertificate.Certificate == 0 || providerCertificate.Error != 0)
                        return new(NativeClientTrustOutcome.UntrustedChain);
                    var certificate = Marshal.PtrToStructure<CertificateContext>(providerCertificate.Certificate);
                    if (certificate.Encoded == 0 || certificate.EncodedLength is < 1 or > MaximumCertificateBytes)
                        return new(NativeClientTrustOutcome.MetadataUnavailable);
                    byte[] encoded = new byte[certificate.EncodedLength]; Marshal.Copy(certificate.Encoded, encoded, 0, encoded.Length);
                    certificates.Add(X509CertificateLoader.LoadCertificate(encoded));
                }
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.Offline;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                chain.ChainPolicy.DisableCertificateDownloads = true;
                chain.ChainPolicy.VerificationTime = now.UtcDateTime;
                chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(1);
                chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.3"));
                for (int index = 1; index < certificates.Count; index++) chain.ChainPolicy.ExtraStore.Add(certificates[index]);
                bool built = chain.Build(certificates[0]); ct.ThrowIfCancellationRequested();
                if (!built || chain.ChainStatus.Any(item => item.Status != X509ChainStatusFlags.NoError))
                {
                    bool unavailable = chain.ChainStatus.Any(item => (item.Status & (X509ChainStatusFlags.OfflineRevocation | X509ChainStatusFlags.RevocationStatusUnknown)) != 0);
                    return new(unavailable ? NativeClientTrustOutcome.RevocationUnavailable : NativeClientTrustOutcome.UntrustedChain);
                }
                X509Certificate2 leaf = certificates[0];
                if (chain.ChainElements.Count < 2 || !leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                    .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.3")))
                    return new(NativeClientTrustOutcome.UntrustedChain);
                string? organization = ReadOrganization(leaf.SubjectName.RawData);
                if (organization is null) return new(NativeClientTrustOutcome.MetadataUnavailable);
                return new(NativeClientTrustOutcome.VerifiedCached, new(leaf.GetNameInfo(X509NameType.SimpleName, false), organization,
                    leaf.GetCertHashString(HashAlgorithmName.SHA256), new(leaf.NotBefore.ToUniversalTime()), new(leaf.NotAfter.ToUniversalTime())));
            }
            finally { foreach (var certificate in certificates) certificate.Dispose(); }
        }
        finally
        {
            try
            {
                if (invoked)
                {
                    data.StateAction = 2;
                    if (WinVerifyTrust(-1, ref action, ref data) != 0) throw new CryptographicException("Windows trust state cleanup failed.");
                }
            }
            finally
            {
                if (fileInfo != 0) Marshal.FreeCoTaskMem(fileInfo);
                if (pathText != 0) Marshal.FreeCoTaskMem(pathText);
                if (signatureSettings != 0) Marshal.FreeCoTaskMem(signatureSettings);
                if (handleHeld) handle.DangerousRelease();
            }
        }
    }

    internal static NativeClientTrustOutcome Classify(int status) => unchecked((uint)status) switch
    {
        0x800B0100 => NativeClientTrustOutcome.Unsigned,
        0x80096010 or 0x800B0004 or 0x800B010C => NativeClientTrustOutcome.InvalidSignature,
        0x80092012 or 0x80092013 or 0x800B010E => NativeClientTrustOutcome.RevocationUnavailable,
        0x800B0101 or 0x800B0109 or 0x800B010A or 0x800B0111 => NativeClientTrustOutcome.UntrustedChain,
        0x80070005 => NativeClientTrustOutcome.AccessDenied,
        _ => NativeClientTrustOutcome.Unavailable
    };

    internal static string? ReadOrganization(byte[] distinguishedName)
    {
        if (distinguishedName.Length is < 1 or > MaximumCertificateBytes) return null;
        try
        {
            var reader = new AsnReader(distinguishedName, AsnEncodingRules.DER); var sequence = reader.ReadSequence();
            string? organization = null; int attributes = 0;
            while (sequence.HasData)
            {
                var set = sequence.ReadSetOf();
                while (set.HasData)
                {
                    if (++attributes > 64) return null;
                    var attribute = set.ReadSequence(); string oid = attribute.ReadObjectIdentifier();
                    if (oid == "2.5.4.10")
                    {
                        if (organization is not null) return null;
                        var tag = attribute.PeekTag();
                        if (tag.TagClass != TagClass.Universal) return null;
                        organization = attribute.ReadCharacterString((UniversalTagNumber)tag.TagValue);
                    }
                    else attribute.ReadEncodedValue();
                    attribute.ThrowIfNotEmpty();
                }
            }
            reader.ThrowIfNotEmpty();
            return organization is { Length: > 0 and <= 256 } && !organization.Any(char.IsControl) ? organization : null;
        }
        catch (Exception error) when (error is AsnContentException or ArgumentException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct TrustFile { public uint Size; public nint Path, File, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct SignatureSettings { public uint Size, Index, Flags, SecondaryCount, VerifiedIndex; public nint CryptoPolicy; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    {
        public uint Size; public nint Policy, Sip; public uint UiChoice, RevocationChecks, UnionChoice;
        public nint File; public uint StateAction; public nint State, Url; public uint ProviderFlags, UiContext; public nint SignatureSettings;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProviderSigner
    {
        public uint Size, VerifyTimeLow, VerifyTimeHigh, CertificateCount; public nint Certificates;
        public uint SignerType; public nint Signer; public uint Error, CounterSignerCount; public nint CounterSigners, Chain;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProviderCertificate
    {
        public uint Size; public nint Certificate; public int Commercial, TrustedRoot, SelfSigned, TestCertificate;
        public uint RevokedReason, Confidence, Error; public nint TrustList; public int TrustListSigner; public nint Ctl;
        public uint CtlError; public int Cyclic; public nint ChainElement;
    }
    [StructLayout(LayoutKind.Sequential)] private struct CertificateContext { public uint Encoding; public nint Encoded; public uint EncodedLength; public nint Info, Store; }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint WTHelperProvDataFromStateData(nint state);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint WTHelperGetProvSignerFromChain(nint provider, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint WTHelperGetProvCertFromChain(nint signer, uint certificate);
}
