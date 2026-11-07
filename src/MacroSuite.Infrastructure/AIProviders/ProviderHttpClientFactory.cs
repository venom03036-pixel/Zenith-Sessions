using System.Collections.Concurrent;

namespace MacroSuite.Infrastructure.AIProviders;

/// <summary>
/// Factory managing long-lived, pooled <see cref="HttpClient"/> instances for AI vision providers (Rule 16).
/// Prevents socket exhaustion and configures connection pooling and timeouts.
/// Supports custom <see cref="HttpMessageHandler"/> injection for testing.
/// </summary>
public sealed class ProviderHttpClientFactory : IDisposable
{
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpMessageHandler? _customHandler;
    private readonly bool _disposeHandler;
    private bool _disposed;

    /// <summary>Default request timeout for AI vision requests.</summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public ProviderHttpClientFactory(HttpMessageHandler? customHandler = null, bool disposeHandler = true)
    {
        _customHandler = customHandler;
        _disposeHandler = disposeHandler;
    }

    /// <summary>
    /// Gets or creates a reusable, thread-safe <see cref="HttpClient"/> for the specified provider.
    /// </summary>
    public HttpClient GetClient(string providerId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        return _clients.GetOrAdd(providerId, pid =>
        {
            HttpClient client = _customHandler != null
                ? new HttpClient(_customHandler, disposeHandler: false)
                : new HttpClient(new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    EnableMultipleHttp2Connections = true
                });

            client.Timeout = DefaultTimeout;
            return client;
        });
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }
        _clients.Clear();

        if (_disposeHandler)
        {
            _customHandler?.Dispose();
        }
    }
}
