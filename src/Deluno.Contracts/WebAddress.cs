namespace Deluno.Contracts;

/// <summary>
/// An address a person types for a server, indexer, client or download: an
/// absolute http or https address with a host. <c>Uri.TryCreate</c> alone is
/// not enough: on Linux "/x.torrent" parses as the file address
/// <c>file:///x.torrent</c> (on Windows it does not), and "C:\x" and
/// "magnet:?..." parse as absolute addresses too.
/// </summary>
public static class WebAddress
{
    public static bool TryParse(string? value, out Uri uri)
    {
        uri = null!;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)
            || !Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(parsed.Host))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    public static bool IsValid(string? value) => TryParse(value, out _);

    /// <summary>A download link a client can take: a web address or a magnet link.</summary>
    public static bool IsDownloadLink(string? value)
    {
        var text = value?.Trim();
        return IsValid(text)
            || (text is not null && text.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase));
    }
}
