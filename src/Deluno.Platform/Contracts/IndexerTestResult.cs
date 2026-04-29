namespace Deluno.Platform.Contracts;

public sealed record IndexerTestResult(
    string Id,
    string HealthStatus,
    string Message,
    string? FailureCategory,
    int? LatencyMs,
    DateTimeOffset TestedUtc);
