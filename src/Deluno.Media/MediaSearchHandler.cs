using Deluno.Contracts;
using System.Text.Json;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Data;
using Deluno.Jobs.Decisions;
using Deluno.Libraries.Data;
using Deluno.Platform.Data;
using Deluno.Platform.Contracts;
using Deluno.Quality.Data;
using Deluno.Quality;

namespace Deluno.Media;

public sealed record MediaSearchResult(
    bool NotFound,
    string Outcome,
    string Summary,
    string Reason,
    string? ReleaseName,
    string? IndexerName,
    string? DispatchStatus,
    string? DispatchMessage,
    IReadOnlyList<MediaSearchCandidate> Candidates,
    IReadOnlyList<IntegrationFailure>? Failures = null,
    string? DownloadClientName = null)
{
    public static MediaSearchResult NotFoundResult()
        => new(true, string.Empty, string.Empty, string.Empty, null, null, null, null, [], []);
}

/// <summary>
/// Shared workflow for manually searching one movie or series and optionally
/// dispatching the planner's selected result. Series-specific episode and
/// season searches remain in Deluno.Series.
/// </summary>
public static class MediaSearchHandler
{
    public static async Task<MediaSearchResult> ExecuteAsync(
        MediaKind kind,
        string id,
        string? mode,
        IMediaStateRepository mediaStateRepository,
        ILibrariesRepository librariesRepository,
        IQualityRepository qualityRepository,
        IJobQueueRepository jobQueueRepository,
        IAcquisitionDecisionPipeline acquisitionPipeline,
        IDownloadClientGrabService downloadClientGrabService,
        IActivityFeedRepository activityFeedRepository,
        TimeProvider timeProvider,
        RecordMediaSearchAttemptAsync recordSearchAttemptAsync,
        CancellationToken cancellationToken,
        IMediaTagStore? mediaTagStore = null,
        IReleasePreferencePlanRepository? releasePreferencePlanRepository = null,
        string? libraryId = null,
        IPlatformSettingsRepository? platformSettingsRepository = null,
        // A whole-show search has no target episode; the provider's titles let each release be checked against its own SxxEyy (#1173).
        Func<CancellationToken, Task<IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>>?>>? episodeTitlesBySeasonAsync = null)
    {
        var item = await mediaStateRepository.GetByIdAsync(kind, id, cancellationToken);
        if (item is null)
        {
            return MediaSearchResult.NotFoundResult();
        }

        var entityType = kind == MediaKind.Movie ? "movie" : "series";
        var mediaType = kind == MediaKind.Movie ? "movies" : "tv";
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var rows = (await mediaStateRepository.ListWantedByIdsAsync(kind, [id], cancellationToken))
            .Where(candidate => candidate.Id == id)
            .ToArray();
        var (wantedItem, choiceProblem) = MediaLibraryChoice.Pick(rows, libraryId, libraries, entityType);
        if (wantedItem is null)
        {
            return BlockedResult(choiceProblem!, rows.Length > 1 && string.IsNullOrWhiteSpace(libraryId) ? MediaSearchReasons.ChooseLibrary : MediaSearchReasons.NotSearchable);
        }

        var library = libraries.FirstOrDefault(candidate => candidate.Id == wantedItem.LibraryId);
        if (library is null)
        {
            return BlockedResult(
                $"Deluno could not find the linked library for this {entityType}.",
                MediaSearchReasons.LibraryMissing);
        }

        // The title's own profile, when it has one, judges the search (#1157);
        // the library's is only the default.
        library = await TitleQualityProfile.ApplyAsync(mediaStateRepository, qualityRepository, kind, id, library, cancellationToken);

        var routing = await librariesRepository.GetLibraryRoutingAsync(library.Id, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var customFormats = await ResolveCustomFormatsAsync(
            qualityRepository,
            library.QualityProfileId,
            cancellationToken);
        // Everything the profile says, read once (#599). Two answers to one
        // question read apart are two answers that eventually disagree.
        var profileRules = await QualityProfileResolver.ResolveSearchRulesAsync(
            qualityRepository,
            library.QualityProfileId,
            cancellationToken);
        // #974: "Do not upgrade automatically" is honoured by this path too —
        // "Search now" builds its plan and picks the best candidate the same
        // way an automatic search does, so a Proper of an installed file must
        // pass over it here too.
        var properRepackAcquisitionMode = platformSettingsRepository is null
            ? null
            : await SearchPlanPreferences.ResolveProperRepackAcquisitionModeAsync(platformSettingsRepository, cancellationToken);
        var preferencePlan = await QualityProfileResolver.ResolveReleasePreferencePlanAsync(
            qualityRepository,
            releasePreferencePlanRepository,
            library.QualityProfileId,
            cancellationToken,
            customFormats,
            properRepackAcquisitionMode);
        var currentPreferenceEvaluation = wantedItem.HasFile
            ? await mediaStateRepository.GetLatestPreferenceEvaluationSnapshotAsync(
                kind,
                item.Id,
                library.Id,
                 fileIdentity: null,
                 cancellationToken,
                 filePath: wantedItem.FilePath,
                 fileSizeBytes: wantedItem.FileSizeBytes)
            : null;
        var tagNames = mediaTagStore is null
            ? []
            : (await mediaTagStore.ListAsync(kind, item.Id, cancellationToken))
                .Select(tag => tag.Name)
                .ToArray();
        var decisionPlan = await acquisitionPipeline.PlanAsync(
            new AcquisitionDecisionRequest(
                item.Title,
                item.Year,
                mediaType,
                wantedItem.CurrentQuality,
                wantedItem.TargetQuality,
                routing?.Sources ?? [],
                routing?.DownloadClients ?? [],
                customFormats,
                PreviewOnly: string.Equals(mode, "preview", StringComparison.OrdinalIgnoreCase),
                TagNames: tagNames,
                SearchKind: AcquisitionSearchKinds.Interactive,
                AvailableUtc: item.AvailableUtc,
                CurrentFilePresent: wantedItem.HasFile,
                CurrentReleaseName: wantedItem.FilePath,
                PreferencePlan: preferencePlan,
                CurrentPreferenceEvaluation: currentPreferenceEvaluation,
                RuntimeMinutes: item.RuntimeMinutes,
                EntityId: item.Id,
                ImdbId: wantedItem.ImdbId,
                OtherTitles: CatalogueMetadata.ReadAlternateTitles(item.MetadataJson),
                EpisodeTitlesBySeason: episodeTitlesBySeasonAsync is null ? null : await episodeTitlesBySeasonAsync(cancellationToken)).WithProfile(profileRules),
            cancellationToken);
        var searchPlan = decisionPlan.SearchPlan;
        var bestCandidate = searchPlan.BestCandidate;
        var outcome = decisionPlan.Outcome;
        var failures = (searchPlan.Failures ?? []).ToList();
        DownloadClientGrabResult? grabResult = null;

        // One send at a time for this title and release, and no second send of what is
        // already on its way (#1192). "Search now" skipped the check the automatic
        // searches make, so a film the add-time search had just sent to one Usenet
        // client was sent again, a minute later, to the other. The look and the
        // recorded send happen inside the gate, so a search running at the same moment
        // finds this one's send. To send a second copy on purpose, pick a release and
        // choose "Send as well" (#1175).
        using var sendGate = decisionPlan.ShouldDispatch && bestCandidate is not null
            && decisionPlan.SelectedDownloadClient is not null && decisionPlan.DispatchRequest is not null
            ? await DispatchSendGate.EnterAsync(bestCandidate.ReleaseName, [item.Id], cancellationToken)
            : null;
        var alreadyOnItsWay = sendGate is not null
            ? await DispatchCoverage.AlreadyOnItsWayAsync(jobQueueRepository, bestCandidate!.ReleaseName, [item.Id], cancellationToken)
              // Nor a release the library already holds: a title removed and restored has nothing in flight, yet its
              // file came from this very release (#1202).
              ?? await DispatchCoverage.AlreadyInLibraryAsync(jobQueueRepository, bestCandidate.ReleaseName, item.Id, wantedItem.HasFile, cancellationToken)
            : null;
        if (alreadyOnItsWay is not null)
        {
            outcome = "skipped";
        }

        if (alreadyOnItsWay is null
            && decisionPlan.ShouldDispatch
            && decisionPlan.SelectedDownloadClient is not null
            && decisionPlan.DispatchRequest is not null)
        {
            var downloadClient = decisionPlan.SelectedDownloadClient;
            grabResult = bestCandidate!.DownloadUrl is null
                ? new DownloadClientGrabResult(
                    downloadClient.DownloadClientId,
                    bestCandidate.ReleaseName,
                    false,
                    "planned",
                    "No download URL was available.")
                {
                    Failure = IntegrationFailureFactory.FromLegacy(
                        "download-client",
                        downloadClient.DownloadClientId,
                        downloadClient.DownloadClientName,
                        "grab",
                        "planned",
                        "No downloadable URL was available for this release.")
                }
                : await downloadClientGrabService.GrabAsync(
                    downloadClient.DownloadClientId,
                    decisionPlan.DispatchRequest with
                    {
                        OnDeadRelease = new DeadReleaseFollowUp(entityType, item.Id, item.Title, library.Id, library.Name)
                    },
                    cancellationToken);
            if (grabResult.Failure is { } dispatchFailure)
            {
                failures.Add(dispatchFailure);
            }
            await jobQueueRepository.RecordDownloadDispatchAsync(
                library.Id,
                mediaType,
                entityType,
                item.Id,
                bestCandidate.ReleaseName,
                bestCandidate.IndexerName,
                downloadClient.DownloadClientId,
                downloadClient.DownloadClientName,
                grabResult.Status,
                JsonSerializer.Serialize(new { searchPlan, grabResult }),
                grabResponseCode: grabResult.Succeeded ? 200 : 400,
                grabFailureCode: grabResult.Failure?.Code ?? grabResult.FailureCode,
                cancellationToken: cancellationToken,
                replacementAuthorized: wantedItem.HasFile,
                replacementExpectedPath: wantedItem.FilePath,
                clientExternalId: grabResult.ExternalId,
                startedBy: DownloadStartedBy.Manual);

            // "Search now" takes a release like any other grab, so its title is
            // on its way too (#555).
            await mediaStateRepository.MarkDownloadingIfSentAsync(kind, item.Id, grabResult.Status, now, cancellationToken);
        }

        // A search no indexer answered because each was busy is not recorded
        // (#598): the title keeps its place, and the person is told to try
        // again in a few minutes.
        if (!string.Equals(searchPlan.Reason, MediaSearchReasons.IndexersBusy, StringComparison.Ordinal))
        {
            await recordSearchAttemptAsync(
                item.Id,
                library.Id,
                "manual",
                outcome,
                now,
                now.AddHours(Math.Max(1, library.RetryDelayHours)),
                alreadyOnItsWay ?? decisionPlan.SearchResult,
                bestCandidate?.ReleaseName,
                bestCandidate?.IndexerName,
                searchPlan.Candidates.Count == 0 ? null : JsonSerializer.Serialize(searchPlan),
                cancellationToken);
        }

        await activityFeedRepository.RecordDecisionAsync(
            new DecisionExplanationPayload(
                Scope: $"{entityType}.search",
                Status: outcome,
                Reason: alreadyOnItsWay ?? decisionPlan.SearchResult,
                Inputs: new Dictionary<string, string?>
                {
                    ["title"] = item.Title,
                    ["year"] = item.Year?.ToString(),
                    ["libraryId"] = library.Id,
                    ["sourceCount"] = decisionPlan.SourceCount.ToString(),
                    ["downloadClientCount"] = decisionPlan.DownloadClientCount.ToString(),
                    ["policyVersion"] = decisionPlan.PolicyVersion,
                    ["mode"] = string.Equals(mode, "preview", StringComparison.OrdinalIgnoreCase) ? "preview" : "manual"
                },
                Outcome: grabResult is null
                    ? alreadyOnItsWay ?? searchPlan.Summary
                    : $"{grabResult.Status}: {grabResult.Message}",
                Alternatives: decisionPlan.Alternatives),
            null,
            entityType,
            item.Id,
            cancellationToken);

        await activityFeedRepository.RecordActivityAsync(
            $"{entityType}.search.manual",
            $"Search now was used for {item.Title}, from a screen or an outside app.",
            null,
            null,
            entityType,
            item.Id,
            cancellationToken);

        return new MediaSearchResult(
            NotFound: false,
            Outcome: outcome,
            Summary: alreadyOnItsWay ?? searchPlan.Summary,
            Reason: searchPlan.Reason,
            ReleaseName: bestCandidate?.ReleaseName,
            IndexerName: bestCandidate?.IndexerName,
            DispatchStatus: grabResult?.Status,
            DispatchMessage: grabResult?.Message,
            Candidates: searchPlan.Candidates,
            Failures: failures.Distinct().ToArray(),
            DownloadClientName: grabResult is null ? null : decisionPlan.SelectedDownloadClient?.DownloadClientName);
    }

    private static MediaSearchResult BlockedResult(string summary, string reason)
        => new(
            NotFound: false,
            Outcome: "blocked",
            Summary: summary,
            Reason: reason,
            ReleaseName: null,
            IndexerName: null,
            DispatchStatus: null,
            DispatchMessage: null,
            Candidates: [],
            Failures: []);

    private static async Task<IReadOnlyList<Deluno.Quality.Contracts.CustomFormatItem>> ResolveCustomFormatsAsync(
        IQualityRepository repository,
        string? qualityProfileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(qualityProfileId))
        {
            return [];
        }

        var profiles = await repository.ListQualityProfilesAsync(cancellationToken);
        var profile = profiles.FirstOrDefault(item => item.Id == qualityProfileId);
        if (profile is null || string.IsNullOrWhiteSpace(profile.CustomFormatIds))
        {
            return [];
        }

        var ids = profile.CustomFormatIds
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (ids.Length == 0)
        {
            return [];
        }

        var formats = await repository.ListCustomFormatsAsync(cancellationToken);
        return formats.Where(item => ids.Contains(item.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
    }
}
