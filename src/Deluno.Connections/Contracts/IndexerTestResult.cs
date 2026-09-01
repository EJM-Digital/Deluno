using Deluno.Contracts;

namespace Deluno.Connections.Contracts;

public sealed record IndexerTestResult(
    string Id,
    string HealthStatus,
    string Message,
    string? FailureCategory,
    int? LatencyMs,
    DateTimeOffset TestedUtc)
{
    /// <summary>
    /// Structured failure details. The legacy category remains for older API
    /// consumers and existing health rows.
    /// </summary>
    public IntegrationFailure? Failure { get; init; }
}
