using System.Globalization;
using System.Xml.Linq;
using Deluno.Connections.Contracts;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// A saved tracker indexer, run through the engine (#606): its definition
/// found, its settings and secrets together, and its results written as the
/// Torznab feed every other indexer's results already arrive as, so the
/// planner, RSS sync and decision engine read them the same way.
/// </summary>
public sealed class TrackerIndexers(
    ITrackerDefinitionCatalog catalog,
    ITrackerEngine engine,
    ICloudflareHelper? cloudflareHelper = null,
    TimeSpan? budget = null,
    Deluno.Connections.Data.IConnectionsRepository? connections = null,
    Microsoft.Extensions.Logging.ILogger<TrackerIndexers>? logger = null) : ITrackerIndexerTester
{
    public async Task<TrackerConnection> ConnectAsync(IndexerItem indexer, CancellationToken cancellationToken)
    {
        // A definition removed or renamed since this indexer was added (or one
        // never installed after a data folder was copied elsewhere) must not
        // crash a search: it is a configuration problem this row can show and
        // a person can clear, by adding the definition back or removing the row.
        var definition = catalog.Find(indexer.DefinitionId is { } saved && TrackerSiteMapping.Retired.TryGetValue(saved, out var mapped) ? mapped : indexer.DefinitionId)
                         ?? throw new TrackerException(indexer.DefinitionId is null
                             ? $"{indexer.Name} does not say which tracker it is."
                             : $"This tracker's definition ('{indexer.DefinitionId}') is not installed. Add one to the trackers folder or remove this indexer.");

        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        // Settings this site's current file does not know are leftovers from an
        // earlier bundled version; they are dropped here, keeping all it knows.
        var known = definition.Settings.Select(setting => setting.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in indexer.DefinitionSettings ?? new Dictionary<string, string>())
        {
            if (known.Contains(name))
            {
                settings[name] = value;
            }
        }

        foreach (var (name, value) in indexer.DefinitionSecrets ?? new Dictionary<string, string>())
        {
            if (known.Contains(name))
            {
                settings[name] = value;
            }
        }

        // An API key typed into the ordinary field counts as the tracker's key.
        if (!string.IsNullOrWhiteSpace(indexer.ApiKey) && !settings.ContainsKey("apikey"))
        {
            settings["apikey"] = indexer.ApiKey;
        }

        var baseUrl = string.IsNullOrWhiteSpace(indexer.BaseUrl) ? definition.Links.FirstOrDefault() ?? string.Empty : indexer.BaseUrl;
        var (proxy, helperProxy, helperUrl) = await ResolveProxyAsync(indexer, cancellationToken);
        return new TrackerConnection(indexer.Id, definition, baseUrl, settings)
        {
            Proxy = proxy,
            HelperProxy = helperProxy,
            HelperUrl = helperUrl
        };
    }

    /// <summary>
    /// This indexer's own assigned proxy (#748, #976), resolved for both this
    /// connection's own requests and the Cloudflare helper's request (#749).
    /// An HTTP/SOCKS4/SOCKS5 proxy routes the connection's own requests
    /// directly and travels with the Cloudflare helper's request too, so a
    /// check behind it is still solved from the same exit IP. A
    /// FlareSolverr/Byparr proxy is not a forward proxy -- there is no
    /// protocol for nesting one helper's solve behind another -- so it
    /// becomes this indexer's own Cloudflare helper instead, replacing the
    /// single instance-wide helper URL as its only routing option.
    /// </summary>
    private async Task<(Deluno.Connections.Contracts.ProxyItem? Proxy, CloudflareHelperProxy? HelperProxy, string? HelperUrl)> ResolveProxyAsync(IndexerItem indexer, CancellationToken cancellationToken)
    {
        if (connections is null)
        {
            return (null, null, null);
        }

        var proxy = await Deluno.Connections.Data.IndexerProxyResolver.ResolveAsync(indexer, connections, cancellationToken);
        if (proxy is null)
        {
            return (null, null, null);
        }

        if (string.Equals(proxy.ProxyType, ProxyTypes.FlareSolverr, StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, ProxyAddressFormat.ToUrl(ProxyTypes.Http, proxy.Address));
        }

        return (proxy, new CloudflareHelperProxy(ProxyAddressFormat.ToUrl(proxy.ProxyType, proxy.Address), proxy.Username, proxy.Password), null);
    }

    /// <summary>
    /// How long one tracker gets to answer a search or a test (#634, #628).
    /// Longer with a Cloudflare helper, which can take most of a minute to clear
    /// a check: 49 seconds for 1337x on the rig.
    /// </summary>
    public async Task<TimeSpan> BudgetAsync(CancellationToken cancellationToken, IndexerItem? indexer = null)
    {
        if (budget is { } fixedBudget)
        {
            return fixedBudget;
        }

        // The helper set in Preferences, General, or this indexer's own
        // FlareSolverr/Byparr proxy (#1143): either one is a Cloudflare helper
        // that can take 14 seconds a page, so both get the longer budget.
        if (cloudflareHelper is not null && await cloudflareHelper.IsConfiguredAsync(cancellationToken))
        {
            return TimeSpan.FromSeconds(150);
        }

        if (indexer is not null && connections is not null)
        {
            var proxy = await Deluno.Connections.Data.IndexerProxyResolver.ResolveAsync(indexer, connections, cancellationToken);
            if (proxy is not null && string.Equals(proxy.ProxyType, ProxyTypes.FlareSolverr, StringComparison.OrdinalIgnoreCase))
            {
                return TimeSpan.FromSeconds(150);
            }
        }

        return TimeSpan.FromSeconds(45);
    }

    public async Task<XDocument> SearchAsync(IndexerItem indexer, TrackerQuery query, CancellationToken cancellationToken)
    {
        var result = await engine.SearchAsync(await ConnectAsync(indexer, cancellationToken), query, cancellationToken);
        return TorznabFeed.Write(result.Releases, logger);
    }

    public async Task<TrackerGrab> ResolveAsync(IndexerItem indexer, string link, CancellationToken cancellationToken)
        => await engine.ResolveDownloadAsync(await ConnectAsync(indexer, cancellationToken), link, cancellationToken);

    public async Task<(string HealthStatus, string Message, string? FailureCategory)> TestAsync(IndexerItem indexer, CancellationToken cancellationToken)
    {
        TrackerConnection connection;
        try
        {
            connection = await ConnectAsync(indexer, cancellationToken);
        }
        catch (TrackerException exception)
        {
            return ("degraded", exception.Message, "configuration");
        }

        var missing = connection.Definition.Settings
            .Where(setting => setting.Name is "username" or "password" or "cookie" or "apikey" or "passkey" or "rsskey")
            .Where(setting => connection.Definition.Login is not null || setting.Name is "apikey" or "passkey" or "rsskey")
            .Where(setting => string.IsNullOrWhiteSpace(connection.Settings.GetValueOrDefault(setting.Name)))
            .Select(setting => setting.Label ?? setting.Name)
            .ToArray();
        if (missing.Length > 0)
        {
            return ("degraded", $"{connection.Definition.Name} needs {string.Join(" and ", missing)} before Deluno can search it.", "auth");
        }

        // A Cloudflare helper can take most of a minute to clear a check: 49
        // seconds for 1337x on the rig, against the 45 a test used to get (#628).
        var budget = await BudgetAsync(cancellationToken, indexer);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(budget);
            var result = await engine.SearchAsync(connection, new TrackerQuery(string.Empty, Categories: TrackerCategories.Parse(indexer.Categories)), timeout.Token);
            var host = new Uri(connection.BaseUrl).Host;
            if (result.Releases.Count == 0 && result.RowsSkipped > 0)
            {
                return ("degraded", $"Reached {host}, but could not read its results: {result.Problems.FirstOrDefault()}", "unexpected-response");
            }

            return ("healthy", result.Releases.Count == 0
                ? $"Reached {host}; its latest page had nothing in these categories."
                : $"Reached {host} and read {result.Releases.Count} releases from its latest page.", null);
        }
        catch (TrackerException exception)
        {
            // A Cloudflare check turning every mirror away is not the same
            // as Deluno's own request never reaching one (#971, #1027): it
            // needs a Cloudflare helper set up, not a network fix, and must
            // never be counted like an outage or a bad release.
            // The helper itself not answering is a setup problem too (#1027).
            if (exception is CloudflareHelperUnavailableException || exception.InnerException is CloudflareHelperUnavailableException)
            {
                return ("degraded", exception.Message, "configuration");
            }

            if (exception.InnerException is TrackerBlockedException)
            {
                return ("degraded", exception.Message, "blocked");
            }

            return exception.Message.Contains("sign-in", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("cookie", StringComparison.OrdinalIgnoreCase)
                ? ("degraded", exception.Message, "auth")
                : ("unreachable", exception.Message, "connectivity");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ("unreachable", $"{connection.Definition.Name} did not answer within {budget.TotalSeconds:0} seconds.", "connectivity");
        }
    }
}

