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
    public const string UserAgent = ProjectRepositories.DelunoUserAgent;

    /// <summary>
    /// The base URL with <c>/api</c> on the end, unless it already has it or
    /// carries its own query, which means someone gave the full endpoint.
    /// </summary>
    public static string For(string baseUrl) => For(baseUrl, apiPath: null);

    /// <summary>
    /// The base URL with <paramref name="apiPath"/> on the end (#740), so a
    /// Newznab/Torznab server whose API does not live at the default
    /// <c>/api</c> can still be reached without folding a custom path into
    /// the base URL itself. A blank path keeps the existing default
    /// behaviour.
    /// </summary>
    public static string For(string baseUrl, string? apiPath)
    {
        var trimmed = NormalizeBaseUrl(baseUrl) ?? string.Empty;
        if (trimmed.Contains('?', StringComparison.Ordinal))
        {
            return trimmed;
        }

        var path = string.IsNullOrWhiteSpace(apiPath) ? "/api" : "/" + apiPath.Trim().Trim('/');
        if (trimmed.TrimEnd('/').EndsWith(path, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return trimmed.TrimEnd('/') + path;
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
