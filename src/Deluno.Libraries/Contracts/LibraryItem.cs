using Deluno.Contracts;

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
    /// <summary>Deprecated: see <see cref="UpdateLibraryAutomationRequest.MaxItemsPerRun"/>. Kept in step with the higher of the two split caps below for any reader still using it.</summary>
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
    string SubtitleLanguageMode = "all",
    /// <summary>
    /// What a subtitle with no language in its name is.
    ///
    /// <para>Empty means "do not guess", which is the default and what Deluno
    /// has always done: a bare <c>Movie.srt</c> is recorded as <c>und</c> and
    /// counts for nothing. Reading it as the first wanted language would be
    /// right most of the time, and when it was wrong it would stop Deluno
    /// fetching a language somebody asked for and never say why (DESIGN-002).
    /// Bazarr does not guess either — it asks once, and so does this.</para>
    /// </summary>
    string SubtitleUnknownLanguage = "",
    /// <summary>
    /// Whether a subtitle track inside the video counts as held.
    ///
    /// <para>True by default, which is what Deluno has always done. Off means a
    /// sidecar is fetched even when the container already has the language —
    /// which some people want, because a player handles the two differently and
    /// an embedded track cannot be swapped or corrected (#321).</para>
    /// </summary>
    bool SubtitleEmbeddedCounts = true,
    /// <summary>
    /// Named cleanup applied to subtitles after download and before they are
    /// written beside the video. Null means provider content is preserved.
    /// </summary>
    SubtitleContentModificationPolicy? SubtitleContentPolicy = null,
    /// <summary>Automatic timing-repair policy for fetched subtitles.</summary>
    SubtitleTimingPolicy? SubtitleTimingPolicy = null,
    /// <summary>Words a subtitle's release name must or must not carry.</summary>
    SubtitleNamePolicy? SubtitleNamePolicy = null,
    /// <summary>
    /// Whether a subtitle Deluno writes leaves the language code out of its
    /// name, for players that only load <c>Film.srt</c>.
    /// </summary>
    bool SubtitleOmitLanguageCode = false,
    /// <summary>
    /// Shuffles automatic wanted-title search order instead of always oldest
    /// added first, so a recently added title is never stuck behind a long
    /// backlog (#831). Off by default: the order is unchanged from before
    /// this existed.
    /// </summary>
    bool RandomizeSearchOrder = false,
    /// <summary>
    /// Per-language hearing-impaired and forced-track preferences (#835,
    /// #836, #923). A wanted language with no row here gets the defaults —
    /// hearing-impaired is coverage with a plain track winning a tie, and
    /// forced is never fetched.
    /// </summary>
    IReadOnlyList<SubtitleLanguageTypePreference>? SubtitleLanguageTypePreferences = null,
    /// <summary>How many missing-item candidates a search cycle works through per library, independent of <see cref="MaxUpgradeItemsPerRun"/> (#830).</summary>
    int MaxMissingItemsPerRun = 25,
    /// <summary>How many cutoff-unmet (upgrade) candidates a search cycle works through per library, independent of <see cref="MaxMissingItemsPerRun"/> (#830).</summary>
    int MaxUpgradeItemsPerRun = 25,
    /// <summary>
    /// Which days of the week the search window applies on, as a 7-bit mask —
    /// one bit per <see cref="DayOfWeek"/> value (Sunday = bit 0 ... Saturday =
    /// bit 6). 127 (every bit set) is every day, which is what every library
    /// had before this existed and is the default (#832).
    /// </summary>
    int SearchWindowDays = 127);
