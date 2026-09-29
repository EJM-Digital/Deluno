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
    string? MinimumAvailability = null,
    /// <summary>See <see cref="CreateIntakeSourceRequest.MonitorMode"/> (#848).</summary>
    string? MonitorMode = null,
    /// <summary>See <see cref="CreateIntakeSourceRequest.SeriesType"/> (#850).</summary>
    string? SeriesType = null,
    /// <summary>See <see cref="CreateIntakeSourceRequest.SeasonFolder"/> (#850).</summary>
    bool? SeasonFolder = null,
    /// <summary>See <see cref="CreateIntakeSourceRequest.Tags"/> (#1015). Null keeps the tags the list already has.</summary>
    string? Tags = null);
