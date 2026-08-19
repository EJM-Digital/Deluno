using Deluno.Series.Data;

namespace Deluno.Series.Services;

/// <summary>
/// Finds episodes that have a file but not a good enough one, so they can be
/// re-fetched.
///
/// Both questions here used to be answered by reading the whole catalogue: list
/// every series, then pull the full episode inventory of each one, to return at
/// most twenty ids. At a few thousand shows that is hundreds of thousands of
/// rows read to answer a question SQL can answer directly, and it ignored the
/// <c>libraryId</c> it was given.
/// </summary>
public sealed class EpisodeImportRecoveryService(
    ISeriesCatalogRepository seriesCatalogRepository,
    TimeProvider timeProvider)
    : IEpisodeImportRecoveryService
{
    /// <summary>
    /// The most one series may contribute, so a long-running show with a lot of
    /// under-cutoff files cannot crowd out every other series.
    /// </summary>
    private const int PerSeriesLimit = 5;

    private const int RecoveryBatchSize = 20;

    public Task<IReadOnlyList<string>> FindEpisodesNeedingRecoveryAsync(
        string libraryId,
        CancellationToken cancellationToken)
        => seriesCatalogRepository.ListEpisodesNeedingRecoveryAsync(
            libraryId,
            PerSeriesLimit,
            RecoveryBatchSize,
            cancellationToken);

    public async Task<int> RecoveryPriorityAsync(string episodeId, CancellationToken cancellationToken)
    {
        var updatedUtc = await seriesCatalogRepository.GetEpisodeUpdatedUtcAsync(episodeId, cancellationToken);
        if (updatedUtc is null)
        {
            return 0;
        }

        var ageHours = (int)(timeProvider.GetUtcNow() - updatedUtc.Value).TotalHours;
        return Math.Max(100, ageHours);
    }
}
