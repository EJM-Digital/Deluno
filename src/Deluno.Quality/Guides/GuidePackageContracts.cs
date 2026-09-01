using System.Text.Json.Serialization;

namespace Deluno.Quality.Guides;

/// <summary>
/// The immutable, backend-owned guide data used by setup, quality profiles,
/// release rules, and automation. The package is a Deluno adaptation of the
/// upstream guide material; it is not a Recyclarr configuration file.
/// </summary>
public sealed record GuidePackage(
    string Id,
    string Name,
    int Version,
    int SchemaVersion,
    GuidePackageProvenance Source,
    string? IntegritySha256,
    IReadOnlyList<GuideQualityTier> QualityTiers,
    IReadOnlyList<GuideCustomFormat> CustomFormats,
    IReadOnlyList<GuideQualityProfile> QualityProfiles,
    IReadOnlyList<GuideFormatBundle> Bundles,
    GuideSourceInventory? SourceInventory = null);

public sealed record GuidePackageProvenance(
    string SourceName,
    string RepositoryUrl,
    string GuideUrl,
    string UpstreamRevision,
    string ReviewedUtc,
    string Adaptation);

public sealed record GuideQualityTier(
    string Id,
    string Label,
    string Source,
    string Resolution,
    double MinMbPerMin,
    double MaxMbPerMin,
    int Rank);

public sealed record GuideCustomFormat(
    string TrashId,
    string Name,
    string Category,
    string Description,
    int OriginalScore,
    IReadOnlyList<string> Patterns,
    bool BundleOnly,
    GuideMappingStatus MappingStatus,
    IReadOnlyList<string> MappedTraitIds,
    string SourceKind,
    IReadOnlyList<string>? MediaTypes = null,
    IReadOnlyList<string>? SourceGroupIds = null,
    IReadOnlyList<GuideSourceMatcherClause>? SourceMatcherClauses = null,
    IReadOnlyDictionary<string, int>? SourceScores = null,
    string? SourcePath = null);

public enum GuideMappingStatus
{
    Reviewed,
    Advanced
}

public sealed record GuideQualityProfile(
    string Id,
    string Name,
    string Tagline,
    string Description,
    IReadOnlyList<string> Highlights,
    string MediaType,
    IReadOnlyList<string> QualityOrder,
    string CutoffQualityId,
    bool UpgradeAllowed,
    int MinFormatScore,
    int CutoffFormatScore,
    IReadOnlyList<GuideRecommendedFormat> RecommendedFormats);

public sealed record GuideRecommendedFormat(
    string TrashId,
    int Score);

public sealed record GuideFormatBundle(
    string Id,
    string Name,
    string Level,
    string MediaType,
    string Description,
    string BestFor,
    IReadOnlyList<GuideFormatBundleEntry> Includes,
    IReadOnlyList<string> Warnings);

public sealed record GuideFormatBundleEntry(
    string TrashId,
    int? Score);
