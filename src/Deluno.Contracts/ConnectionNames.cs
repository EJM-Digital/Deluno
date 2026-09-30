namespace Deluno.Contracts;

/// <summary>
/// #1076: a connection's name is worked out, never typed. A download client,
/// processor, media server, notification connection or Seerr server is
/// "App on host" ("qBittorrent on RIG"), an indexer is its site ("1337x") and
/// an import list is its provider and list. A machine's own address
/// (localhost, 127.0.0.1, ::1) is written as this machine's hostname.
///
/// <para>Two connections of one kind on one host tell themselves apart by port
/// ("qBittorrent on RIG:8081"), then by a short suffix. Nothing looks a
/// connection up by its name: every link is the connection's id.</para>
/// </summary>
public static class ConnectionNames
{
    public static bool IsLoopback(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var value = host.Trim().Trim('[', ']');
        return value.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || value.Equals("::1", StringComparison.Ordinal)
            || value.StartsWith("127.", StringComparison.Ordinal);
    }

    /// <summary>The host part of a bare host, host:port or full address; this machine's hostname for a loopback address. Null when there is none.</summary>
    public static string? HostOf(string? hostOrUrl)
    {
        var host = RawHostOf(hostOrUrl);
        if (host is null)
        {
            return null;
        }

        return IsLoopback(host) ? DelunoHostName.Current : host;
    }

    /// <summary>The port written in a bare host:port or full address, when one is written.</summary>
    public static int? PortOf(string? hostOrUrl)
        => TryParse(hostOrUrl, out var uri) && !uri.IsDefaultPort ? uri.Port : null;

    /// <summary>"app on host", or just the app when no host is known.</summary>
    public static string AppOnHost(string app, string? hostOrUrl)
    {
        var host = HostOf(hostOrUrl);
        return host is null ? app : $"{app} on {host}";
    }

    /// <summary>The site an indexer's address belongs to: no www or api prefix, no port, no path ("nzbgeek.info").</summary>
    public static string SiteName(string? baseUrl, string fallback = "Indexer")
    {
        var host = RawHostOf(baseUrl);
        if (host is null)
        {
            return fallback;
        }

        // Behind Jackett or Prowlarr the host is the aggregator's, shared by every
        // indexer it serves; the tracker is in the path.
        var tracker = AggregatedTrackerOf(baseUrl);
        if (tracker is not null)
        {
            return tracker;
        }

        if (IsLoopback(host))
        {
            return DelunoHostName.Current;
        }

        foreach (var prefix in new[] { "www.", "api." })
        {
            if (host.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && host.Count(c => c == '.') >= 2)
            {
                host = host[prefix.Length..];
            }
        }

        return host;
    }

    /// <summary>
    /// The tracker an indexer address inside Jackett ("/api/v2.0/indexers/1337x/results/torznab")
    /// points at: the Jackett indexer id. Null for an address that is not one (or Jackett's "all" aggregate).
    /// A "/12/api" address is only a Prowlarr indexer once Prowlarr has answered for it (#1076), which
    /// needs a network call; an ordinary Newznab site can have the same path shape, so until then it
    /// is named after its host like any other indexer.
    /// </summary>
    public static string? AggregatedTrackerOf(string? baseUrl)
        => AggregatorOf(baseUrl) is { Kind: AggregatorKind.Jackett } aggregator ? aggregator.Id : null;

    public enum AggregatorKind { Jackett, Prowlarr }

    /// <param name="Id">Jackett's indexer id (the tracker's own id), or Prowlarr's numeric indexer id.</param>
    /// <param name="UrlBase">The path in front of the aggregator's own routes ("" or "/prowlarr"), without a trailing slash.</param>
    public sealed record AggregatorAddress(AggregatorKind Kind, string Id, string Origin, string UrlBase);

    /// <summary>Which aggregator an indexer address belongs to, which of its indexers it is, and where the aggregator lives.</summary>
    public static AggregatorAddress? AggregatorOf(string? baseUrl)
    {
        if (!TryParse(baseUrl, out var uri))
        {
            return null;
        }

        var origin = uri.GetLeftPart(UriPartial.Authority);

        var parts = uri.Segments.Select(part => Uri.UnescapeDataString(part.Trim('/'))).Where(part => part.Length > 0).ToArray();
        for (var index = 0; index + 1 < parts.Length; index++)
        {
            if (parts[index].Equals("indexers", StringComparison.OrdinalIgnoreCase)
                && !parts[index + 1].Equals("all", StringComparison.OrdinalIgnoreCase)
                && index + 2 < parts.Length
                && parts[index + 2].Equals("results", StringComparison.OrdinalIgnoreCase))
            {
                return new AggregatorAddress(AggregatorKind.Jackett, parts[index + 1], origin, string.Empty);
            }
        }

        // Prowlarr: {urlBase}/{indexer id}/api, the url base being anything (or nothing) in front.
        for (var index = 0; index + 1 < parts.Length; index++)
        {
            if (parts[index].Length > 0 && parts[index].All(char.IsDigit) && parts[index + 1].Equals("api", StringComparison.OrdinalIgnoreCase))
            {
                var urlBase = index == 0 ? string.Empty : "/" + string.Join('/', parts.Take(index).Select(Uri.EscapeDataString));
                return new AggregatorAddress(AggregatorKind.Prowlarr, parts[index], origin, urlBase);
            }
        }

        return null;
    }

