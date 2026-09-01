using Deluno.Quality;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Quality.Data;
using Deluno.Quality.ReleasePreferences;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Jobs;

/// <summary>
/// Reads what a file actually is — codec, audio, channel layout — and records
/// it against the copy you hold.
///
/// <para><b>Why it is its own pass.</b> These facts were briefly read inside
/// the subtitle scan, because that pass already runs ffprobe and opening the
/// file twice looked wasteful. James: <i>"dont you think its better we separate
/// these jobs so nothing relies on each other or fights or conflicts or
/// overlaps... everything needs to run independently"</i>. He is right, and the
/// coupling had already produced a defect: the subtitle scan returns
/// immediately for a library asking for no subtitle languages, so turning
/// subtitles off would have silently stopped codecs ever being read. One saved
/// file read is not worth a feature that only works while an unrelated one is
/// switched on.</para>
///
/// <para><b>What it is for.</b> The codec, the audio and the channel layout are
/// otherwise parsed from the release name, which carries them by convention and
/// carries nothing once a library has been renamed on the way in. On the rig,
/// <c>Big Buck Bunny (2008).mkv</c> yields none of the three and the Codec
/// switch draws a dash on every card.</para>
///
/// <para><b>Bounded like the scans beside it.</b> One slice per job, re-queued
/// while there is more to do, so it drains at the lane's own pace rather than
/// holding a lease over a library.</para>
/// </summary>
public sealed class LibraryMediaProbeJobHandler(
    IMediaStateRepository mediaStateRepository,
    IMediaProbeService mediaProbeService,
    IJobScheduler jobScheduler,
    ILogger<LibraryMediaProbeJobHandler> logger,
    TimeProvider timeProvider,
    ILibrariesRepository librariesRepository,
    IQualityRepository qualityRepository,
    IReleasePreferencePlanRepository releasePreferencePlanRepository)
    : IJobHandler
{
    /// <summary>
    /// How many files one slice reads.
    ///
    /// <para>Local disk and a local process, so this is sized like the subtitle
    /// scan rather than like an indexer call — the thing being protected is the
    /// lane's own concurrency, not somebody else's server.</para>
    /// </summary>
    private const int SliceSize = 40;

    public string JobType => "library.media.probe";

    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        var kind = job.RelatedEntityType == "series" ? MediaKind.Series : MediaKind.Movie;

        // The probe pass is also the repair point for the installed-file
        // preference baseline. Load the governing library/profile data once
        // for the slice, rather than resolving it once per stream read.
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var librariesById = libraries.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var profiles = await qualityRepository.ListQualityProfilesAsync(cancellationToken);
        var profilesById = profiles.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var customFormats = await qualityRepository.ListCustomFormatsAsync(cancellationToken);
        var preferencePlans = new Dictionary<string, ReleasePreferencePlan?>(StringComparer.OrdinalIgnoreCase);
        var invalidPreferenceProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles)
        {
            try
            {
                preferencePlans[profile.Id] = await QualityProfileResolver.ResolveReleasePreferencePlanAsync(
                    qualityRepository,
                    releasePreferencePlanRepository,
                    profile.Id,
                    cancellationToken,
                    customFormats);
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
            {
                // A pinned plan that is missing or hash-invalid must not be
                // silently recompiled from today's guide. The file facts are
                // still recorded below, while this profile waits for the
                // configuration to be repaired.
                invalidPreferenceProfiles.Add(profile.Id);
                logger.LogWarning(
                    exception,
                    "Could not resolve the immutable release-preference plan for profile {ProfileId}; installed-file snapshots will not be rewritten for this slice.",
                    profile.Id);
            }
        }

        var expectedPlans = libraries
            .Where(library => string.Equals(
                    MediaPolicyCatalog.NormalizeMediaType(library.MediaType),
                    kind == MediaKind.Series ? "tv" : "movies",
                    StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(library.QualityProfileId)
                && preferencePlans.TryGetValue(library.QualityProfileId, out var plan)
                && plan is not null)
            .Select(library =>
            {
                var plan = preferencePlans[library.QualityProfileId!]!;
                return new MediaPreferencePlanExpectation(
                    library.Id,
                    plan.Id,
                    plan.Version,
                    plan.PlanHash);
            })
            .ToArray();
        var candidates = await mediaStateRepository.ListFileProbeCandidatesAsync(
            kind,
            SliceSize,
            cancellationToken,
            expectedPlans);
        if (candidates.Count == 0)
        {
            return "Every file Deluno holds has been read and evaluated under its current preference plan.";
        }

        var wantedItems = await mediaStateRepository.ListWantedByIdsAsync(
            kind,
            candidates.Select(candidate => candidate.MediaId).ToArray(),
            cancellationToken);

        var read = 0;
        var unreadable = 0;
        var preferenceSnapshots = 0;
        var evaluatedUtc = timeProvider.GetUtcNow();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var probe = await mediaProbeService.ProbeAsync(candidate.FilePath, cancellationToken);

            // A file ffprobe cannot read is still recorded, with nothing in it.
            // The write stamps the bookkeeping either way, so an unreadable
            // file is not retried at the front of every future pass — and the
            // COALESCE means a failed read never erases what the release name
            // already said.
            var facts = probe.Status == "succeeded"
                ? new ProbedFileFacts(
                    MediaProbedFacts.VideoCodec(probe.VideoStreams.FirstOrDefault()?.Codec),
                    MediaProbedFacts.AudioCodec(
                        probe.AudioStreams.FirstOrDefault()?.Codec,
                        probe.AudioStreams.FirstOrDefault()?.Profile),
                    MediaProbedFacts.AudioChannels(
                        probe.AudioStreams.FirstOrDefault()?.ChannelLayout,
                        probe.AudioStreams.FirstOrDefault()?.Channels))
                : new ProbedFileFacts(null, null, null);

            if (probe.Status == "succeeded")
            {
                read++;
            }
            else
            {
                unreadable++;
            }

            await mediaStateRepository.UpdateProbedFileFactsAsync(
                kind,
                candidate.MediaId,
                candidate.FilePath,
                facts,
                cancellationToken,
                candidate.LibraryId);

            if (TryResolvePreferenceContext(
                    candidate,
                    wantedItems,
                    librariesById,
                    profilesById,
                    preferencePlans,
                    invalidPreferenceProfiles,
                    out var library,
                    out var profile,
                    out var preferencePlan,
                    out var wanted))
            {
                // A changed path/size is a new physical file even when the
                // title and library are unchanged. Only carry raw evidence
                // forward when it still belongs to this file; the active plan
                // will be evaluated again from that evidence below.
                var previous = await mediaStateRepository.GetLatestPreferenceEvaluationSnapshotAsync(
                    kind,
                    candidate.MediaId,
                    candidate.LibraryId,
                    fileIdentity: null,
                    cancellationToken,
                    filePath: candidate.FilePath,
                    fileSizeBytes: candidate.FileSizeBytes);
                if (previous is not null && previous.FileSizeBytes != candidate.FileSizeBytes)
                {
                    previous = null;
                }

                var snapshot = InstalledPreferenceEvaluationFactory.Create(
                    profile,
                    candidate.MediaId,
                    library.Id,
                    candidate.FilePath,
                    candidate.FileSizeBytes,
                    wanted.CurrentQuality,
                    evaluatedUtc,
                    source: "library-media-probe",
                    customFormats,
                    preferencePlan: preferencePlan,
                    baselineFacts: previous?.Facts,
                    probedVideoCodec: facts.VideoCodec,
                    probedAudioCodec: facts.AudioCodec,
                    probedAudioChannels: facts.AudioChannels);

                if (snapshot is not null)
                {
                    await mediaStateRepository.SavePreferenceEvaluationSnapshotAsync(
                        kind,
                        snapshot,
                        cancellationToken);
                    preferenceSnapshots++;
                }
            }
        }

        // More to do, so the next slice queues itself. Same shape as the
        // subtitle scan's own slicing, and deliberately not the same job.
        if (candidates.Count == SliceSize)
        {
            await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: JobType,
                    Source: "system",
                    PayloadJson: null,
                    RelatedEntityType: job.RelatedEntityType,
                    RelatedEntityId: null,
                    // One queued slice at a time per kind: without this a pass
                    // that starts while the previous one is still draining
                    // stacks a second chain of slices behind the first.
                    DedupeKey: $"media-probe:{job.RelatedEntityType}"),
                cancellationToken);
        }

        if (unreadable > 0)
        {
            logger.LogDebug(
                "Media probe read {Read} files, could not read {Unreadable}, and refreshed {PreferenceSnapshots} installed-file preference snapshots.",
                read,
                unreadable,
                preferenceSnapshots);
        }
        else
        {
            logger.LogDebug(
                "Media probe read {Read} files and refreshed {PreferenceSnapshots} installed-file preference snapshots.",
                read,
                preferenceSnapshots);
        }

        return unreadable == 0
            ? $"Read {read} file{(read == 1 ? "" : "s")}."
            : $"Read {read} of {candidates.Count} files; {unreadable} could not be read.";
    }

    private static bool TryResolvePreferenceContext(
        MediaFileProbeCandidate candidate,
        IReadOnlyList<MediaWantedItem> wantedItems,
        IReadOnlyDictionary<string, Deluno.Libraries.Contracts.LibraryItem> librariesById,
        IReadOnlyDictionary<string, Deluno.Quality.Contracts.QualityProfileItem> profilesById,
        IReadOnlyDictionary<string, ReleasePreferencePlan?> preferencePlans,
        ISet<string> invalidPreferenceProfiles,
        out Deluno.Libraries.Contracts.LibraryItem library,
        out Deluno.Quality.Contracts.QualityProfileItem profile,
        out ReleasePreferencePlan? preferencePlan,
        out MediaWantedItem wanted)
    {
        library = null!;
        profile = null!;
        preferencePlan = null;
        wanted = null!;

        if (string.IsNullOrWhiteSpace(candidate.LibraryId)
            || !librariesById.TryGetValue(candidate.LibraryId, out library!))
        {
            return false;
        }

        wanted = wantedItems.FirstOrDefault(item =>
            string.Equals(item.Id, candidate.MediaId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.LibraryId, candidate.LibraryId, StringComparison.OrdinalIgnoreCase))!;
        if (wanted is null || string.IsNullOrWhiteSpace(library.QualityProfileId)
            || !profilesById.TryGetValue(library.QualityProfileId, out profile!))
        {
            return false;
        }

        if (invalidPreferenceProfiles.Contains(profile.Id)
            || !preferencePlans.TryGetValue(profile.Id, out preferencePlan))
        {
            return false;
        }

        return true;
    }
}
