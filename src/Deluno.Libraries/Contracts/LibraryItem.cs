namespace Deluno.Libraries.Contracts;

public sealed record LibraryItem(
    string Id,
    string Name,
    string MediaType,
    string Purpose,
    string RootPath,
    string? DownloadsPath,
    string? QualityProfileId,
    string? QualityProfileName,
    string? CutoffQuality,
    bool UpgradeUntilCutoff,
    bool UpgradeUnknownItems,
    string ImportWorkflow,
    string? ProcessorName,
    string? ProcessorOutputPath,
    int ProcessorTimeoutMinutes,
    string ProcessorFailureMode,
    bool AutoSearchEnabled,
    bool MissingSearchEnabled,
    bool UpgradeSearchEnabled,
    int SearchIntervalHours,
    int RetryDelayHours,
    int MaxItemsPerRun,
    int? SearchWindowStartHour,
    int? SearchWindowEndHour,
    string AutomationStatus,
    bool SearchRequested,
    DateTimeOffset? LastSearchedUtc,
    DateTimeOffset? NextSearchUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string? DefaultPolicySetId = null,
    string? DefaultPolicySetName = null,
    string CleanupMode = "keep-source",
    bool RemoveEmptySourceFolders = false,
    /// <summary>
    /// Ordered ISO 639-1 codes, most wanted first. Empty means no subtitles are
    /// wanted here, and a title that wants none draws no bar (DESIGN-001).
    /// </summary>
    IReadOnlyList<string>? SubtitleLanguages = null,
    /// <summary>
    /// How many of <see cref="SubtitleLanguages"/> a file needs.
    ///
    /// <c>all</c> — every language listed. <c>first</c> — the first one that can
    /// be found, in order, and then stop.
    ///
    /// Bazarr expresses this as an ordered list plus a cutoff *position*, which
    /// conflates two different intentions: "English and Japanese" and "English,
    /// or Spanish if English is unavailable". These are the two intentions, in
    /// two words, and they are what the bar counts.
    /// </summary>
    string SubtitleLanguageMode = "all");
