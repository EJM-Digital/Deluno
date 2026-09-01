namespace Deluno.Quality.Guides;

/// <summary>
/// Identifies the exact read-before-write guide sync the owner reviewed.
/// Neither endpoint accepts a client-supplied package: Deluno rebuilds it from
/// the immutable upstream revision again and checks all three identities.
/// </summary>
public sealed record GuidePackageSyncRequest(
    string? ExpectedCurrentIntegritySha256,
    string? ExpectedUpstreamRevision,
    string? ExpectedProposedIntegritySha256 = null);

public interface IGuidePackageSyncService
{
    /// <summary>Downloads and validates an immutable upstream revision, without persisting it.</summary>
    Task<GuidePackageUpdatePreview> PreviewAsync(
        GuidePackageSyncRequest request,
        CancellationToken cancellationToken);

    /// <summary>Persists precisely the candidate the owner previewed.</summary>
    Task<StoredGuidePackage> ApplyAsync(
        GuidePackageSyncRequest request,
        CancellationToken cancellationToken);
}
