using Deluno.Contracts;
using Deluno.Platform.Contracts;

namespace Deluno.Platform.Data;

public interface IDownloadHealthRepository
{
    /// <summary>
    /// Records what one client's queue shows now.
    ///
    /// <para><paramref name="listedQueueItemIds"/> is every download the client
    /// listed in this snapshot, with or without a finding. A download listed
    /// without a finding it used to have has recovered from it, so that record
    /// ends. A download that is not listed at all has left the client, or the
    /// client did not answer, so its records are left alone (#560).</para>
    ///
    /// <para><paramref name="defaultStrikeWindow"/> paces a finding outside the
    /// rule engine (#905); a rule-engine observation uses its own
    /// <see cref="DownloadHealthObservation.RuleTimerMinutes"/> instead.
    /// <paramref name="strikeInactivityWindow"/> is the separate, usually much
    /// longer, gap a download can go without being observed before Deluno
    /// treats the problem as over rather than merely paused (#825) -
    /// independent of any rule's own timer.</para>
    /// </summary>
    Task<IReadOnlyList<DownloadHealthRecord>> RecordDownloadHealthObservationsAsync(
        string clientId,
        IReadOnlyCollection<string> listedQueueItemIds,
        IReadOnlyList<DownloadHealthObservation> observations,
        TimeSpan defaultStrikeWindow,
        TimeSpan strikeInactivityWindow,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadHealthRecord>> ListDownloadHealthRecordsAsync(
        int take,
        CancellationToken cancellationToken);

    Task<Page<DownloadHealthRecord>> ListDownloadHealthRecordsPageAsync(
        PageRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Forgets everything recorded about one download client's downloads.
    ///
    /// <para>A record ends when the client lists the download again and the
    /// finding is gone. A deleted client is never asked again, so its records
    /// would sit until the 90-day retention with no screen able to clear them
    /// (#568).</para>
    /// </summary>
    Task<int> ForgetDownloadClientAsync(string clientId, CancellationToken cancellationToken);

    Task<DownloadHealthRecord?> IgnoreDownloadHealthFindingAsync(
        string clientId,
        string queueItemId,
        string kind,
        int durationDays,
        CancellationToken cancellationToken);

    /// <summary>Clears every recorded strike for every download in one call (#826). Irreversible.</summary>
    Task<int> PurgeAllDownloadHealthStrikesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Updates the restart-safe memory of downloaded bytes for every queue
    /// item this observation lists, and returns when each one's bytes were
    /// last seen to increase (#659). A gain moves that item's clock to now;
    /// no gain leaves the previous value alone, so a torrent that is merely
    /// slow (any gain, however small) never reaches the stall window, and one
    /// that has genuinely stopped keeps the clock it had before a restart.
    /// </summary>
    Task<IReadOnlyDictionary<string, DateTimeOffset>> RecordDownloadProgressAsync(
        string clientId,
        IReadOnlyList<(string QueueItemId, long DownloadedBytes)> observations,
        CancellationToken cancellationToken);

    /// <summary>
    /// The configured download-health rules (#905). The first call on an
    /// installation with none stored yet synthesizes and persists the rule
    /// set equivalent to <paramref name="legacySettings"/>'s flat toggles, so
    /// nothing changes for an existing install until it opens the new screen.
    /// </summary>
    Task<IReadOnlyList<DownloadHealthRule>> GetDownloadHealthRulesAsync(
        PlatformSettingsSnapshot legacySettings,
        CancellationToken cancellationToken);

    /// <summary>Creates or updates one rule. Returns the refusal message instead of saving when it overlaps another enabled rule of the same kind.</summary>
    Task<(IReadOnlyList<DownloadHealthRule> Rules, string? Error)> SaveDownloadHealthRuleAsync(
        DownloadHealthRule rule,
        PlatformSettingsSnapshot legacySettings,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadHealthRule>> DeleteDownloadHealthRuleAsync(
        string ruleId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadHealthIgnoreEntry>> ListDownloadHealthIgnoreEntriesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadHealthIgnoreEntry>> AddDownloadHealthIgnoreEntryAsync(
        DownloadHealthIgnoreEntry entry,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadHealthIgnoreEntry>> RemoveDownloadHealthIgnoreEntryAsync(
        string entryId,
        CancellationToken cancellationToken);

    /// <summary>
    /// #828: physically deletes every download-health record and progress
    /// record last observed before <paramref name="olderThan"/>, on the
    /// user's own configured window rather than the fixed 90-day ceiling
    /// every read already applies. Returns how many rows were removed.
    /// </summary>
    Task<int> PruneDownloadHealthHistoryAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);
}
