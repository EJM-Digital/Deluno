namespace Deluno.Contracts;

/// <summary>
/// The address a Newznab or Torznab server answers its API on (#624).
///
/// <para>These servers answer on <c>/api</c>, and the base URL people enter
/// usually leaves it off: NZBGeek is <c>https://api.nzbgeek.info</c>. Search
/// added the <c>/api</c>, but the connection test used the base URL as it was.
/// So the test fetched NZBGeek's HTML home page and called a working indexer
/// degraded. Both now take the address from here.</para>
/// </summary>
public static class IndexerApiEndpoint
{
    /// <summary>
    /// What Deluno calls itself to an indexer (#625). NZBGeek refuses a request
    /// with no User-Agent ("Invalid User Agent"), and neither searches nor the
    /// connection test sent one.
    /// </summary>
    public const string UserAgent = "Deluno/1.0 (+https://github.com/jampat000/Deluno)";

    /// <summary>
    /// The base URL with <c>/api</c> on the end, unless it already has it or
    /// carries its own query, which means someone gave the full endpoint.
    /// </summary>
    public static string For(string baseUrl)
    {
        var trimmed = NormalizeBaseUrl(baseUrl) ?? string.Empty;
        if (trimmed.Contains('?', StringComparison.Ordinal) ||
            trimmed.TrimEnd('/').EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return trimmed.TrimEnd('/') + "/api";
    }

    /// <summary>
    /// An indexer address the way a browser reads what is typed into its bar
    /// (#627): <c>api.nzbplanet.net</c> means <c>https://api.nzbplanet.net</c>.
    /// Saved without a scheme, it was kept as typed and then never worked,
    /// tested only as "The address is not valid yet". Null for a blank one.
    /// </summary>
    public static string? NormalizeBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        var trimmed = baseUrl.Trim();
        return trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "https://" + trimmed.TrimStart('/');
    }

    /// <summary>Whether a normalised address is one Deluno can send a request to.</summary>
    public static bool IsUsable(string? baseUrl)
        => NormalizeBaseUrl(baseUrl) is { } normalized &&
           Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
           !string.IsNullOrWhiteSpace(uri.Host);
}
