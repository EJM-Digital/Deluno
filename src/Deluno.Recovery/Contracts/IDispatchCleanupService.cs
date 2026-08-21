namespace Deluno.Recovery.Contracts;

public interface IDispatchCleanupService
{
    Task<DispatchCleanupResult> RunCleanupPassAsync(CancellationToken cancellationToken);
}

public sealed record DispatchCleanupResult(
    int ArchivedCount,
    int SkippedCount,
    string Summary);
