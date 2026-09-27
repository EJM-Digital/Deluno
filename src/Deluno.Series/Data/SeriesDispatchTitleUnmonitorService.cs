using Deluno.Contracts;

namespace Deluno.Series.Data;

/// <summary>
/// The TV side of "I don't want this title any more" (#924): stops
/// monitoring a series, or just one episode of it, so automatic searches
/// stop wanting it again — without Integrations ever referencing this
/// repository directly. See <see cref="IDispatchTitleUnmonitorService"/>.
///
/// <para>An automatic per-episode search dispatches with entity type
/// <c>"episode"</c>; only that episode is unmonitored, not its whole series,
/// matching how <c>SupportsReplacementSearch</c> in the download-client
/// telemetry service already tells episode dispatches apart from series
/// ones.</para>
/// </summary>
public sealed class SeriesDispatchTitleUnmonitorService(ISeriesCatalogRepository catalogRepository)
    : IDispatchTitleUnmonitorComponent
{
    public bool CanHandle(string entityType) => entityType is "series" or "episode";

    public async Task<bool> UnmonitorAsync(string entityType, string entityId, CancellationToken cancellationToken)
    {
        if (entityType == "episode")
        {
            var updatedEpisodes = await catalogRepository.UpdateEpisodeMonitoredAsync([entityId], false, cancellationToken);
            return updatedEpisodes > 0;
        }

        var updatedSeries = await catalogRepository.UpdateMonitoredAsync([entityId], false, cancellationToken);
        return updatedSeries > 0;
    }
}
