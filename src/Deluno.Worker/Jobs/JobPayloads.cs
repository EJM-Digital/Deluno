using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;

namespace Deluno.Worker.Jobs;

internal static class JobPayloads
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal sealed record LibrarySearchPayload(
        string LibraryId,
        string LibraryName,
        string MediaType,
        bool CheckMissing,
        bool CheckUpgrades,
        int MaxItems,
        int RetryDelayHours,
        string TriggeredBy,
        string? TargetEntityId = null,
        string SearchKind = "combined",
        string? ScopeId = null,
        string? ScopeName = null,
        IReadOnlyList<CatalogueFilterCondition>? ScopeConditions = null,
        bool RandomizeSearchOrder = false,
        /// <summary>How many missing-item candidates this cycle works through, independent of <see cref="MaxUpgradeItems"/> (#830). Falls back to half of <see cref="MaxItems"/> for a job queued before this existed.</summary>
        int? MaxMissingItems = null,
        /// <summary>How many upgrade candidates this cycle works through, independent of <see cref="MaxMissingItems"/> (#830). Falls back to half of <see cref="MaxItems"/> for a job queued before this existed.</summary>
        int? MaxUpgradeItems = null)
    {
        /// <summary>Effective missing-item cap: the split value when present, otherwise <see cref="MaxItems"/> itself (an older, unsplit job).</summary>
        public int EffectiveMaxMissingItems => MaxMissingItems ?? MaxItems;

        /// <summary>Effective upgrade-item cap: the split value when present, otherwise <see cref="MaxItems"/> itself (an older, unsplit job).</summary>
        public int EffectiveMaxUpgradeItems => MaxUpgradeItems ?? MaxItems;
    }

    internal sealed record LibraryQualityPayload(
        string LibraryId,
        string LibraryName,
        string MediaType,
        string? CutoffQuality,
        bool UpgradeUntilCutoff,
        bool UpgradeUnknownItems);

    internal sealed record EpisodeSearchPayload(
        string EpisodeId,
        string SeriesId,
        string LibraryId,
        int SeasonNumber,
        int EpisodeNumber,
        string Title);

    internal sealed record SeasonSearchPayloadEpisode(
        string EpisodeId,
        int EpisodeNumber,
        string Title);

    internal sealed record SeasonSearchPayload(
        string SeriesId,
        string SeriesTitle,
        string LibraryId,
        int SeasonNumber,
        IReadOnlyList<SeasonSearchPayloadEpisode> Episodes);

    internal sealed record IntakeSyncPayload(
        string? SourceId,
        bool Manual);

    /// <summary>
    /// One more try at sending the exact same release to the exact same
    /// client after a connectivity or setup failure (#875) — carries
    /// everything a fresh <see cref="DownloadClientGrabRequest"/> needs so
    /// the retry is a literal resend, never a new search.
    /// </summary>
    internal sealed record DownloadClientRetryGrabPayload(
        string DownloadClientId,
        string DownloadClientName,
        string LibraryId,
        string MediaType,
        string EntityType,
        string EntityId,
        string Title,
        string ReleaseName,
        string DownloadUrl,
        string? Category,
        string? IndexerName,
        string? IndexerId,
        string? DispatchId,
        int? Priority,
        string? Password,
        byte[]? TorrentFile,
        int AttemptNumber,
        // #874/#942: the candidate's hash or NZB id, when the original
        // search knew one, so a retry that turns out dead still blocklists
        // keyed on the release's identity rather than only its name.
        string? TorrentHashOrItemId = null);

    /// <summary>
    /// A targeted library search an add endpoint wants to run once this
    /// catalogue refresh has finished — the same shape it would otherwise
    /// have enqueued directly.
    ///
    /// <para>Series adding queued <c>series.catalog.refresh</c> (which learns
    /// the episode list) and the targeted <c>library.search.tv</c> job
    /// side by side. Both went straight to the queue, so the two workers ran
    /// in whichever order they were picked up — often the search first, since
    /// a fresh series has no episode rows yet to search for. Carrying the
    /// search request as part of the refresh payload instead means the
    /// refresh handler enqueues it itself, after the refresh (metadata and,
    /// for a series, the episode catalogue) has actually landed.</para>
    /// </summary>
    internal sealed record CatalogRefreshPendingSearch(
        string LibraryId,
        string LibraryName,
        string MediaType,
        int RetryDelayHours,
        string TriggeredBy,
        string TargetEntityId);

    internal sealed record CatalogRefreshPayload(
        string? Id,
        string? Title,
        string? ImdbId,
        IReadOnlyList<CatalogRefreshPendingSearch>? PendingSearches = null);

    internal static LibrarySearchPayload? ParseLibraryPayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<LibrarySearchPayload>(payloadJson ?? "{}", Options);
        }
        catch
        {
            return null;
        }
    }

    internal static LibraryQualityPayload? ParseQualityPayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<LibraryQualityPayload>(payloadJson ?? "{}", Options);
        }
        catch
        {
            return null;
        }
    }

    internal static EpisodeSearchPayload? ParseEpisodeSearchPayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<EpisodeSearchPayload>(payloadJson ?? "{}", Options);
        }
        catch
        {
            return null;
        }
    }

    internal static SeasonSearchPayload? ParseSeasonSearchPayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<SeasonSearchPayload>(payloadJson ?? "{}", Options);
        }
        catch
        {
            return null;
        }
    }

    internal static CatalogRefreshPayload? ParseCatalogRefreshPayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<CatalogRefreshPayload>(payloadJson ?? "{}", Options);
        }
        catch
        {
            return null;
        }
    }

    internal static IntakeSyncPayload? ParseIntakeSyncPayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<IntakeSyncPayload>(payloadJson ?? "{}", Options);
        }
        catch
        {
            return null;
        }
    }
}
