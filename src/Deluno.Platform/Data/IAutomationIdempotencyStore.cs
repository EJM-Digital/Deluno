namespace Deluno.Platform.Data;

public sealed record AutomationIdempotencyLookup(
    bool Found,
    bool HashMatches,
    string? ResponseJson,
    string? Operation);

public interface IAutomationIdempotencyStore
{
    Task<AutomationIdempotencyLookup> GetAsync(
        string key,
        string operation,
        string requestHash,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores a completed response, or returns the response already won by a
    /// concurrent request using the same key.
    /// </summary>
    Task<AutomationIdempotencyLookup> SaveAsync(
        string key,
        string operation,
        string requestHash,
        string responseJson,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes keys stored before the given time. Integration keys are kept
    /// for 7 days so a repeat inside that window still gets the stored answer.
    /// </summary>
    Task<int> PruneAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken);
}
