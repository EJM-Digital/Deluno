namespace Deluno.Intake.Contracts;

public sealed record CreateIntakeSourceRequest(
    string Name,
    string Provider,
    string FeedUrl,
    string? MediaType,
    string? LibraryId,
    string? QualityProfileId,
    string? RequiredGenres,
    double? MinimumRating,
    int? MinimumYear,
    int? MaximumAgeDays,
    string? AllowedCertifications,
    string? Audience,
    int? SyncIntervalHours,
    bool SearchOnAdd,
    bool IsEnabled,
    /// <summary>
    /// The minimum release stage (Announced/In Cinemas/Released) a list-added
    /// movie must reach before Deluno searches for it (#849). Null or unset
    /// normalizes to Announced, which always passes and preserves today's
    /// immediate-search behaviour. TV sources ignore this.
    /// </summary>
    string? MinimumAvailability = null);
