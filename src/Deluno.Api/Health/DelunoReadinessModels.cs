namespace Deluno.Api.Health;

public sealed record DelunoLivenessResponse(
    string Status,
    DateTimeOffset CheckedUtc);

public sealed record DelunoReadinessResponse(
    bool Ready,
    string Status,
    DateTimeOffset CheckedUtc,
    IReadOnlyList<ReadinessCheckResult> Checks);

public sealed record ReadinessCheckResult(
    string Name,
    string Status,
    string Message,
    IReadOnlyDictionary<string, object?> Details);
