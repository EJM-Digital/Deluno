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
    /// <para>When <paramref name="everythingTheClientHolds"/> is given, the client did
    /// answer and this is the whole of what it holds, queue and history alike (#1223).
    /// A download it does not hold is gone: removed by Deluno, by a person, or by the
    /// client's own clean-up. Its records close at once and stop counting. Null means
    /// the client may not have answered, so a briefly unreachable client never reads
    /// as every download gone.</para>
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
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? everythingTheClientHolds = null);

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

    /// <summary>
    /// Moves everything recorded about one download (its strikes, an ignore, its
    /// progress clock) from the id its client used to file it under to the id it
    /// files it under now (#1062): a Transmission torrent's session number
    /// becoming its hash. What is already recorded under the new id wins.
    /// </summary>
    Task<int> MoveDownloadHealthItemIdAsync(
        string clientId,
        string oldQueueItemId,
        string newQueueItemId,
        CancellationToken cancellationToken);

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
    /// <summary>
    /// <paramref name="allowReset"/> is the no-progress rule's own
    /// <see cref="DownloadHealthRule.ResetStrikesOnProgress"/> (#951): false
    /// means a gain never moves the clock at all, once one has been
    /// recorded, so the spell keeps accruing strikes even through a small
    /// trickle. <paramref name="minimumGainBytes"/> is that same rule's
    /// <see cref="DownloadHealthRule.ResetStrikesMinimumProgressBytes"/> - a
    /// gain smaller than this does not count as "real" progress either. Both
    /// default to the original behaviour (any gain always resets) for a
    /// caller that predates #951.
    /// </summary>
    Task<IReadOnlyDictionary<string, DateTimeOffset>> RecordDownloadProgressAsync(
        string clientId,
        IReadOnlyList<(string QueueItemId, long DownloadedBytes)> observations,
        CancellationToken cancellationToken,
        bool allowReset = true,
        long minimumGainBytes = 0);

    /// <summary>
    /// The same bookkeeping as <see cref="RecordDownloadProgressAsync"/>, but each item also says whether its
    /// downloaded bytes grew since the previous check (#1207): by at least <paramref name="minimumGainBytes"/> (one
    /// byte when 0). Null for an item with no earlier reading. The previous bytes are the ones stored per item, so
    /// this survives a restart and needs no schema change.
    /// </summary>
    Task<IReadOnlyDictionary<string, DownloadProgressObservation>> ObserveDownloadProgressAsync(
        string clientId,
        IReadOnlyList<(string QueueItemId, long DownloadedBytes)> observations,
        CancellationToken cancellationToken,
        bool allowReset = true,
        long minimumGainBytes = 0);

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

    /// <summary>The content-blocklist configuration (#816). Off by default (#936) until saved once.</summary>
    Task<ContentBlocklistSettings> GetContentBlocklistSettingsAsync(CancellationToken cancellationToken);

    Task<ContentBlocklistSettings> SaveContentBlocklistSettingsAsync(
        ContentBlocklistSettings settings,
        CancellationToken cancellationToken);

    /// <summary>Replaces the patterns fetched from the configured custom remote URL, and stamps when that fetch happened (#816).</summary>
    Task<ContentBlocklistSettings> RecordContentBlocklistRemoteRefreshAsync(
        IReadOnlyList<string> remotePatterns,
        CancellationToken cancellationToken);

    /// <summary>
    /// The last time Deluno pushed the content blocklist into one qBittorrent
    /// client's "Excluded file names" preference (#822), and whether that push
    /// succeeded, keyed by download-client id.
    /// </summary>
    Task<IReadOnlyDictionary<string, QbittorrentBlocklistSyncState>> ListQbittorrentBlocklistSyncStatesAsync(CancellationToken cancellationToken);

    Task RecordQbittorrentBlocklistSyncAsync(
        string clientId,
        string blocklistHash,
        bool succeeded,
        string? message,
        CancellationToken cancellationToken);
}

/// <summary>One qBittorrent client's last content-blocklist sync attempt (#822).</summary>
public sealed record QbittorrentBlocklistSyncState(
    string ClientId,
    string BlocklistHash,
    bool Succeeded,
    string? Message,
    DateTimeOffset SyncedUtc);
