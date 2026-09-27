namespace Deluno.Intake.Contracts;

public sealed record UpdateIntakeSourceRequest(
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
    /// <summary>See <see cref="CreateIntakeSourceRequest.MinimumAvailability"/> (#849).</summary>
    string? MinimumAvailability = null);
