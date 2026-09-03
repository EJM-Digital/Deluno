namespace Deluno.Quality.Guides;

public sealed record StoredGuidePackage(
    GuidePackage Package,
    bool IsActive,
    DateTimeOffset StoredUtc)
{
    public string IntegritySha256 => Package.IntegritySha256 ?? GuidePackageCatalog.ComputeIntegritySha256(Package);
}

public sealed record GuidePackageUpdateRequest(
    GuidePackage? Package,
    string? ExpectedCurrentIntegritySha256 = null);

public sealed record GuideProfileUpdateDiff(
    string ProfileId,
    string ProfileName,
    string? CurrentPlanHash,
    string? ProposedPlanHash,
    int CurrentAdvancedRuleCount,
    int ProposedAdvancedRuleCount,
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> Warnings);

public sealed record GuidePackageUpdatePreview(
    StoredGuidePackage Current,
    GuidePackage Proposed,
    string ProposedIntegritySha256,
    GuideCapabilityInventory ProposedInventory,
    IReadOnlyList<GuideProfileUpdateDiff> ProfileDiffs,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    bool CanApply);

public interface IGuidePackageStore
{
    Task<StoredGuidePackage> GetCurrentAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredGuidePackage>> ListAsync(CancellationToken cancellationToken);

    Task<StoredGuidePackage?> GetAsync(
        string packageId,
        int version,
        CancellationToken cancellationToken);

    Task<GuidePackageUpdatePreview> PreviewAsync(
        GuidePackageUpdateRequest request,
        CancellationToken cancellationToken);

    Task<StoredGuidePackage> ApplyAsync(
        GuidePackageUpdateRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Makes a retained version current again.
    ///
    /// <para>Every version is immutable and kept, which makes each update a
    /// rollback point — but a point you cannot return to is not one. This is
    /// the way back, and it re-validates the stored definition rather than
    /// trusting the row, so a package that has been tampered with in the
    /// database is refused instead of being activated.</para>
    /// </summary>
    Task<StoredGuidePackage> ActivateAsync(
        string packageId,
        int version,
        CancellationToken cancellationToken);
}