    /// <summary>An import list: its provider and the list it reads ("Trakt: my-list").</summary>
    public static string ImportList(string? provider, string? feed)
    {
        var app = ProviderLabel(provider);
        var list = ListOf(feed);
        return list is null ? app : $"{app}: {list}";
    }

    public static string ProviderLabel(string? provider)
    {
        var value = provider?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return "List";
        }

        return value.ToLowerInvariant() switch
        {
            "tmdb" => "TMDb list",
            "tmdb-person" => "TMDb person",
            "tmdb-discover" => "TMDb discover",
            "tmdb-collection" => "TMDb collection",
            "tmdb-company-credits" => "TMDb company",
            "tmdb-popular" => "TMDb popular",
            "imdb" => "IMDb",
            "trakt" => "Trakt",
            "plex" or "plex-watchlist" => "Plex watchlist",
            _ => char.ToUpperInvariant(value[0]) + value[1..]
        };
    }

    /// <summary>The download client protocols, as their own products spell them.</summary>
    public static string DownloadClientApp(string? protocol)
        => protocol?.Trim().ToLowerInvariant() switch
        {
            "qbittorrent" => "qBittorrent",
            "sabnzbd" => "SABnzbd",
            "nzbget" => "NZBGet",
            "transmission" => "Transmission",
            "deluge" => "Deluge",
            "utorrent" => "uTorrent",
            "rtorrent" => "rTorrent",
            null or "" => "Download client",
            var other => char.ToUpperInvariant(other[0]) + other[1..]
        };

    /// <summary>
    /// The name to give one connection: <paramref name="name"/> if no other connection of
    /// its kind has it, else with its port, else with a short suffix from its id.
    /// </summary>
    public static string Unique(string name, int? port, string id, IEnumerable<string?> takenByOthers)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in takenByOthers)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                taken.Add(value);
            }
        }

        if (!taken.Contains(name))
        {
            return name;
        }

        var withPort = port is null || name.EndsWith($":{port}", StringComparison.Ordinal) ? name : $"{name}:{port}";
        if (!taken.Contains(withPort))
        {
            return withPort;
        }

        var seed = new string((id ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
        for (var length = Math.Min(4, seed.Length); length > 0 && length <= seed.Length; length++)
        {
            var candidate = $"{withPort} ({seed[^length..]})";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        var counter = 2;
        while (taken.Contains($"{withPort} ({counter})"))
        {
            counter++;
        }

        return $"{withPort} ({counter})";
    }

    /// <summary>
    /// Names a whole kind at once (the migration): a name nobody else shares stays as it is,
    /// a shared one takes each connection's port, and what still clashes takes a suffix.
    /// Rows are (id, worked-out name, port), in creation order.
    /// </summary>
    public static IReadOnlyDictionary<string, string> AssignAll(IReadOnlyList<(string Id, string Name, int? Port)> rows)
    {
        var shared = rows.GroupBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = new List<string>();
        foreach (var row in rows)
        {
            var wanted = shared.Contains(row.Name) && row.Port is not null ? $"{row.Name}:{row.Port}" : row.Name;
            var final = Unique(wanted, row.Port, row.Id, used);
            used.Add(final);
            result[row.Id] = final;
        }

        return result;
    }

    private static string? ListOf(string? feed)
    {
        var value = feed?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var segment = uri.Segments.Select(part => part.Trim('/')).LastOrDefault(part => part.Length > 0);
            return string.IsNullOrEmpty(segment) ? uri.Host : Uri.UnescapeDataString(segment);
        }

        return value.Length > 60 ? value[..60] : value;
    }

    private static string? RawHostOf(string? hostOrUrl)
        => TryParse(hostOrUrl, out var uri) && !string.IsNullOrEmpty(uri.Host) ? uri.Host.Trim('[', ']') : null;

    private static bool TryParse(string? hostOrUrl, out Uri uri)
    {
        uri = null!;
        var value = hostOrUrl?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(absolute.Host))
        {
            uri = absolute;
            return true;
        }

        // A bare IPv6 address ("::1") needs its brackets to be an authority.
        var authority = value.Count(c => c == ':') > 1 && !value.StartsWith('[') ? $"[{value}]" : value;
        return Uri.TryCreate($"http://{authority}", UriKind.Absolute, out uri!) && !string.IsNullOrEmpty(uri.Host);
    }
}
