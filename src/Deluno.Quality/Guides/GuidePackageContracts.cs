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
    string? SourcePath = null,
    /// <summary>
    /// TRaSH's own answer to "should this format appear in a file's name?".
    /// Deluno uses it to decide what a rename keeps, rather than inventing a
    /// list. Null on packages stored before naming read it, and omitted from
    /// the JSON then, so their integrity hashes are unchanged.
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? IncludeWhenRenaming = null);

/// <summary>
/// Read as its name wherever a package arrives (#1326): GET returns it as a string, so the apply and preview routes, which bind
/// with the host's own JSON settings, must take that string back. The package routes' own settings (camel-case) still decide what is written.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
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
    IReadOnlyList<GuideRecommendedFormat> RecommendedFormats,
    /// <summary>
    /// TRaSH's own minimum score gain for an upgrade (#778). Every pinned
    /// upstream profile agrees this is 1 - any positive gain counts - so a
    /// missing value defaults to 1 rather than 0, which would read as "no
    /// preference has ever been an upgrade".
    /// </summary>
    int MinUpgradeFormatScore = 1,
    /// <summary>
    /// TRaSH's own language answer for this profile - "Original", "Any", or a
    /// specific language name (#769). Null or "Any" means no language
    /// preference is set when a profile starts from this guide profile.
    /// </summary>
    string? Language = null);

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
