using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// Deluno used to ship nine hand-written public sites (#606). Seven of them are
/// now served by the built-in Prowlarr site list instead (#986); two (eztv and
/// torrentdownloads) stay because they use an API or feed the list's own file
/// does not. An indexer saved against one of the seven keeps its definition id
/// and its settings: the site list has a definition for the same site under the
/// same id, so the "map" below is the identity. It is still a map so a site
/// whose id differs could be added without touching anything else.
/// </summary>
public static class TrackerSiteMapping
{
    /// <summary>Old Deluno-written site id to the site-list id that replaced it.</summary>
    public static readonly IReadOnlyDictionary<string, string> Retired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["yts"] = "yts",
        ["thepiratebay"] = "thepiratebay",
        ["limetorrents"] = "limetorrents",
        ["1337x"] = "1337x",
        ["nyaasi"] = "nyaasi",
        ["rutor"] = "rutor",
        ["internetarchive"] = "internetarchive"
    };

    /// <summary>What a tracker indexer's definition id resolves to, or why it cannot, in plain words.</summary>
    public static TrackerSiteResolution Resolve(IndexerItem indexer, ITrackerDefinitionCatalog catalog)
    {
        var saved = indexer.DefinitionId?.Trim();
        if (string.IsNullOrEmpty(saved))
        {
            return new TrackerSiteResolution(null, false, "It has no tracker site chosen.");
        }

        var wanted = Retired.TryGetValue(saved, out var mapped) ? mapped : saved;
        var definition = catalog.Find(wanted);
        if (definition is null)
        {
            return new TrackerSiteResolution(wanted, Retired.ContainsKey(saved), $"The site list has no site '{wanted}' that Deluno can use.");
        }

        var known = definition.Settings.Select(setting => setting.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lost = (indexer.DefinitionSettings?.Keys ?? Enumerable.Empty<string>())
            .Concat(indexer.DefinitionSecrets?.Keys ?? Enumerable.Empty<string>())
            .Where(name => !known.Contains(name))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return lost.Length == 0
            ? new TrackerSiteResolution(wanted, Retired.ContainsKey(saved), null)
            : new TrackerSiteResolution(wanted, Retired.ContainsKey(saved), $"{definition.Name} no longer has the setting{(lost.Length == 1 ? "" : "s")} {string.Join(", ", lost)}.");
    }
}

/// <param name="SiteId">The site-list id this indexer runs from, when there is one.</param>
/// <param name="WasOwnSite">Whether the saved id was one of Deluno's retired own sites.</param>
/// <param name="Problem">Why it cannot be run as it is, or null when it can.</param>
public sealed record TrackerSiteResolution(string? SiteId, bool WasOwnSite, string? Problem);

/// <summary>
/// Once, after an upgrade: says in Activity which indexers now run from the
/// built-in site list instead of one of Deluno's own site files (#986). Nothing
/// is changed or deleted; an indexer that cannot be matched is left as it is and
/// shows on Health (<see cref="TrackerSiteMapping.Resolve"/>).
/// </summary>
public sealed class TrackerSiteSwitchHostedService(
    IConnectionsRepository connections,
    ITrackerDefinitionCatalog catalog,
    IActivityFeedRepository activityFeedRepository,
    IOptions<StoragePathOptions> storage,
    IDelunoStartupGate startupGate,
    ILogger<TrackerSiteSwitchHostedService> logger) : BackgroundService
{
    public const string MarkerFileName = ".own-sites-switched";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await startupGate.WaitAsync(TimeSpan.FromMinutes(2), stoppingToken);
            await RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Deluno could not check which tracker indexers moved to the built-in site list.");
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetFullPath(storage.Value.DataRoot), "trackers");
        var marker = Path.Combine(directory, MarkerFileName);
        if (File.Exists(marker))
        {
            return;
        }

        var switched = new List<string>();
        foreach (var indexer in await connections.ListIndexersAsync(cancellationToken))
        {
            if (!IndexerProtocols.IsTracker(indexer.Protocol))
            {
                continue;
            }

            var resolution = TrackerSiteMapping.Resolve(indexer, catalog);
            if (resolution.WasOwnSite && resolution.Problem is null)
            {
                switched.Add(indexer.Name);
            }
        }

        if (switched.Count > 0)
        {
            await activityFeedRepository.RecordActivityAsync(
                "trackers.definitions.switch",
                switched.Count == 1
                    ? $"Deluno now runs your {switched[0]} indexer from the built-in tracker site list instead of its own site file. Its settings were kept."
                    : $"Deluno now runs your {string.Join(", ", switched)} indexers from the built-in tracker site list instead of its own site files. Their settings were kept.",
                System.Text.Json.JsonSerializer.Serialize(new { indexers = switched }),
                null,
                "trackers",
                null,
                cancellationToken);
        }

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(marker, DateTimeOffset.UtcNow.ToString("u"), cancellationToken);
    }
}
