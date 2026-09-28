namespace Deluno.Jobs.Contracts;

public sealed record LibraryAutomationPlanItem(
    string LibraryId,
    string LibraryName,
    string MediaType,
    bool AutoSearchEnabled,
    bool MissingSearchEnabled,
    bool UpgradeSearchEnabled,
    int SearchIntervalMinutes,
    int RetryDelayHours,
    int MaxItemsPerRun,
    int? SearchWindowStartHour,
    int? SearchWindowEndHour,
    /// <summary>
    /// Whether this library has asked for any subtitle languages.
    ///
    /// A flag rather than the list itself, because the planner does not need to
    /// know which languages — only whether there is anything to read files for.
    /// A shelf that has asked for none is never planned a subtitle scan, which
    /// is what keeps this feature free for everybody not using it.
    /// </summary>
    bool WantsSubtitles = false,
    /// <summary>
    /// Saved views that scope scheduled searches for this library. Null or an
    /// empty list means the normal unfiltered cycle remains in effect.
    /// </summary>
    IReadOnlyList<LibraryAutomationScope>? SearchScopes = null,
    /// <summary>Shuffles automatic wanted-title search order instead of oldest-added-first (#831).</summary>
    bool RandomizeSearchOrder = false,
    /// <summary>How many missing-item candidates a search cycle works through, independent of <see cref="MaxUpgradeItemsPerRun"/> (#830).</summary>
    int MaxMissingItemsPerRun = 25,
    /// <summary>How many cutoff-unmet (upgrade) candidates a search cycle works through, independent of <see cref="MaxMissingItemsPerRun"/> (#830).</summary>
    int MaxUpgradeItemsPerRun = 25,
    /// <summary>Which days of the week the search window applies on, as a 7-bit mask (Sunday = bit 0 ... Saturday = bit 6). 127 is every day (#832).</summary>
    int SearchWindowDays = 127);
