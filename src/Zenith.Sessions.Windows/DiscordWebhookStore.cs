using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>The saved webhook file is unreadable, damaged or not this Windows user's. Saving again or Clear webhook replaces it.</summary>
public sealed class DiscordWebhookStorageException(string message) : IOException(message);

/// <summary>
/// The one Discord webhook URL in a DPAPI-protected file (current user) under the writable data folder, like the private-server
/// store: the clear document exists only in memory and its buffers are zeroed after use; one writer per process plus a cross-process
/// lock file. A damaged file is reported by type, never read as "no webhook", and only an explicit Save or Clear replaces it.
/// </summary>
public sealed class DiscordWebhookStore : IDiscordWebhookStore, IDisposable
{
    public const int MaximumFileBytes = 8 * 1024;
    private const int CurrentVersion = 1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Zenith.Sessions.DiscordWebhook.v1");
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly string _root;
    private int _disposed;
    public string Path { get; }
    private sealed record Document(int Version, string Url);

    public DiscordWebhookStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("The webhook data directory is required.", nameof(root));
        _root = System.IO.Path.GetFullPath(root); Path = System.IO.Path.Combine(_root, "discord-webhook.dat");
    }

    public Task<DiscordWebhookSummary?> ReadSummaryAsync(CancellationToken ct) => SerializedAsync(async token =>
    {
        using var address = await LoadAsync(token).ConfigureAwait(false);
        return address is null ? null : new DiscordWebhookSummary(address.Host, address.Last4);
    }, ct);

    public Task<DiscordWebhookAddress?> ReadAsync(CancellationToken ct) => SerializedAsync(LoadAsync, ct);

    public Task SaveAsync(DiscordWebhookAddress address, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(address);
        return SerializedAsync(async token =>
        {
            // The clear URL is a transient string between the protected holder and the protected file, like a cookie header.
            string url = ""; address.Reveal(span => url = new string(span));
            if (!DiscordWebhookAddress.TryParse(url, out var check, out _)) throw new InvalidOperationException("The webhook was cleared while it was being saved. Nothing was written.");
            check.Dispose();
            await WriteAsync(new(CurrentVersion, url), token).ConfigureAwait(false);
            return true;
        }, ct);
    }

    public Task ClearAsync(CancellationToken ct) => SerializedAsync(token =>
    {
        if (File.Exists(Path)) File.Delete(Path);
        return Task.FromResult(true);
    }, ct);

    private async Task<T> SerializedAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this); await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                Directory.CreateDirectory(_root);
                await using var ownership = await AcquireFileLockAsync(ct).ConfigureAwait(false);
                return await operation(ct).ConfigureAwait(false);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _writer.Release(); }
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous); }
            catch (IOException ex) when (((ex.HResult & 0xffff) is 32 or 33) && attempt < 99)
            { await Task.Delay(TimeSpan.FromMilliseconds(20), ct).ConfigureAwait(false); }
        }
    }

    private async Task<DiscordWebhookAddress?> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(Path)) return null;
        byte[] protectedBytes;
        await using (var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (file.Length > MaximumFileBytes) throw new DiscordWebhookStorageException("The saved Discord webhook file is larger than any webhook. Save the webhook again or clear it.");
            protectedBytes = new byte[(int)file.Length];
            try { await file.ReadExactlyAsync(protectedBytes, ct).ConfigureAwait(false); }
            catch (EndOfStreamException) { throw new DiscordWebhookStorageException("The saved Discord webhook file was truncated while reading. Save the webhook again or clear it."); }
        }
        byte[] clear;
        try { clear = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { throw new DiscordWebhookStorageException("The saved Discord webhook could not be unprotected for this Windows user. Save the webhook again or clear it."); }
        try
        {
            string url;
            try
            {
                using var json = JsonDocument.Parse(clear, new JsonDocumentOptions { MaxDepth = 4 });
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 || root.GetProperty("Version").GetInt32() != CurrentVersion) throw new InvalidDataException();
                url = root.GetProperty("Url").GetString() ?? throw new InvalidDataException();
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException or InvalidOperationException or KeyNotFoundException)
            { throw new DiscordWebhookStorageException("The saved Discord webhook file is not readable. Save the webhook again or clear it."); }
            if (!DiscordWebhookAddress.TryParse(url, out var address, out _))
                throw new DiscordWebhookStorageException("The saved Discord webhook is not a webhook URL Zenith accepts. Save the webhook again or clear it.");
            return address;
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private async Task WriteAsync(Document document, CancellationToken ct)
    {
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(document); byte[] protectedBytes;
        try { protectedBytes = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        ct.ThrowIfCancellationRequested(); string temporary = Path + ".tmp";
        await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await file.WriteAsync(protectedBytes, ct).ConfigureAwait(false); await file.FlushAsync(ct).ConfigureAwait(false); file.Flush(true);
        }
        ct.ThrowIfCancellationRequested();
        if (File.Exists(Path))
        {
            for (int attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try { File.Replace(temporary, Path, null); break; }
                catch (IOException ex) when (((ex.HResult & 0xffff) is 32 or 33 or 1175) && attempt < 9 && File.Exists(temporary) && File.Exists(Path))
                { await Task.Delay(TimeSpan.FromMilliseconds(25), ct).ConfigureAwait(false); }
            }
        }
        else File.Move(temporary, Path);
    }

    /// <summary>Dispose after the caller has awaited all operations; this store owns no persistent worker.</summary>
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _writer.Dispose(); }
}
