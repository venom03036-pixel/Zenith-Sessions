using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// Roblox's own installer, downloaded from Roblox's setup host into a fresh temporary folder, verified with the executable trust
/// code (cached-evidence Authenticode signature, Roblox Corporation publisher, double digest, re-open comparison) and held open
/// with read sharing until it is run once or discarded. One operation at a time. The HTTP client is owned here and uses the same
/// anonymous handler settings as the public directory: no redirects, no cookie jar, no credentials, the Windows system proxy. No
/// User-Agent, Referer or Origin is sent. The download is bounded in size and time; a running installer is never ended, only
/// waited for. No path, address beyond the setup host or exception text leaves this class in a summary (Rule 10).
/// </summary>
public sealed class WindowsRobloxClientUpdateInstaller : IClientUpdateInstaller
{
    private const string FolderPrefix = "ZenithSessions-RobloxUpdate-";
    private const int BufferBytes = 64 * 1024;
    private readonly HttpClient _http;
    private readonly WindowsClientExecutableTrust _trust;
    private readonly string _temporaryRoot;
    private readonly TimeSpan _waitLimit;
    private readonly long _maximumBytes;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Held? _held;
    private bool _busy, _disposed;
    private sealed record Held(WindowsInstallerPreparation Preparation, string Folder);

    public WindowsRobloxClientUpdateInstaller(TimeProvider? time = null)
        : this(new HttpClient(RobloxPublicDirectory.CreateAnonymousHandler()) { Timeout = TimeSpan.FromSeconds(30) },
            new WindowsAuthenticodePolicy(), new WindowsInstallerProcessStarter(), Path.GetTempPath(), time) { }

