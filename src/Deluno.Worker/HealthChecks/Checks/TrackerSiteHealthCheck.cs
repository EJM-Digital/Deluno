using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.Search.Trackers;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// A tracker indexer whose site Deluno can no longer run as saved (#986): its
/// site is not in the built-in list. Settings the site's file does not know are
/// harmless leftovers and never reported. Only problems are reported, and each row goes away the
/// moment the indexer is fixed or removed; nothing is ever deleted here.
/// </summary>
public sealed class TrackerSiteHealthCheck(IConnectionsRepository connections, ITrackerDefinitionCatalog catalog) : IHealthCheck
{
    public string Category => HealthCheckCategories.Indexer;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var outcomes = new List<HealthCheckOutcome>();
        foreach (var indexer in await connections.ListIndexersAsync(cancellationToken))
        {
            if (!IndexerProtocols.IsTracker(indexer.Protocol))
            {
                continue;
            }

            var resolution = TrackerSiteMapping.Resolve(indexer, catalog);
            if (resolution.Problem is null)
            {
                continue;
            }

            outcomes.Add(HealthCheckOutcome.Problem(
                $"tracker-site:{indexer.Id}",
                Category,
                $"{indexer.Name} (tracker site)",
                $"{indexer.Name} cannot be searched as it is saved. {resolution.Problem}",
                cause: $"Deluno's tracker sites now come from the built-in Prowlarr site list, and the saved site for {indexer.Name} is not in it.",
                fix: $"Open Connections, edit {indexer.Name} and choose its site again, or remove it. Nothing was deleted.",
                detailsJson: $$"""{"indexerId":"{{indexer.Id}}","definitionId":"{{indexer.DefinitionId}}"}"""));
        }

        return outcomes;
    }
}
