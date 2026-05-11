namespace Deluno.Series.Services;

public interface IEpisodeImportRecoveryService
{
    Task<IReadOnlyList<string>> FindEpisodesNeedingRecoveryAsync(
        string libraryId,
        CancellationToken cancellationToken);

    Task<int> RecoveryPriorityAsync(string episodeId, CancellationToken cancellationToken);
}