/// <summary>Tracker releases as a Torznab feed: the one shape Deluno reads search results in.</summary>
public static class TorznabFeed
{
    private static readonly XNamespace Torznab = "http://torznab.com/schemas/2015/feed";

    /// <summary>
    /// Text XML 1.0 can hold: a character it cannot (a stray backspace or other control
    /// character from scraped HTML, a lone surrogate) is removed, so one bad row never
    /// throws and loses the tracker's whole page.
    /// </summary>
    internal static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                builder.Append(character).Append(text[++index]);
            }
            else if (!char.IsSurrogate(character) && System.Xml.XmlConvert.IsXmlChar(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    public static XDocument Write(IEnumerable<TrackerRelease> releases, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        var skipped = new List<string?>();
        var channel = new XElement("channel", new XElement("title", "Deluno tracker search"));
        foreach (var release in releases)
        {
            // A row with nowhere to download from cannot be a result, and one such row
            // must not lose the page: it is skipped and reported once, below.
            if (string.IsNullOrWhiteSpace(release.Link))
            {
                skipped.Add(release.Title);
                continue;
            }

            var link = Clean(release.Link);
            var item = new XElement(
                "item",
                new XElement("title", Clean(release.Title)),
                new XElement("guid", release.DetailsUrl is null ? link : Clean(release.DetailsUrl)),
                new XElement("link", link),
                new XElement("enclosure",
                    new XAttribute("url", link),
                    new XAttribute("length", release.Size ?? 0),
                    new XAttribute("type", "application/x-bittorrent")));

            if (release.DetailsUrl is not null)
            {
                item.Add(new XElement("comments", Clean(release.DetailsUrl)));
            }

            if (release.PublishDate is { } published)
            {
                item.Add(new XElement("pubDate", published.ToString("r", CultureInfo.InvariantCulture)));
            }

            if (release.Size is { } size)
            {
                item.Add(new XElement("size", size));
            }

            void Attr(string name, object? value)
            {
                if (value is not null && Clean(Convert.ToString(value, CultureInfo.InvariantCulture)) is { Length: > 0 } text)
                {
                    item.Add(new XElement(Torznab + "attr", new XAttribute("name", name), new XAttribute("value", text)));
                }
            }

            foreach (var category in release.Categories)
            {
                Attr("category", category);
            }

            Attr("size", release.Size);
            Attr("seeders", release.Seeders);
            Attr("peers", release.Peers);
            Attr("grabs", release.Grabs);
            Attr("files", release.Files);
            Attr("infohash", release.InfoHash ?? TrackerMagnet.InfoHashOf(release.MagnetUrl));
            Attr("magneturl", release.MagnetUrl);
            Attr("imdbid", release.ImdbId);
            Attr("tmdbid", release.TmdbId);
            Attr("tvdbid", release.TvdbId);
            Attr("downloadvolumefactor", release.DownloadVolumeFactor);
            Attr("uploadvolumefactor", release.UploadVolumeFactor);
            Attr("minimumratio", release.MinimumRatio);
            Attr("minimumseedtime", release.MinimumSeedTimeSeconds);
            // Structured indexer flags (#745): the same signal a private
            // tracker's own site already gives, read once here so every
            // release -- native tracker or Torznab/Newznab -- reaches the
            // planner's "flags"/"flag" attrs the same way, whether or not the
            // indexer names the flag itself.
            if (release.DownloadVolumeFactor == 0)
            {
                Attr("flags", "freeleech");
            }
            else if (release.DownloadVolumeFactor == 0.5)
            {
                Attr("flags", "half leech");
            }

            if (release.UploadVolumeFactor >= 2)
            {
                Attr("flags", "double upload");
            }

            channel.Add(item);
        }

        if (skipped.Count > 0 && logger is not null)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(
                logger,
                "Skipped {Count} tracker release(s) with neither a download link nor a magnet link, for example {Title}.",
                skipped.Count,
                Clean(skipped[0]));
        }

        return new XDocument(new XElement("rss", new XAttribute("version", "2.0"), new XAttribute(XNamespace.Xmlns + "torznab", Torznab), channel));
    }
}
