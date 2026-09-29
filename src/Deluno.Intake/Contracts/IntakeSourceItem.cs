namespace Deluno.Intake.Contracts;

public sealed record IntakeSourceItem(
    string Id,
    string Name,
    string Provider,
    string FeedUrl,
    string MediaType,
    string? LibraryId,
    string? LibraryName,
    string? QualityProfileId,
    string? QualityProfileName,
    string RequiredGenres,
    double? MinimumRating,
    int? MinimumYear,
    int? MaximumAgeDays,
    string AllowedCertifications,
    string Audience,
    int SyncIntervalHours,
    DateTimeOffset? LastSyncUtc,
    string LastSyncStatus,
    string? LastSyncSummary,
    bool SearchOnAdd,
    bool IsEnabled,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    /// <summary>See <see cref="CreateIntakeSourceRequest.MinimumAvailability"/> (#849). Always normalized to Announced/In Cinemas/Released.</summary>
    string MinimumAvailability = "announced",
    /// <summary>See <see cref="IntakeMonitorModes"/> (#848). Always normalized against MediaType.</summary>
    string MonitorMode = "all",
    /// <summary>See <see cref="CreateIntakeSourceRequest.SeriesType"/> (#850). TV-only; null for movie sources.</summary>
    string? SeriesType = null,
    /// <summary>See <see cref="CreateIntakeSourceRequest.SeasonFolder"/> (#850).</summary>
    bool? SeasonFolder = null,
    /// <summary>See <see cref="CreateIntakeSourceRequest.Tags"/> (#1015).</summary>
    string Tags = "");
