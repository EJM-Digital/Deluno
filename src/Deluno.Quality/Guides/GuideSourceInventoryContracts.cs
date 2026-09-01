namespace Deluno.Quality.Guides;

/// <summary>
/// A pinned, lossless inventory of the upstream guide material. This is kept
/// beside Deluno's reviewed package so a guide update can be audited without
/// turning an upstream score or matcher into a release decision by accident.
/// </summary>
public sealed record GuideSourceInventory(
    int SchemaVersion,
    string UpstreamRevision,
    IReadOnlyList<GuideSourceCustomFormat> CustomFormats,
    IReadOnlyList<GuideSourceFormatGroup> FormatGroups,
    IReadOnlyList<GuideSourceQualityProfile> QualityProfiles);

public sealed record GuideSourceCustomFormat(
    string TrashId,
    string Name,
    string? Description,
    string MediaType,
    string SourcePath,
    string SourceBlobSha,
    IReadOnlyDictionary<string, int> Scores,
    bool IncludeWhenRenaming,
    IReadOnlyList<GuideSourceMatcherClause> MatcherClauses);

/// <summary>
/// Retains the upstream specification exactly enough for an owner to inspect
/// it. Deluno does not claim that every upstream implementation is a safe
/// title-regex matcher; unreviewed clauses remain Advanced.
/// </summary>
public sealed record GuideSourceMatcherClause(
    string Name,
    string Implementation,
    bool Negate,
    bool Required,
    string FieldsJson);

public sealed record GuideSourceFormatGroup(
    string TrashId,
    string Name,
    string? Description,
    string MediaType,
    string SourcePath,
    string SourceBlobSha,
    IReadOnlyList<GuideSourceFormatGroupEntry> CustomFormats,
    IReadOnlyList<string> QualityProfileIds);

public sealed record GuideSourceFormatGroupEntry(
    string TrashId,
    string Name,
    bool Required);

/// <summary>
/// The raw upstream quality profile is retained for stable-ID migration and
/// source tracing. It is deliberately not compiled into a Deluno plan until
/// its semantics have a reviewed mapping.
/// </summary>
public sealed record GuideSourceQualityProfile(
    string TrashId,
    string Name,
    string? Description,
    string MediaType,
    string SourcePath,
    string SourceBlobSha,
    IReadOnlyList<GuideSourceProfileFormatAssignment> FormatAssignments,
    string DefinitionJson);

public sealed record GuideSourceProfileFormatAssignment(
    string Name,
    string TrashId);
