using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.Subtitles;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Notifications;

namespace Deluno.Worker.Jobs;

/// <summary>
/// Searches one title for its wanted subtitle languages, right now, because
/// a person asked for it — not the library's own bounded slice.
///
/// <para><b>Its own job kind and its own lane, by the owner's decision on #840.</b>
/// The Wanted screen's bulk "search all" queues one of these per listed title,
/// and a title's own subtitle panel can queue one for just that title. Sharing
/// <c>library.subtitles.search</c>'s lane would mean a person's own "search
/// this now" click waits behind whatever the library's automatic pass is doing
/// — the same mistake <see cref="LibrarySubtitleSearchJobHandler"/>'s own
/// header already explains for <c>subtitle.sync</c>.</para>
///
/// <para><b>Deliberately simpler than the library pass.</b> It reads the
/// title directly (<c>GetWantedItemAsync</c>) rather than a backoff-bounded
/// slice, so a title mid-backoff is still searched — asking "now" means now.
/// It does not fetch a forced track or queue a timing-sync pass for what it
/// finds below the cutoff: those are automatic-pass refinements a person
/// clicking "search this title" is not asking for, and the automatic pass
/// still owns both once this job's fetch has landed a file.</para>
/// </summary>
public sealed class SubtitleTitleSearchJobHandler(
    ILibrariesRepository librariesRepository,
    IMediaSubtitleRepository mediaSubtitleRepository,
    IMediaSubtitleOverrideRepository mediaSubtitleOverrideRepository,
    ISubtitleFetchService subtitleFetchService,
    NotificationEventPublisher notificationEventPublisher,
    IActivityFeedRepository activityFeedRepository)
    : IJobHandler
{
    public const string JobType = "subtitle.title.search";

    string IJobHandler.JobType => JobType;

    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        var payload = ParsePayload(job.PayloadJson);
        if (payload is null || string.IsNullOrWhiteSpace(payload.LibraryId) || string.IsNullOrWhiteSpace(payload.MediaId))
        {
            return "This title search job does not say which title it is for, so nothing was searched.";
        }

        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var library = libraries.FirstOrDefault(item => item.Id == payload.LibraryId);
        if (library is null)
        {
            return "That library no longer exists, so nothing was searched for.";
        }

        var kind = payload.Kind;
        // The override belongs to the title level: the movie itself, or the
        // show for an episode — never the episode, so its own id is the one
        // to resolve against, not the episode's (#843).
        var overrideMediaId = payload.OverrideMediaId ?? payload.MediaId;
        var titleOverride = await mediaSubtitleOverrideRepository.GetAsync(kind, overrideMediaId, cancellationToken);
        var (languages, _) = SubtitleLanguagePreference.Resolve(library.SubtitleLanguages, library.SubtitleLanguageMode, titleOverride);

        if (languages.Count == 0)
        {
            return $"{library.Name} is not asking for any subtitle languages, so nothing was searched for.";
        }

        var item = await mediaSubtitleRepository.GetWantedItemAsync(
            kind, payload.MediaId, languages, library.SubtitleEmbeddedCounts, cancellationToken);
        if (item is null)
        {
            return "This title has no file on disk to search subtitles for.";
        }

        if (item.LanguagesToFetch.Count == 0)
        {
            return $"{item.Title} already has every subtitle language it asks for.";
        }

        var existingSubtitles = await mediaSubtitleRepository.ListSubtitlesAsync(kind, item.MediaId, cancellationToken);
        var languagesAlreadyHeld = existingSubtitles
            .Select(subtitle => subtitle.Language)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var request = new SubtitleSearchRequest(
            Title: item.Title,
            Year: item.Year,
            SeasonNumber: item.SeasonNumber,
            EpisodeNumber: item.EpisodeNumber,
            EpisodeTitle: item.EpisodeTitle,
            ReleaseName: item.ReleaseName,
            ImdbId: null,
            Languages: item.LanguagesToFetch,
            IsEpisode: kind == MediaKind.Series);

        var typePreferences = SubtitleLanguageTypePreferenceCodec.Normalize(library.SubtitleLanguageTypePreferences);
        var tally = new SubtitleDecisionTally();

        foreach (var language in item.LanguagesToFetch)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var blockedKeys = await mediaSubtitleRepository.ListBlockedKeysAsync(kind, item.MediaId, cancellationToken);
            var typePreference = SubtitleLanguageTypePreferenceCodec.For(typePreferences, language);

            var outcome = await subtitleFetchService.FetchAsync(
                request, language, isEpisodeMedia: kind == MediaKind.Series, videoPath: item.FilePath,
                hearingImpairedPreference: typePreference.HearingImpaired, cancellationToken,
                library.SubtitleContentPolicy, library.SubtitleNamePolicy, library.SubtitleOmitLanguageCode, blockedKeys);

            foreach (var candidate in outcome.RejectedCandidates ?? [])
            {
                await mediaSubtitleRepository.BlockSubtitleAsync(
                    kind, item.MediaId, candidate.Language, candidate.BlockKey,
                    candidate.ProviderKey, candidate.ReleaseName, candidate.Reason, cancellationToken);

                await activityFeedRepository.RecordActivityAsync(
                    "subtitle.rejected",
                    $"Rejected a {candidate.Language} subtitle for {item.Title} from {candidate.ProviderKey}: {candidate.Reason} It will not be fetched again; the next search tries the next candidate.",
                    null,
                    job.Id,
                    kind == MediaKind.Series ? "series" : "movie",
                    item.MediaId,
                    cancellationToken);
            }

            if (outcome.Reason.StartsWith("No subtitle providers", StringComparison.Ordinal))
            {
                return $"{item.Title} wants subtitles and no providers are set up yet, so nothing could be fetched. Add one under Find & Download.";
            }

            if (!outcome.Found || outcome.WrittenPath is null)
            {
                var decision = outcome.Failure?.Kind == IntegrationFailureKind.MalformedResponse
                    ? SubtitleDecision.NeedsReview
                    : SubtitleDecision.Rejected;
                tally = tally.With(decision);

                await mediaSubtitleRepository.RecordAttemptHistoryAsync(
                    kind, item.MediaId, language, provider: null,
                    $"{decision.ToStoredName()}: {outcome.Reason}", cancellationToken);
                await notificationEventPublisher.PublishSubtitleFailedAsync(item.Title, language, outcome.Reason, cancellationToken);
                continue;
            }

            var wasAlreadyHeld = languagesAlreadyHeld.Contains(language);
            await mediaSubtitleRepository.RecordFetchedAsync(
                kind, item.MediaId,
                new MediaSubtitleRow(
                    Language: outcome.Language,
                    Source: "fetched",
                    Forced: false,
                    HearingImpaired: outcome.HearingImpaired,
                    FilePath: outcome.WrittenPath,
                    StreamIndex: null,
                    Codec: "srt",
                    Provider: outcome.ProviderKey,
                    MatchRung: (int)outcome.Match,
                    SourceKey: outcome.SourceKey),
                cancellationToken);

            if (wasAlreadyHeld)
            {
                await notificationEventPublisher.PublishSubtitleUpgradedAsync(item.Title, language, cancellationToken);
            }
            else
            {
                await notificationEventPublisher.PublishSubtitleDownloadedAsync(item.Title, language, cancellationToken);
            }

            var decisionOnSuccess = outcome.Match >= SubtitleCutoff.Rung
                ? SubtitleDecision.MeetsSubtitlePlan
                : SubtitleDecision.UsableFallback;
            tally = tally.With(decisionOnSuccess);

            if (decisionOnSuccess == SubtitleDecision.MeetsSubtitlePlan)
            {
                await mediaSubtitleRepository.ClearAttemptAsync(kind, item.MediaId, language, cancellationToken);
            }

            await mediaSubtitleRepository.RecordAttemptHistoryAsync(
                kind, item.MediaId, language, outcome.ProviderKey,
                $"{decisionOnSuccess.ToStoredName()}: {SubtitleMatchRanking.Describe(outcome.Match)}",
                cancellationToken);
        }

        return $"{item.Title}: {SubtitleDecisions.Describe(tally, library.Name)}";
    }

    private static SubtitleTitleSearchPayload? ParsePayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<SubtitleTitleSearchPayload>(payloadJson ?? "{}", JobPayloads.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <param name="OverrideMediaId">
    /// The id the title-level override is keyed by, when it differs from
    /// <paramref name="MediaId"/> — an episode's own override lives on its
    /// show, never the episode (#843). Null for a movie, where the two are
    /// the same id.
    /// </param>
    public sealed record SubtitleTitleSearchPayload(
        MediaKind Kind,
        string LibraryId,
        string MediaId,
        string? OverrideMediaId = null);
}
