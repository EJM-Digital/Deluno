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
    string? MinimumAvailability = null,
    /// <summary>See <see cref="IntakeMonitorModes"/> (#848). Null normalizes to All/Movie, preserving today's fully-monitored behaviour.</summary>
    string? MonitorMode = null,
    /// <summary>
    /// TV-only (#850): the series type (Standard/Daily/Anime) and Season
    /// Folder override a list-added series should take on, mirroring the
    /// same fields on a direct series add. Null/unset preserves Deluno's
    /// existing per-series defaults (Standard type, global season format).
    /// </summary>
    string? SeriesType = null,
    bool? SeasonFolder = null,
    /// <summary>
    /// #1015: comma-separated tag names given to every title this list adds
    /// (Radarr and Sonarr's ImportListSyncService sets Tags = importList.Tags on
    /// each title it adds). Blank gives none, which is what every existing list does.
    /// </summary>
    string? Tags = null);
