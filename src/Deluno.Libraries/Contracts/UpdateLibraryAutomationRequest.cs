namespace Deluno.Libraries.Contracts;

public sealed record UpdateLibraryAutomationRequest(
    bool AutoSearchEnabled,
    bool MissingSearchEnabled,
    bool UpgradeSearchEnabled,
    int? SearchIntervalHours,
    int? RetryDelayHours,
    /// <summary>
    /// Deprecated: a single cap shared by missing and upgrade searches. Kept
    /// so an older client (or the external-integration API) that has not been
    /// updated for #830 still works — a request that only sets this applies
    /// it to both <see cref="MaxMissingItemsPerRun"/> and
    /// <see cref="MaxUpgradeItemsPerRun"/>. A request that sets either of
    /// those wins over this one.
    /// </summary>
    int? MaxItemsPerRun,
    int? SearchWindowStartHour,
    int? SearchWindowEndHour,
    bool RandomizeSearchOrder = false,
    /// <summary>How many missing-item candidates a search cycle works through, independent of the upgrade cap (#830).</summary>
    int? MaxMissingItemsPerRun = null,
    /// <summary>How many cutoff-unmet (upgrade) candidates a search cycle works through, independent of the missing cap (#830).</summary>
    int? MaxUpgradeItemsPerRun = null,
    /// <summary>
    /// Which days of the week the search window applies on, as a 7-bit mask —
    /// one bit per <see cref="DayOfWeek"/> value (Sunday = bit 0 ... Saturday
    /// = bit 6). Null (or an empty/out-of-range mask) means every day, which
    /// is what a library did before this existed (#832).
    /// </summary>
    int? SearchWindowDays = null);
