namespace Deluno.Integrations.Metadata;

/// <summary>
/// The outcome of one artwork-cache maintenance pass.
/// </summary>
public sealed record ArtworkCacheCleanupResult(
    int ScannedCount,
    int DeletedCount,
    long ReclaimedBytes,
    int SkippedReferencedCount,
    int FailedCount);
