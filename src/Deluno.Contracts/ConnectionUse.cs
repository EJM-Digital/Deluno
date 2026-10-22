namespace Deluno.Contracts;

/// <summary>
/// Says "Deluno just used this connection" (#1098): a title handed to a processor, a media server sent a
/// refresh, a Seerr request received, a subtitle fetched, a list checked. The reporter keeps the use where a
/// day's count needs it, works out what the connection has done since the person's midnight and pushes a
/// <c>ConnectionUsed</c> event. It never fails or undoes what it reports.
/// </summary>
public interface IConnectionUseReporter
{
    /// <param name="kind">One of <see cref="ConnectionKinds"/>.</param>
    /// <param name="id">The connection's id; for lists, the provider family key from <see cref="ListProviderFamilies"/>.</param>
    Task ReportAsync(string kind, string id, string name, CancellationToken cancellationToken);
}

/// <summary>Groups the import-list providers (tmdb, tmdb-popular, trakt-trending...) into the family a person knows them by.</summary>
public static class ListProviderFamilies
{
    public static string KeyFor(string? provider)
    {
        var key = (provider ?? string.Empty).Trim().ToLowerInvariant();
        foreach (var family in new[] { "tmdb", "trakt", "mdblist", "letterboxd", "stevenlu" })
        {
            if (key == family || key.StartsWith(family + "-", StringComparison.Ordinal))
            {
                return family;
            }
        }

        return string.IsNullOrEmpty(key) ? "other" : key;
    }

    public static string NameFor(string? provider) => KeyFor(provider) switch
    {
        "tmdb" => "TMDb",
        "trakt" => "Trakt",
        "mdblist" => "MDBList",
        "letterboxd" => "Letterboxd",
        "stevenlu" => "StevenLu",
        "rss" => "RSS feeds",
        "url-list" => "Web lists",
        "other" => "Lists",
        var other => char.ToUpperInvariant(other[0]) + other[1..]
    };
}
