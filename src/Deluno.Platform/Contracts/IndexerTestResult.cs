namespace Deluno.Platform.Contracts;

public sealed record IndexerTestResult(
    string Id,
    string HealthStatus,
    string Message,
    DateTimeOffset TestedUtc);
