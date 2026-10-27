using Deluno.Contracts;
using System.Net;
using System.Net.Http.Headers;
using Deluno.Connections;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.Search.Trackers;
using Microsoft.Extensions.Logging;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// What Deluno got when it followed a release's download link itself (#1062):
/// a magnet the link redirected to, or the .torrent's bytes, or the plain
/// reason it got neither. <see cref="ReleaseIsGone"/> is true only when the
/// indexer said the release no longer exists (404/410) or handed over a file
/// that is not a torrent: an indexer that is down, slow or refusing a sign-in
/// says nothing about the release and is never a bad release.
/// </summary>
public sealed record IndexerTorrentFetch(string? MagnetUrl, byte[]? TorrentFile, string? Problem = null, bool ReleaseIsGone = false, bool AuthFailed = false)
{
    public bool Succeeded => Problem is null;
}

/// <summary>
/// Follows a torrent release's download link so Deluno holds the .torrent (and
/// so knows its info-hash) before it sends anything to a client.
/// </summary>
public interface IIndexerTorrentFetcher
{
    Task<IndexerTorrentFetch> FetchAsync(IndexerItem? indexer, string url, CancellationToken cancellationToken);
}

/// <summary>
/// The fetch goes out the way the indexer's own requests do: through the proxy
/// assigned to it, with its API key already in the link, and, when the link
/// sits behind a Cloudflare check and a helper is set up, with the clearance
/// the helper won. It reads at most <see cref="MaxTorrentBytes"/>, and takes a
/// redirect that ends in a magnet link as the answer.
/// </summary>
public sealed class IndexerTorrentFetcher(
    IHttpClientFactory httpClientFactory,
    IConnectionsRepository? connectionsRepository = null,
    IIndexerProxyHttpClientFactory? proxyHttpClientFactory = null,
    ICloudflareHelper? cloudflareHelper = null,
    ILogger<IndexerTorrentFetcher>? logger = null) : IIndexerTorrentFetcher
{
    /// <summary>The biggest .torrent Deluno reads from an indexer; the same cap trackers use.</summary>
    public const int MaxTorrentBytes = 5 * 1024 * 1024;

    public async Task<IndexerTorrentFetch> FetchAsync(IndexerItem? indexer, string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return new IndexerTorrentFetch(null, null, "The release's download link is not a web address Deluno can fetch.", ReleaseIsGone: true);
        }

        var name = indexer?.Name ?? uri.Host;
        try
        {
            using var http = await CreateClientAsync(indexer, cancellationToken);
            var first = await GetAsync(http, uri, null, null, cancellationToken);
            if (first.IsChallenge && cloudflareHelper is not null && indexer is not null)
            {
                var (helperProxy, helperUrl) = await ResolveHelperAsync(indexer, cancellationToken);
                if (await cloudflareHelper.IsConfiguredAsync(cancellationToken, helperUrl))
                {
                    var solved = await cloudflareHelper.FetchAsync(uri, cancellationToken, helperProxy, helperUrl);
                    if (solved is not null)
                    {
                        var cookie = string.Join("; ", solved.Cookies.Select(item => $"{item.Name}={item.Value}"));
                        var retried = await GetAsync(http, uri, cookie, solved.UserAgent, cancellationToken);
                        if (!retried.IsChallenge)
                        {
                            first = retried;
                        }
                    }
                }
            }

            return Interpret(name, first);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            if (exception is TaskCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            logger?.LogInformation(exception, "Could not fetch a release from {Indexer}.", name.ForLog());
            return new IndexerTorrentFetch(null, null, $"{name} did not answer when Deluno asked for the release: {exception.Message}");
        }
    }

    private static IndexerTorrentFetch Interpret(string name, Fetched fetched)
    {
        if (fetched.Magnet is { } magnet)
        {
            return new IndexerTorrentFetch(magnet, null);
        }

        if (fetched.Status is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return new IndexerTorrentFetch(null, null, $"{name} no longer has this release (it answered {(int)fetched.Status}).", ReleaseIsGone: true);
        }

        if (fetched.IsChallenge)
        {
            return new IndexerTorrentFetch(null, null, $"{name} is behind a Cloudflare check, so Deluno could not fetch the release. Set up a Cloudflare helper for it.");
        }

        if (fetched.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new IndexerTorrentFetch(
                null, null,
                $"{name} refused to hand over the release (it answered {(int)fetched.Status}). Its API key or sign-in may be wrong, or it may block Deluno's download requests; check it in Indexers.",
                AuthFailed: true);
        }

        if ((int)fetched.Status >= 400)
        {
            return new IndexerTorrentFetch(null, null, $"{name} refused to hand over the release (it answered {(int)fetched.Status}).");
        }

        if (fetched.TooBig)
        {
            return new IndexerTorrentFetch(null, null, $"{name} sent a file bigger than a torrent can be ({MaxTorrentBytes / (1024 * 1024)} MB), so Deluno did not take it.", ReleaseIsGone: true);
        }

        if (!LooksLikeBencodeDictionary(fetched.Body))
        {
            // A page rather than a file: a sign-in page, a rate-limit notice.
            // It says nothing certain about the release, so it is not a bad one.
            return new IndexerTorrentFetch(null, null, $"{name} answered with a web page instead of a torrent file. Its sign-in, API key or download limit may be the reason.");
        }

        return new IndexerTorrentFetch(null, fetched.Body);
    }

    /// <summary>
    /// A torrent file is a bencoded dictionary: a 'd' then a key length or the
    /// closing 'e'. Plain text that happens to start with a 'd' (a daily-limit
    /// notice) is a page, not a file (#1062).
    /// </summary>
    internal static bool LooksLikeBencodeDictionary(byte[] body)
        => body.Length >= 2 && body[0] == (byte)'d' && (body[1] is >= (byte)'0' and <= (byte)'9' || body[1] == (byte)'e');

    private static async Task<Fetched> GetAsync(HttpClient http, Uri uri, string? cookie, string? userAgent, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/x-bittorrent, application/octet-stream;q=0.9, */*;q=0.5");
        if (!string.IsNullOrEmpty(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        if (!string.IsNullOrEmpty(userAgent))
        {
            request.Headers.UserAgent.Clear();
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.Headers.Location is { } location
            && location.OriginalString.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return new Fetched(response.StatusCode, [], location.OriginalString, false, false);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        var tooBig = false;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxTorrentBytes)
            {
                tooBig = true;
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        var body = buffer.ToArray();
        var challenge = (int)response.StatusCode is 403 or 503 or 429 && LooksLikeCloudflare(body);
        return new Fetched(response.StatusCode, body, null, tooBig, challenge);
    }

    private static bool LooksLikeCloudflare(byte[] body)
    {
        if (body.Length == 0 || body[0] == (byte)'d')
        {
            return false;
        }

        var text = System.Text.Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 64 * 1024));
        return text.Contains("cf-chl", StringComparison.Ordinal)
            || text.Contains("challenge-platform", StringComparison.Ordinal)
            || text.Contains("<title>Just a moment", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Attention Required! | Cloudflare", StringComparison.Ordinal);
    }

    private async Task<HttpClient> CreateClientAsync(IndexerItem? indexer, CancellationToken cancellationToken)
    {
        HttpClient http;
        var proxy = indexer is not null && connectionsRepository is not null && !string.IsNullOrWhiteSpace(indexer.ProxyId)
            ? await IndexerProxyResolver.ResolveAsync(indexer, connectionsRepository, cancellationToken)
            : null;
        http = proxy is not null && proxyHttpClientFactory is not null
            ? proxyHttpClientFactory.CreateClient(proxy)
            : httpClientFactory.CreateClient("indexers");
        http.Timeout = TimeSpan.FromSeconds(20);
        return http;
    }

    private async Task<(CloudflareHelperProxy? Proxy, string? HelperUrl)> ResolveHelperAsync(IndexerItem indexer, CancellationToken cancellationToken)
    {
        if (connectionsRepository is null || string.IsNullOrWhiteSpace(indexer.ProxyId))
        {
            return (null, null);
        }

        var proxy = await IndexerProxyResolver.ResolveAsync(indexer, connectionsRepository, cancellationToken);
        if (proxy is null)
        {
            return (null, null);
        }

        return string.Equals(proxy.ProxyType, ProxyTypes.FlareSolverr, StringComparison.OrdinalIgnoreCase)
            ? (null, ProxyAddressFormat.ToUrl(ProxyTypes.Http, proxy.Address))
            : (new CloudflareHelperProxy(ProxyAddressFormat.ToUrl(proxy.ProxyType, proxy.Address), proxy.Username, proxy.Password), null);
    }

    private sealed record Fetched(HttpStatusCode Status, byte[] Body, string? Magnet, bool TooBig, bool IsChallenge);
}
