using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.Subtitles;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Media;

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
    IJobScheduler jobScheduler)
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

        if (wanted.Count == 0)
        {
            return $"Every file in {library.Name} has the subtitles you asked for.";
        }

        var found = 0;
        var attempted = 0;
        var noProviders = false;

        foreach (var item in wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
                attempted++;

                var outcome = await subtitleFetchService.FetchAsync(
                    request,
                    language,
                    isEpisodeMedia: kind == MediaKind.Series,
                    videoPath: item.FilePath,
                    // The setting for this arrives with #321's languages work.
                    // Until then a hearing-impaired track is coverage, which is
                    // what DESIGN-002 settled, and the pick prefers a plain one
                    // where both exist.
                    excludeHearingImpaired: false,
                    cancellationToken);

                if (outcome.Reason.StartsWith("No subtitle providers", StringComparison.Ordinal))
                {
                    noProviders = true;
                    // Nothing is configured. Every remaining attempt would say
                    // the same thing and cost a database round trip to say it.
                    break;
                }

                if (!outcome.Found || outcome.WrittenPath is null)
                {
                    // Remembered, so the next slice asks something else, and the
                    // library keeps moving instead of asking the same ten titles
                    // for ever. The delay is subtitles' own — see
                    // <c>FirstRetryDelay</c> — and doubles from there.
                    await mediaSubtitleRepository.RecordAttemptAsync(
                        kind,
                        item.MediaId,
                        language,
                        outcome.Reason,
                        FirstRetryDelay,
                        cancellationToken);
                    continue;
                }

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
                        MatchRung: (int)outcome.Match),
                    cancellationToken);

                if (outcome.Match >= SubtitleCutoff.Rung)
                {
                    // At the cutoff: made for this file, so the timing is right
                    // and there is nothing better to find. Done, and the attempt
                    // row goes.
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
                    await mediaSubtitleRepository.RecordAttemptAsync(
                        kind,
                        item.MediaId,
                        language,
                        SubtitleMatchRanking.Describe(outcome.Match),
                        FirstRetryDelay,
                        cancellationToken);

                    // And this is where the timing gets fixed. "Not provably in
                    // time" is precisely the set worth listening to the audio
                    // for, so the rung that keeps a subtitle on the upgrade list
                    // is the same rung that sends it to be timed — one test, not
                    // two, and nothing to configure.
                    //
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
                                    null),
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

                found++;
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

        return BuildSummary(library.Name, found, attempted, noProviders);
    }

    /// <summary>
    /// Written for the person reading Activity, who wants to know what arrived —
    /// and told plainly when nothing can arrive, because a library quietly
    /// finding nothing every night with no providers configured is the failure
    /// nobody would otherwise notice.
    /// </summary>
    private static string BuildSummary(string libraryName, int found, int attempted, bool noProviders)
    {
        if (noProviders)
        {
            return $"{libraryName} wants subtitles and no providers are set up yet, so nothing could be fetched. Add one under Find & Download.";
        }

        if (attempted == 0)
        {
            return $"Nothing in {libraryName} was short of a subtitle.";
        }

        return found == 0
            ? $"Looked for {attempted} subtitle(s) in {libraryName} and none of the providers had them."
            : $"Fetched {found} of {attempted} subtitle(s) looked for in {libraryName}.";
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

    private sealed record LibrarySubtitleSearchPayload(string LibraryId, string? LibraryName, string? MediaType);
}
