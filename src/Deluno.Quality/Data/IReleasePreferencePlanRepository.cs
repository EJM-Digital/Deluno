using Deluno.Quality.ReleasePreferences;

namespace Deluno.Quality.Data;

public sealed record StoredReleasePreferencePlan(
    ReleasePreferencePlan Plan,
    string PlanHash,
    DateTimeOffset CreatedUtc);

/// <summary>
/// Stores immutable compiled plans separately from mutable quality-profile
/// rows. A profile edit creates a new version/hash; it cannot rewrite the plan
/// used by an earlier evaluation.
/// </summary>
public interface IReleasePreferencePlanRepository
{
    Task<StoredReleasePreferencePlan> SaveAsync(
        ReleasePreferencePlan plan,
        CancellationToken cancellationToken);

    Task<StoredReleasePreferencePlan?> GetAsync(
        string planId,
        string? version,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredReleasePreferencePlan>> ListAsync(
        string? mediaType,
        CancellationToken cancellationToken);
}
