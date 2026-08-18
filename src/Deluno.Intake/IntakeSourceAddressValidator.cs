using System.Text.RegularExpressions;

namespace Deluno.Intake;

/// <summary>
/// Keeps the supported import-list providers explicit. This is deliberately
/// address validation only: fetching and credential checks happen through the
/// preview/sync path and never require a browser to expose provider secrets.
/// </summary>
public static partial class IntakeSourceAddressValidator
{
    public static string? Validate(string? provider, string? address)
    {
        var normalizedProvider = provider?.Trim().ToLowerInvariant();
        var value = address?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Add the source URL or identifier Deluno should poll.";
        }

        return normalizedProvider switch
        {
            "tmdb" when TmdbListIdRegex().IsMatch(value) || IsHostedBy(value, "themoviedb.org") || IsHostedBy(value, "tmdb.org")
                => null,
            "tmdb" => "Use a TMDb list URL or its numeric list ID.",
            "imdb" when ImdbListIdRegex().IsMatch(value) || IsHostedBy(value, "imdb.com")
                => null,
            "imdb" => "Use an IMDb list URL, an IMDb CSV export URL, or an ls… list ID.",
            "trakt" when TraktUsernameRegex().IsMatch(value) || IsHostedBy(value, "trakt.tv")
                => null,
            "trakt" => "Use a Trakt list/watchlist URL or a Trakt username.",
            "mdblist" when IsHostedBy(value, "mdblist.com")
                => null,
            "mdblist" => "Use a public MDbList URL such as https://mdblist.com/lists/owner/list-name.",
            "letterboxd" when IsHostedBy(value, "letterboxd.com")
                => null,
            "letterboxd" => "Use a public Letterboxd URL or RSS feed from letterboxd.com.",
            "rss" or "url-list" when IsHttpUrl(value)
                => null,
            "rss" => "Use a public HTTP or HTTPS RSS/Atom feed URL.",
            "url-list" => "Use a public HTTP or HTTPS URL containing one title per line.",
            _ => "Choose a supported import-list provider."
        };
    }

    private static bool IsHostedBy(string value, string domain)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
           (string.Equals(uri.Host, domain, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase));

    private static bool IsHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    [GeneratedRegex(@"^\d{3,}$")]
    private static partial Regex TmdbListIdRegex();

    [GeneratedRegex(@"^ls\d{4,}$", RegexOptions.IgnoreCase)]
    private static partial Regex ImdbListIdRegex();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{1,62}$", RegexOptions.IgnoreCase)]
    private static partial Regex TraktUsernameRegex();
}
