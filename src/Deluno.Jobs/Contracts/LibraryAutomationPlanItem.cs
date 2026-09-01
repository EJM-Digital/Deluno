namespace Deluno.Jobs.Contracts;

public sealed record LibraryAutomationPlanItem(
    string LibraryId,
    string LibraryName,
    string MediaType,
    bool AutoSearchEnabled,
    bool MissingSearchEnabled,
    bool UpgradeSearchEnabled,
    int SearchIntervalHours,
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
    IReadOnlyList<LibraryAutomationScope>? SearchScopes = null);
