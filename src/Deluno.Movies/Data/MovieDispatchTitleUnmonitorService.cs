using Deluno.Contracts;

namespace Deluno.Movies.Data;

/// <summary>
/// The movie side of "I don't want this title any more" (#924): stops
/// monitoring a movie so automatic searches stop wanting it again, without
/// Integrations ever referencing this repository directly — see
/// <see cref="IDispatchTitleUnmonitorService"/>.
/// </summary>
public sealed class MovieDispatchTitleUnmonitorService(IMovieCatalogRepository catalogRepository)
    : IDispatchTitleUnmonitorComponent
{
    public bool CanHandle(string entityType) => entityType == "movie";

    public async Task<bool> UnmonitorAsync(string entityType, string entityId, CancellationToken cancellationToken)
    {
        var updated = await catalogRepository.UpdateMonitoredAsync([entityId], false, cancellationToken);
        return updated > 0;
    }
}
