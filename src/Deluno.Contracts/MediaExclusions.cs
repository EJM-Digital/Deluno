namespace Deluno.Contracts;

public static class MediaExclusionSourceKinds
{
    public const string ImportList = "import-list";
    public const string Collection = "collection";
}

/// <summary>
/// One durable decision not to add a media entry from an automated source.
/// Import lists and movie collections deliberately use the same shape so the
/// review screen does not hide a decision merely because its source changed.
/// </summary>
public sealed record MediaExclusionItem(
    string Id,
    string MediaType,
    string SourceKind,
    string SourceId,
    string SourceName,
    string Provider,
    string EntryKey,
    string Title,
    int? Year,
    string? ImdbId,
    string Reason,
    DateTimeOffset? ExpiresUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record UpsertMediaExclusionRequest(
    string MediaType,
    string SourceKind,
    string SourceId,
    string SourceName,
    string Provider,
    string EntryKey,
    string Title,
    int? Year,
    string? ImdbId,
    int? DurationDays,
    string? Reason);

public interface IUnifiedExclusionRepository
{
    Task<IReadOnlyList<MediaExclusionItem>> ListActiveAsync(
        string? mediaType,
        string? sourceKind,
        string? sourceId,
        CancellationToken cancellationToken);

    Task<MediaExclusionItem?> UpsertAsync(
        UpsertMediaExclusionRequest request,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken);

    Task<bool> DeleteByScopeAsync(
        string sourceKind,
        string sourceId,
        string entryKey,
        CancellationToken cancellationToken);
}
