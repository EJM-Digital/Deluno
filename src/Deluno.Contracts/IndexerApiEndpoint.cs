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
    /// The base URL with <c>/api</c> on the end, unless it already has it or
    /// carries its own query, which means someone gave the full endpoint.
    /// </summary>
    public static string For(string baseUrl)
    {
        var trimmed = baseUrl.Trim();
        if (trimmed.Contains('?', StringComparison.Ordinal) ||
            trimmed.TrimEnd('/').EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return trimmed.TrimEnd('/') + "/api";
    }
}
