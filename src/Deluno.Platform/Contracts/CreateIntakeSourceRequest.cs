namespace Deluno.Platform.Contracts;

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
    bool IsEnabled);
