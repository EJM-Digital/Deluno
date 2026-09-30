using System.Text.Json;
using System.Xml.Linq;
using Deluno.Connections.Contracts;
using Deluno.Contracts;

namespace Deluno.Connections.Data;

/// <summary>
/// #1076: an indexer behind Prowlarr or Jackett is named after its tracker. The address only
/// carries an id, so this asks the aggregator: Prowlarr for the indexer's name, Jackett for
/// the title in its caps answer (unless that is just "Jackett"). Any failure is "no name" and
/// the path-derived name stays; nothing here ever fails a save.
/// </summary>
public static class AggregatorIndexerNames
{
    /// <summary>Only a changed address, API path or key is worth asking the aggregator about again; a toggle or priority change is not.</summary>
    public static bool LookupNeeded(IndexerItem before, IndexerItem after)
        => !string.Equals(before.BaseUrl, after.BaseUrl, StringComparison.Ordinal)
            || !string.Equals(before.ApiKey, after.ApiKey, StringComparison.Ordinal)
            || !string.Equals(before.ApiPath, after.ApiPath, StringComparison.Ordinal);

    public static async Task<string?> DiscoverAsync(IndexerItem item, HttpClient httpClient, CancellationToken cancellationToken)
    {
        var address = ConnectionNames.AggregatorOf(item.BaseUrl);
        if (address is null || string.IsNullOrWhiteSpace(item.ApiKey))
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            return address.Kind == ConnectionNames.AggregatorKind.Prowlarr
                ? await ProwlarrNameAsync(address, item.ApiKey, httpClient, timeout.Token)
                : await JackettTitleAsync(item, httpClient, timeout.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    // Prowlarr's own API: GET {urlBase}/api/v1/indexer/{id} answers the indexer with its "name".
    // The key in a Prowlarr Torznab/Newznab address is Prowlarr's own API key.
    private static async Task<string?> ProwlarrNameAsync(ConnectionNames.AggregatorAddress address, string apiKey, HttpClient httpClient, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{address.Origin}{address.UrlBase}/api/v1/indexer/{address.Id}");
        request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        request.Headers.UserAgent.ParseAdd(IndexerApiEndpoint.UserAgent);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.String
                ? Clean(name.GetString())
                : null;
    }

    // Jackett's t=caps answer names its server; the tracker's own title, or just "Jackett".
    private static async Task<string?> JackettTitleAsync(IndexerItem item, HttpClient httpClient, CancellationToken cancellationToken)
    {
        var uri = new Uri(IndexerApiEndpoint.For(item.BaseUrl, item.ApiPath));
        var separator = string.IsNullOrWhiteSpace(uri.Query) ? "?" : "&";
        var url = $"{uri}{separator}t=caps&apikey={Uri.EscapeDataString(item.ApiKey!)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(IndexerApiEndpoint.UserAgent);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var title = Clean(XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken))
            .Descendants().FirstOrDefault(element => element.Name.LocalName.Equals("server", StringComparison.OrdinalIgnoreCase))
            ?.Attribute("title")?.Value);
        return title is null || title.Equals("Jackett", StringComparison.OrdinalIgnoreCase) ? null : title;
    }

    private static string? Clean(string? name)
    {
        var value = name?.Trim();
        return string.IsNullOrEmpty(value) ? null : value.Length > 80 ? value[..80] : value;
    }
}
