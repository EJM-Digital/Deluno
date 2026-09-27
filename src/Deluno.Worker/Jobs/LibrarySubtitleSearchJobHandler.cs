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
/// Fetches the subtitles a library asked for and does not have.
///
/// <para>The other half of <see cref="LibrarySubtitleScanJobHandler"/>: the scan
/// learns what is already on disk, and this goes and gets the rest. They run in
/// that order for the obvious reason — knowing which subtitles you already hold
/// is what decides which ones are worth fetching, and a fetch that ran first
/// would spend somebody's daily OpenSubtitles allowance on files that already
/// had English sitting beside them.</para>
///
/// <para><b>It is not a scheduler.</b> DESIGN-002 rule 3, restated because this
/// is the handler most likely to break it: nothing here decides when to run. The
/// one planner decides, and this rides a lane that already exists. Weir's
/// Subber shipped its own scheduler, its own lane <i>and</i> its own worker, and
/// that is the whole of what this port refuses to carry over.</para>
///
/// <para>What it does <b>not</b> inherit is the release search's timing. Sharing
/// the planner is one place making a decision; sharing an interval is one kind of
/// work waiting on another kind's manners. See the cadence on the planner.</para>
///
/// <para><b>No release-search number is borrowed any more.</b> The first version
/// took its retry delay from the library's <c>RetryDelayHours</c>, which is how
/// long to wait before asking an <i>indexer</i> again. The owner: <i>"nothing should
/// be shared or have to wait for another process."</i> A subtitle absent from
/// every provider is a different fact from a release absent from every indexer,
/// and pacing one by the other means changing your indexer manners silently
/// changes how often your subtitles are chased.</para>
///
/// <para><b>The slice is <c>MaxItemsPerRun</c>,</b> and that is the difference
/// from the scan. A scan reads local disk with a local process and is bounded at
/// a hundred; this makes outbound requests to somebody else's server, which is
/// the exact thing <c>MaxItemsPerRun</c> exists to bound. A library set to ten
/// titles an hour means ten here too.</para>
/// </summary>
public sealed class LibrarySubtitleSearchJobHandler(
    ILibrariesRepository librariesRepository,
    IMediaSubtitleRepository mediaSubtitleRepository,
    ISubtitleFetchService subtitleFetchService,
    IJobScheduler jobScheduler,
    NotificationEventPublisher notificationEventPublisher)
    : IJobHandler
{
    public string JobType => "library.subtitles.search";

    /// <summary>
    /// How long a language waits before it is asked for again, after nobody had
    /// it. Doubles from here and stops at a fortnight — never a permanent skip,
    /// because work that silently leaves the system is work nobody hears about
    /// the day somebody finally uploads the subtitle.
    ///
    /// <para>Six hours, chosen for what it is: a subtitle absent from every
    /// provider this morning will very rarely appear by lunchtime, and a new
    /// upload is usually days away rather than hours. Deliberately <i>not</i> the
    /// library's <c>RetryDelayHours</c>, which is an indexer manner.</para>
    /// </summary>
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromHours(6);


    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        var payload = ParsePayload(job.PayloadJson);
        if (payload is null || string.IsNullOrWhiteSpace(payload.LibraryId))
        {
            throw new InvalidOperationException("Subtitle search job payload could not be read.");
        }

        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var library = libraries.FirstOrDefault(item => item.Id == payload.LibraryId);
        if (library is null)
        {
            return "That library no longer exists, so nothing was searched for.";
        }

        var languages = library.SubtitleLanguages ?? [];
        if (languages.Count == 0)
        {
            return $"{library.Name} is not asking for any subtitle languages, so nothing was searched for.";
        }

        var kind = string.Equals(library.MediaType, "tv", StringComparison.OrdinalIgnoreCase)
            ? MediaKind.Series
            : MediaKind.Movie;

        var slice = Math.Max(1, library.MaxItemsPerRun);
        var wanted = await mediaSubtitleRepository.ListWantedAsync(
            kind, library.Id, languages, slice, library.SubtitleEmbeddedCounts, cancellationToken);
        var timingPolicy = SubtitleTimingPolicyCodec.Normalize(library.SubtitleTimingPolicy)
            ?? new SubtitleTimingPolicy();

        if (wanted.Count == 0)
        {
            return $"Every file in {library.Name} has the subtitles you asked for.";
        }

        var tally = new SubtitleDecisionTally();
        var noProviders = false;
        var typePreferences = SubtitleLanguageTypePreferenceCodec.Normalize(library.SubtitleLanguageTypePreferences);

        foreach (var item in wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Read before any fetch for this item: whether a language already
            // had a row decides "downloaded" from "upgraded" once the fetch
            // below succeeds, and a fetch's own RecordFetchedAsync call would
            // have already overwritten that answer.
            var existingSubtitles = await mediaSubtitleRepository.ListSubtitlesAsync(kind, item.MediaId, cancellationToken);
            var languagesAlreadyHeld = existingSubtitles
                .Select(subtitle => subtitle.Language)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // A forced track is fetched once and left alone after that — it is
            // never re-checked against the cutoff the way the full subtitle is,
            // because "made for this file" means nothing for four lines of
            // Elvish (#835, #923).
            var forcedAlreadyHeld = existingSubtitles
                .Where(subtitle => subtitle.Forced)
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

            foreach (var language in item.LanguagesToFetch)
            {
                var blockedKeys = await mediaSubtitleRepository.ListBlockedKeysAsync(kind, item.MediaId, cancellationToken);
                var typePreference = SubtitleLanguageTypePreferenceCodec.For(typePreferences, language);

                // Independent of the full subtitle below: asked for once and
                // recorded with Forced: true, which never counts as coverage
                // and is never re-checked against the timing cutoff (#835,
                // #923). Done first so a failure or a skip further down this
                // loop can never skip it by falling through a `continue`.
                if (typePreference.IncludeForced && !forcedAlreadyHeld.Contains(language))
                {
                    var forcedOutcome = await subtitleFetchService.FetchForcedAsync(
                        request, language, isEpisodeMedia: kind == MediaKind.Series, videoPath: item.FilePath,
                        cancellationToken, library.SubtitleContentPolicy, library.SubtitleNamePolicy, library.SubtitleOmitLanguageCode, blockedKeys);

                    if (forcedOutcome.Found && forcedOutcome.WrittenPath is not null)
                    {
                        await mediaSubtitleRepository.RecordFetchedAsync(
                            kind, item.MediaId,
                            new MediaSubtitleRow(
                                Language: forcedOutcome.Language,
                                Source: "fetched",
                                Forced: true,
                                HearingImpaired: false,
                                FilePath: forcedOutcome.WrittenPath,
                                StreamIndex: null,
                                Codec: "srt",
                                Provider: forcedOutcome.ProviderKey,
                                MatchRung: (int)forcedOutcome.Match,
                                SourceKey: forcedOutcome.SourceKey),
                            cancellationToken);
                        await notificationEventPublisher.PublishSubtitleDownloadedAsync(item.Title, $"{language} (forced)", cancellationToken);
                        forcedAlreadyHeld.Add(language);
                    }
                }

                var outcome = await subtitleFetchService.FetchAsync(
                    request,
                    language,
                    isEpisodeMedia: kind == MediaKind.Series,
                    videoPath: item.FilePath,
                    // Per-language, from the library's own setting (#836).
                    // Nobody who has never opened it gets the default: either
                    // is coverage, and the pick prefers a plain one where both
                    // exist — exactly what this always did.
                    hearingImpairedPreference: typePreference.HearingImpaired,
                    cancellationToken,
                    library.SubtitleContentPolicy,
                    library.SubtitleNamePolicy,
                    library.SubtitleOmitLanguageCode,
                    blockedKeys);

                // A candidate that turned out not to be a real subtitle (#898):
                // blocklisted so the next search — automatic or manual — never
                // downloads the same bad file again, whether or not this fetch
                // went on to find something else.
                foreach (var candidate in outcome.RejectedCandidates ?? [])
                {
                    await mediaSubtitleRepository.BlockSubtitleAsync(
                        kind, item.MediaId, candidate.Language, candidate.BlockKey,
                        candidate.ProviderKey, candidate.ReleaseName, candidate.Reason, cancellationToken);
                }

                if (outcome.Reason.StartsWith("No subtitle providers", StringComparison.Ordinal))
                {
                    noProviders = true;
                    // Nothing is configured. Every remaining attempt would say
                    // the same thing and cost a database round trip to say it.
                    break;
                }

                if (!outcome.Found || outcome.WrittenPath is null)
                {
                    // Nobody said no when a provider answered with something
                    // Deluno could not read: that is an open question rather
                    // than a refusal, and the two are worth telling apart in
                    // Activity because only one of them means "stop asking".
                    var decision = outcome.Failure?.Kind == IntegrationFailureKind.MalformedResponse
                        ? SubtitleDecision.NeedsReview
                        : SubtitleDecision.Rejected;
                    tally = tally.With(decision);

                    // Remembered, so the next slice asks something else, and the
                    // library keeps moving instead of asking the same ten titles
                    // for ever. The delay is subtitles' own — see
                    // <c>FirstRetryDelay</c> — and doubles from there.
                    await mediaSubtitleRepository.RecordAttemptAsync(
                        kind,
                        item.MediaId,
                        language,
                        $"{decision.ToStoredName()}: {outcome.Reason}",
                        FirstRetryDelay,
                        cancellationToken,
                        outcome.Failure);
                    await notificationEventPublisher.PublishSubtitleFailedAsync(
                        item.Title, language, outcome.Reason, cancellationToken);
                    continue;
                }

                var wasAlreadyHeld = languagesAlreadyHeld.Contains(language);
                await mediaSubtitleRepository.RecordFetchedAsync(
                    kind,
                    item.MediaId,
                    new MediaSubtitleRow(
                        Language: outcome.Language,
                        // The store carries where a subtitle came from, and this
                        // is the one source that did not need finding: Deluno
                        // wrote the file, so the row is recorded at that moment
                        // rather than by a later scan.
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

                if (outcome.Match >= SubtitleCutoff.Rung)
                {
                    // At the cutoff: made for this file, so the timing is right
                    // and there is nothing better to find. Done, and the attempt
                    // row goes.
                    tally = tally.With(SubtitleDecision.MeetsSubtitlePlan);
                    await mediaSubtitleRepository.ClearAttemptAsync(kind, item.MediaId, language, cancellationToken);
                }
                else
                {
                    // Watchable, and not provably in time. The owner: *"we need the
                    // best method, no point spreading lies about subs that may be
                    // out of sync."* So the language is covered — the file is on
                    // disk and you can watch tonight — and it stays on the list,
                    // because a better one may be uploaded tomorrow.
                    //
                    // The attempt row is what keeps it there, and it is the same
                    // row a failure writes: one mechanism, so an upgrade cannot
                    // acquire a second idea of when to look again.
                    // Which of the two below-target states this is depends on
                    // whether the timing repair is actually going to run, so it
                    // is decided here rather than guessed from the rung.
                    var syncing = timingPolicy.ShouldSync(outcome.Match)
                        && !IsProviderExcluded(timingPolicy, outcome.ProviderKey);
                    var decision = syncing ? SubtitleDecision.NeedsSync : SubtitleDecision.UsableFallback;
                    tally = tally.With(decision);

                    await mediaSubtitleRepository.RecordAttemptAsync(
                        kind,
                        item.MediaId,
                        language,
                        $"{decision.ToStoredName()}: {SubtitleMatchRanking.Describe(outcome.Match)}",
                        FirstRetryDelay,
                        cancellationToken);

                    // And this is where the timing gets fixed. "Not provably in
                    // time" is precisely the set worth listening to the audio
                    // for, so the rung that keeps a subtitle on the upgrade list
                    // is the same rung that sends it to be timed — unless the
                    // library deliberately narrows that threshold or excludes
                    // this provider. Both choices are named in the policy and
                    // survive in the queued job payload.
                    if (syncing)
                    {
                        // Queued rather than done here: this lane is holding a
                        // subtitle provider's attention and timing is several
                        // seconds of local FFmpeg. See SubtitleSyncJobHandler.
                        await jobScheduler.EnqueueAsync(
                            new EnqueueJobRequest(
                                JobType: "subtitle.sync",
                                Source: library.MediaType,
                                PayloadJson: JsonSerializer.Serialize(
                                    new SubtitleSyncJobHandler.SubtitleSyncPayload(
                                        item.FilePath,
                                        outcome.WrittenPath,
                                        // Null, and knowingly. The title's own
                                        // language would let the sync prefer the
                                        // original audio over a dub, but the wanted
                                        // row does not carry it and neither
                                        // catalogue stores it — adding it is a
                                        // migration on two tables for a case the
                                        // fallback already handles: with no
                                        // preference the sync takes the first audio
                                        // track, which is where every muxer puts
                                        // the original. What it cannot get right is
                                        // a foreign-language film muxed dub-first.
                                        null,
                                        timingPolicy),
                                    JobPayloads.Options),
                                RelatedEntityType: "library",
                                RelatedEntityId: library.Id,
                                // One timing job per subtitle file. A language
                                // re-fetched before the first one runs replaces it
                                // rather than queueing a second pass over the same
                                // audio.
                                DedupeKey: $"subtitle.sync:{outcome.WrittenPath}"),
                            cancellationToken);
                    }
                }
            }

            if (noProviders)
            {
                break;
            }
        }

        // More to do, so queue the next slice. The cycle would come back to it
        // anyway; this means a library that is a long way behind catches up in
        // one window rather than one slice a night.
        //
        // Not conditional on having found anything, which it was: a slice that
        // found nothing has still moved every one of its titles onto a backoff,
        // so the next slice asks *different* titles. Stopping on a miss would
        // have meant a library whose first ten titles have no subtitles never
        // reaching the eleventh.
        if (!noProviders && wanted.Count == slice)
        {
            await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: JobType,
                    Source: library.MediaType,
                    PayloadJson: job.PayloadJson,
                    RelatedEntityType: "library",
                    RelatedEntityId: library.Id,
                    DedupeKey: $"library.subtitles.search:{library.Id}"),
                cancellationToken);
        }

        return noProviders
            ? $"{library.Name} wants subtitles and no providers are set up yet, so nothing could be fetched. Add one under Find & Download."
            : SubtitleDecisions.Describe(tally, library.Name);
    }

    private static LibrarySubtitleSearchPayload? ParsePayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<LibrarySubtitleSearchPayload>(payloadJson ?? "{}", JobPayloads.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsProviderExcluded(SubtitleTimingPolicy policy, string? providerKey)
        => !string.IsNullOrWhiteSpace(providerKey)
            && policy.ExcludedProviders?.Contains(providerKey.Trim(), StringComparer.OrdinalIgnoreCase) == true;

    private sealed record LibrarySubtitleSearchPayload(string LibraryId, string? LibraryName, string? MediaType);
}
