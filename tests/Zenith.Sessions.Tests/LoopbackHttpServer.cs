using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Zenith.Sessions.Tests;

/// <summary>
/// A plain-http server on 127.0.0.1 with a port the system picks, so a production HTTP handler is exercised over a real
/// socket instead of ending at a fake HttpMessageHandler. Each canned raw response answers exactly one connection (it
/// says Connection: close), and every raw request is kept as text, so a test sees what actually crossed the socket.
/// The server owns its listener and its serving task; DisposeAsync stops both. Each exchange is bounded by 5 s, and a
/// timeout is rethrown from DisposeAsync instead of being swallowed. A canned response that no client asked for is
/// never sent, so a test can offer a response that only a mistake (a followed redirect) would collect.
/// </summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(5);
    private const int MaximumRequestBytes = 65536;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _requests = [];
    private readonly Task _serving;

    public LoopbackHttpServer(params string[] responses)
    {
        _listener.Start();
        BaseAddress = new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/");
        _serving = ServeAsync(responses, _stop.Token);
    }

    public Uri BaseAddress { get; }
    public IReadOnlyList<string> Requests { get { lock (_requests) return [.. _requests]; } }

    /// <summary>A raw HTTP/1.1 response that closes its connection; each header is one "Name: value" line.</summary>
    public static string Response(string status, string body = "", params string[] headers)
    {
        var text = new StringBuilder("HTTP/1.1 ").Append(status).Append("\r\n");
        foreach (string header in headers) text.Append(header).Append("\r\n");
        return text.Append("Content-Length: ").Append(Encoding.UTF8.GetByteCount(body)).Append("\r\nConnection: close\r\n\r\n").Append(body).ToString();
    }

    public static string RequestLine(string request) => request[..request.IndexOf("\r\n", StringComparison.Ordinal)];

    /// <summary>Every value of one request header, by case-insensitive name, as written on the wire.</summary>
    public static string[] HeaderValues(string request, string name)
    {
        string head = request[..request.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
        return head.Split("\r\n").Skip(1)
            .Where(line => line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            .Select(line => line[(name.Length + 1)..].Trim()).ToArray();
    }

    private async Task ServeAsync(string[] responses, CancellationToken ct)
    {
        foreach (string response in responses)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            using var exchange = CancellationTokenSource.CreateLinkedTokenSource(ct);
            exchange.CancelAfter(ExchangeTimeout);
            var stream = client.GetStream();
            try
            {
                string request = await ReadRequestAsync(stream, exchange.Token).ConfigureAwait(false);
                lock (_requests) _requests.Add(request);
                try
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(response), exchange.Token).ConfigureAwait(false);
                    client.Client.Shutdown(SocketShutdown.Send);
                }
                // A client that refuses the response (an oversized header) may hang up before all of it is written;
                // the test asserts on what the client reported, so the early hang-up is the expected ending here.
                catch (IOException) { }
                catch (SocketException) { }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("The loopback exchange did not finish within " + ExchangeTimeout.TotalSeconds + " s.");
            }
        }
    }

    private static async Task<string> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var received = new StringBuilder();
        byte[] chunk = new byte[4096];
        int expected = -1;
        while (expected < 0 || received.Length < expected)
        {
            int read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0) break; // The client hung up.
            received.Append(Encoding.Latin1.GetString(chunk, 0, read));
            if (received.Length > MaximumRequestBytes) throw new InvalidDataException("The loopback request is larger than any test request.");
            if (expected >= 0) continue;
            string text = received.ToString();
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0) continue;
            int length = HeaderValues(text, "Content-Length") is [string value] ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            expected = end + 4 + length;
        }
        return received.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _serving.ConfigureAwait(false); }
        catch (OperationCanceledException) { } // Disposal while waiting for a connection no client opened.
        finally { _listener.Stop(); _stop.Dispose(); }
    }
}
