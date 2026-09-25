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
    TimeSpan? budget = null) : ITrackerIndexerTester
{
    public TrackerConnection Connect(IndexerItem indexer)
    {
        // A definition removed or renamed since this indexer was added (or one
        // never installed after a data folder was copied elsewhere) must not
        // crash a search: it is a configuration problem this row can show and
        // a person can clear, by adding the definition back or removing the row.
        var definition = catalog.Find(indexer.DefinitionId)
                         ?? throw new TrackerException(indexer.DefinitionId is null
                             ? $"{indexer.Name} does not say which tracker it is."
                             : $"This tracker's definition ('{indexer.DefinitionId}') is not installed. Add one to the trackers folder or remove this indexer.");

        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, value) in indexer.DefinitionSettings ?? new Dictionary<string, string>())
        {
            settings[name] = value;
        }

        foreach (var (name, value) in indexer.DefinitionSecrets ?? new Dictionary<string, string>())
        {
            settings[name] = value;
        }

        // An API key typed into the ordinary field counts as the tracker's key.
        if (!string.IsNullOrWhiteSpace(indexer.ApiKey) && !settings.ContainsKey("apikey"))
        {
            settings["apikey"] = indexer.ApiKey;
        }

        var baseUrl = string.IsNullOrWhiteSpace(indexer.BaseUrl) ? definition.Links.FirstOrDefault() ?? string.Empty : indexer.BaseUrl;
        return new TrackerConnection(indexer.Id, definition, baseUrl, settings);
    }

    /// <summary>
    /// How long one tracker gets to answer a search or a test (#634, #628).
    /// Longer with a Cloudflare helper, which can take most of a minute to clear
    /// a check: 49 seconds for 1337x on the rig.
    /// </summary>
    public async Task<TimeSpan> BudgetAsync(CancellationToken cancellationToken)
        => budget is { } fixedBudget
            ? fixedBudget
            : cloudflareHelper is not null && await cloudflareHelper.IsConfiguredAsync(cancellationToken)
            ? TimeSpan.FromSeconds(150)
            : TimeSpan.FromSeconds(45);

    public async Task<XDocument> SearchAsync(IndexerItem indexer, TrackerQuery query, CancellationToken cancellationToken)
    {
        var result = await engine.SearchAsync(Connect(indexer), query, cancellationToken);
        return TorznabFeed.Write(result.Releases);
    }

    public Task<TrackerGrab> ResolveAsync(IndexerItem indexer, string link, CancellationToken cancellationToken)
        => engine.ResolveDownloadAsync(Connect(indexer), link, cancellationToken);

    public async Task<(string HealthStatus, string Message, string? FailureCategory)> TestAsync(IndexerItem indexer, CancellationToken cancellationToken)
    {
        TrackerConnection connection;
        try
        {
            connection = Connect(indexer);
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
        var budget = await BudgetAsync(cancellationToken);
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

    public static XDocument Write(IEnumerable<TrackerRelease> releases)
    {
        var channel = new XElement("channel", new XElement("title", "Deluno tracker search"));
        foreach (var release in releases)
        {
            var link = release.Link!;
            var item = new XElement(
                "item",
                new XElement("title", release.Title),
                new XElement("guid", release.DetailsUrl ?? link),
                new XElement("link", link),
                new XElement("enclosure",
                    new XAttribute("url", link),
                    new XAttribute("length", release.Size ?? 0),
                    new XAttribute("type", "application/x-bittorrent")));

            if (release.DetailsUrl is not null)
            {
                item.Add(new XElement("comments", release.DetailsUrl));
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
                if (value is not null && Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } text)
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
            if (release.DownloadVolumeFactor == 0)
            {
                Attr("flags", "freeleech");
            }

            channel.Add(item);
        }

        return new XDocument(new XElement("rss", new XAttribute("version", "2.0"), new XAttribute(XNamespace.Xmlns + "torznab", Torznab), channel));
    }
}
