namespace Deluno.Connections.Contracts;

/// <summary>
/// Tests a native tracker indexer (#606). A tracker has no API of its own to
/// ask whether it is there, so the test is a real search through its
/// definition; the tracker engine, which Connections does not reference,
/// provides it.
/// </summary>
public interface ITrackerIndexerTester
{
    Task<(string HealthStatus, string Message, string? FailureCategory)> TestAsync(IndexerItem indexer, CancellationToken cancellationToken);
}
