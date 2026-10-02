using System.Collections.Concurrent;
using Deluno.Connections.Contracts;
using Deluno.Contracts;

namespace Deluno.Connections.Versions;

/// <summary>
/// #1117: the version of the Prowlarr or Jackett an indexer sits behind, asked of the aggregator's own status
/// API. A plain Torznab or Newznab indexer has no aggregator and so no version; nothing is guessed for it.
/// Any failure is "unknown": the indexer's own check never fails because of this.
/// </summary>
public static class AggregatorVersions
{
    /// <summary>Many indexers sit behind one aggregator, so its answer is kept for a few minutes rather than asked for once per indexer.</summary>
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, (string? Version, DateTimeOffset Until)> Recent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The aggregator's project key ("prowlarr" or "jackett"), or null when the address is not behind one.</summary>
    public static string? ProjectOf(string? baseUrl)
        => ConnectionNames.AggregatorOf(baseUrl)?.Kind switch
        {
            ConnectionNames.AggregatorKind.Prowlarr => ConnectionProjects.Prowlarr,
            ConnectionNames.AggregatorKind.Jackett => ConnectionProjects.Jackett,
            _ => null
        };

    public static async Task<string?> ReadAsync(IndexerItem item, HttpClient httpClient, CancellationToken cancellationToken)
    {
        var address = ConnectionNames.AggregatorOf(item.BaseUrl);
        if (address is null || string.IsNullOrWhiteSpace(item.ApiKey))
        {
            return null;
        }

        var key = $"{address.Kind}|{address.Origin}{address.UrlBase}";
        if (Recent.TryGetValue(key, out var recent) && recent.Until > DateTimeOffset.UtcNow)
        {
            return recent.Version;
        }

        string? version = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var request = address.Kind == ConnectionNames.AggregatorKind.Prowlarr
                ? new HttpRequestMessage(HttpMethod.Get, $"{address.Origin}{address.UrlBase}/api/v1/system/status")
                : new HttpRequestMessage(HttpMethod.Get, $"{address.Origin}/api/v2.0/server/config?apikey={Uri.EscapeDataString(item.ApiKey)}");
            if (address.Kind == ConnectionNames.AggregatorKind.Prowlarr)
            {
                request.Headers.TryAddWithoutValidation("X-Api-Key", item.ApiKey);
            }

            request.Headers.UserAgent.ParseAdd(IndexerApiEndpoint.UserAgent);
            using var response = await httpClient.SendAsync(request, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                version = address.Kind == ConnectionNames.AggregatorKind.Prowlarr
                    ? ConnectionVersionParsers.FromProwlarr(body)
                    : ConnectionVersionParsers.FromJackett(body);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            version = null;
        }

        Recent[key] = (version, DateTimeOffset.UtcNow + KeepFor);
        return version;
    }
}
