namespace Deluno.Contracts;

public static class MediaExclusionSourceKinds
{
    public const string ImportList = "import-list";
    public const string Collection = "collection";

    /// <summary>
    /// Cleaned up by a retention rule (#492). Unlike the other two it is not
    /// scoped to the source that added the title: whichever list or collection
    /// would bring it back, it stays out, because the person decided the title
    /// should go, not that one list should stop offering it.
    /// </summary>
    public const string Retention = "retention";
}

public static class MediaExclusionMatching
{
    /// <summary>
    /// Whether an exclusion names this title, by the strongest identity both
    /// sides have: IMDb id, then TMDb id (kept in <c>Provider</c> as
    /// <c>tmdb:&lt;id&gt;</c> on retention rows), then title and year.
    /// </summary>
    public static bool Names(MediaExclusionItem exclusion, string? imdbId, string? tmdbId, string? title, int? year)
    {
        if (!string.IsNullOrWhiteSpace(imdbId) && !string.IsNullOrWhiteSpace(exclusion.ImdbId))
        {
            return string.Equals(imdbId.Trim(), exclusion.ImdbId.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(tmdbId) &&
            string.Equals(exclusion.Provider, $"tmdb:{tmdbId.Trim()}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(title) &&
               string.Equals(title.Trim(), exclusion.Title.Trim(), StringComparison.OrdinalIgnoreCase) &&
               (year is null || exclusion.Year is null || year == exclusion.Year);
    }
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
