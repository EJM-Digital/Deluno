using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using Deluno.Connections.Contracts;

namespace Deluno.Connections.Data;

/// <summary>
/// One pooled handler per configured HTTP/SOCKS indexer proxy (#976), so
/// every request Deluno itself makes for an indexer -- search, RSS, caps and
/// (for a native tracker) a grab -- actually goes out through the proxy
/// assigned to it, rather than only the FlareSolverr/Byparr helper's own
/// nested <c>proxy</c> parameter carrying it.
///
/// <para>Handlers are cached by proxy id, the same way
/// <see cref="IHttpClientFactory"/> pools a named client's handler, so
/// switching between many indexers that share one proxy reuses its
/// connection pool instead of opening a fresh one per request -- and so a
/// proxy assignment change (a new id, or none) never reuses a stale
/// handler built for the old one.</para>
/// </summary>
public interface IIndexerProxyHttpClientFactory
{
    /// <summary>
    /// The handler this proxy's requests share, built fresh the first time
    /// this proxy id is asked for and cached after. Null (or a disabled or
    /// FlareSolverr/Byparr proxy) shares the one direct-connection handler.
    /// </summary>
    HttpMessageHandler GetHandler(ProxyItem? proxy);

    /// <summary>A client wrapping this proxy's shared handler -- a fresh <see cref="HttpClient"/> each call, the handler itself reused.</summary>
    HttpClient CreateClient(ProxyItem? proxy);
}

public sealed class IndexerProxyHttpClientFactory : IIndexerProxyHttpClientFactory, IDisposable
{
    private const string DirectKey = "__direct__";
    private readonly ConcurrentDictionary<string, SocketsHttpHandler> handlers = new(StringComparer.Ordinal);

    public HttpMessageHandler GetHandler(ProxyItem? proxy)
    {
        var webProxy = IndexerProxyWebProxy.Build(proxy);
        var key = webProxy is null ? DirectKey : proxy!.Id;
        return handlers.GetOrAdd(key, _ => Build(webProxy));
    }

    public HttpClient CreateClient(ProxyItem? proxy)
    {
        var client = new HttpClient(GetHandler(proxy), disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(Deluno.Contracts.IndexerApiEndpoint.UserAgent);
        return client;
    }

    private static SocketsHttpHandler Build(IWebProxy? proxy) => new()
    {
        ConnectCallback = Deluno.Infrastructure.Http.LoopbackFirstConnection.ConnectAsync,
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        Proxy = proxy,
        UseProxy = proxy is not null
    };

    public void Dispose()
    {
        foreach (var handler in handlers.Values)
        {
            handler.Dispose();
        }
    }
}
