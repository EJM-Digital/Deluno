namespace Deluno.Recovery.Contracts;

public interface IMovieImportRecoveryRetentionRepository
{
    Task<int> CleanupImportRecoveryCasesAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken);
}

public interface ISeriesImportRecoveryRetentionRepository
{
    Task<int> CleanupImportRecoveryCasesAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken);
}
