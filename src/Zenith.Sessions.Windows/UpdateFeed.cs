using System.Net;

namespace Zenith.Sessions.Windows;

public interface IUpdateFeed
{
    Task<Stream> OpenAsync(Uri uri, CancellationToken cancellationToken);
}

/// <summary>No cookies, credentials, redirects, browser identity, or background activity.</summary>
public sealed class HttpUpdateFeed : IUpdateFeed, IDisposable
{
    private readonly HttpClient _client = new(new HttpClientHandler
    { AllowAutoRedirect = false, UseCookies = false, Credentials = null, AutomaticDecompression = DecompressionMethods.None })
    { Timeout = TimeSpan.FromSeconds(30) };
    public async Task<Stream> OpenAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0) throw new InvalidDataException("Updates require HTTPS.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > SignedUpdateVerifier.MaximumPackageBytes) throw new InvalidDataException("Update is too large.");
            return new ResponseStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), response);
        }
        catch { response.Dispose(); throw; }
    }
    public void Dispose() => _client.Dispose();
    private sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); response.Dispose(); } base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Explicit offline tooling seam. The HTTP adapter never accepts file feeds.</summary>
public sealed class LocalUpdateFeed : IUpdateFeed
{
    public Task<Stream> OpenAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!uri.IsFile || uri.IsUnc) throw new InvalidDataException("Only a local file feed is allowed.");
        return Task.FromResult<Stream>(new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read));
    }
}