    /// <summary>Takes ownership of <paramref name="http"/>. The client's timeout covers connecting and the headers; the body is bounded here.</summary>
    internal WindowsRobloxClientUpdateInstaller(HttpClient http, IClientAuthenticodePolicy policy, IInstallerProcessStarter starter, string temporaryRoot,
        TimeProvider? time = null, TimeSpan? waitLimit = null, long maximumBytes = RobloxClientUpdate.MaximumInstallerBytes)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRoot);
        if (maximumBytes is < 1 or > RobloxClientUpdate.MaximumInstallerBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _trust = new WindowsClientExecutableTrust(policy, time, maximumBytes, installerStarter: starter);
        _temporaryRoot = Path.GetFullPath(temporaryRoot);
        _waitLimit = waitLimit ?? RobloxClientUpdate.InstallerWaitLimit;
        _maximumBytes = maximumBytes;
    }

    public bool HasVerifiedInstaller { get { lock (_gate) return _held is not null; } }

    public async Task<ClientUpdateDownloadResult> DownloadAsync(PublicRobloxClientRelease release, IProgress<ClientUpdateDownloadProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release); ct.ThrowIfCancellationRequested();
        if (!RobloxClientRelease.TryInstallerUri(release, out var uri))
            return new(ClientUpdateDownloadOutcome.Refused, "Roblox's current release has an unexpected name, so no installer address was built and nothing was downloaded.");
        Held? previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_busy) return new(ClientUpdateDownloadOutcome.Refused, "A Roblox update download or installation is already in progress.");
            _busy = true; previous = _held; _held = null;
        }
        string? folder = null;
        try
        {
            if (previous is not null) Release(previous);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            bounded.CancelAfter(RobloxClientUpdate.DownloadLimit);
            var token = bounded.Token;
            folder = CreateFolder();
            string file = Path.Combine(folder, RobloxClientUpdate.InstallerFileName);
            long written;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (response.StatusCode != HttpStatusCode.OK)
                    return Cleanup(folder, new(ClientUpdateDownloadOutcome.Failed, status is >= 300 and < 400
                        ? "Roblox's setup host answered with a redirect, which Zenith does not follow. Nothing was downloaded."
                        : response.StatusCode == HttpStatusCode.NotFound
                            ? "Roblox's setup host has no installer under the current release's name. Nothing was downloaded."
                            : $"Roblox's setup host answered with HTTP {status}. Nothing was downloaded."));
                long? total = response.Content.Headers.ContentLength;
                if (total is { } declared && (declared <= 0 || declared > _maximumBytes))
                    return Cleanup(folder, new(ClientUpdateDownloadOutcome.Refused, declared <= 0
                        ? "Roblox's setup host announced an empty installer. Nothing was downloaded."
                        : $"The installer is larger than the {Megabytes(_maximumBytes)} MB limit, so it was not downloaded."));
                await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                written = await WriteAsync(body, file, total, progress, token).ConfigureAwait(false);
                if (written < 0)
                    return Cleanup(folder, new(ClientUpdateDownloadOutcome.Refused, $"The download passed the {Megabytes(_maximumBytes)} MB limit and was stopped; the partial file was deleted."));
                if (total is { } expected && written != expected)
                    return Cleanup(folder, new(ClientUpdateDownloadOutcome.Failed, "The download ended before the whole installer arrived; the partial file was deleted."));
                if (written == 0)
                    return Cleanup(folder, new(ClientUpdateDownloadOutcome.Failed, "Roblox's setup host sent an empty installer; nothing was kept."));
            }
            catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
                return Cleanup(folder, new(ClientUpdateDownloadOutcome.Failed,
                    "The installer could not be downloaded or saved; nothing was kept. Check the connection and the temporary folder, then try again.", Failure: error));
            }

            var preparation = await _trust.PrepareInstallerAsync(file, token).ConfigureAwait(false);
            var trust = preparation.Trust;
            if (trust.Status == ClientExecutableTrustStatus.VerifiedRobloxPublisher && preparation.Run is not null
                && trust.Sha256 is { Length: 64 } && trust.Signer is { } signer && trust.File is { } snapshot && snapshot.Length == written)
            {
                lock (_gate)
                {
                    if (_disposed) { preparation.Dispose(); return Cleanup(folder, new(ClientUpdateDownloadOutcome.Failed, "Zenith is closing; the downloaded installer was deleted.")); }
                    _held = new(preparation, folder);
                }
                folder = null; // Owned by the held preparation from here.
                return new(ClientUpdateDownloadOutcome.Verified,
                    $"Windows verified the installer's signature from {signer.Organization} using cached trust evidence.", written, trust.Sha256, signer);
            }
            preparation.Dispose();
            return Cleanup(folder, new(ClientUpdateDownloadOutcome.NotVerified, trust.Summary, written, TrustReason: trust.Reason));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            // Only a time limit can cancel here: the download's own, or the client's for connecting and the headers.
            if (folder is not null) DeleteFolder(folder);
            return new(ClientUpdateDownloadOutcome.Failed,
                $"The download did not finish in time (Roblox's setup host did not answer, or the whole download took over {(int)RobloxClientUpdate.DownloadLimit.TotalMinutes} minutes); nothing was kept.", Failure: new TimeoutException());
        }
        catch (Exception)
        {
            if (folder is not null) DeleteFolder(folder);
            throw;
        }
        finally { lock (_gate) _busy = false; }
    }

    public async Task<ClientUpdateInstallResult> InstallAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Held held;
        lock (_gate)
        {
            if (_disposed || _held is null) return new(ClientUpdateInstallOutcome.NotStarted, null, "No verified installer is held. Download the Roblox update first.");
            if (_busy) return new(ClientUpdateInstallOutcome.NotStarted, null, "A Roblox update download or installation is already in progress.");
            _busy = true; held = _held; _held = null;
        }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            return await held.Preparation.Run!.RunAsync(_waitLimit, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            // The run released the held handle; the folder goes unless the installer still runs from it (then Windows refuses
            // the delete and the folder stays in Temp).
            Release(held);
            lock (_gate) _busy = false;
        }
    }

    public void Discard()
    {
        Held? held;
        lock (_gate) { held = _held; _held = null; }
        if (held is not null) Release(held);
    }

    public async ValueTask DisposeAsync()
    {
        Held? held;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; held = _held; _held = null;
        }
        _lifetime.Cancel();
        if (held is not null) Release(held);
        try { await _trust.DisposeAsync().ConfigureAwait(false); }
        finally { _http.Dispose(); _lifetime.Dispose(); }
    }

    private async Task<long> WriteAsync(Stream body, string file, long? total, IProgress<ClientUpdateDownloadProgress>? progress, CancellationToken ct)
    {
        // Exclusive while writing: nothing else can read or change the file before verification opens it.
        await using var target = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.Asynchronous);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            long written = 0; int count;
            while ((count = await body.ReadAsync(buffer.AsMemory(0, BufferBytes), ct).ConfigureAwait(false)) > 0)
            {
                written += count;
                if (written > _maximumBytes) return -1;
                await target.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                progress?.Report(new(written, total));
            }
            await target.FlushAsync(ct).ConfigureAwait(false);
            return written;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private string CreateFolder()
    {
        string folder = Path.Combine(_temporaryRoot, FolderPrefix + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(folder) || File.Exists(folder)) throw new IOException("The temporary folder name is already in use.");
        Directory.CreateDirectory(folder);
        return folder;
    }
    private static ClientUpdateDownloadResult Cleanup(string folder, ClientUpdateDownloadResult result) { DeleteFolder(folder); return result; }
    private static void Release(Held held) { held.Preparation.Dispose(); DeleteFolder(held.Folder); }
    private static void DeleteFolder(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* In use (a running installer) or already gone; it stays in Temp. */ }
    }
    private static string Megabytes(long bytes) => (bytes / (1024d * 1024d)).ToString("0", CultureInfo.InvariantCulture);
}
