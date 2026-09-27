using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Data;
using Deluno.Jobs.Decisions;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Platform.Contracts;
using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Quality.ReleasePreferences;
using Deluno.Series.Data;

namespace Deluno.Series.Services;

/// <summary>
/// Runs one season's search: baseline evidence, the acquisition plan, the grab
/// and every record that follows it (#646).
///
/// <para>Two callers need exactly this — the "Search season" button
/// (<c>POST /api/series/{id}/seasons/{n}/search</c>) and the automatic library
/// cycle, once a season is entirely missing and fully aired. Both must pick and
/// grab a candidate the same way a pack is evaluated against every installed
/// episode file the same way, or the two paths would drift the moment one of
/// them changed. This is the one place that does it; the endpoint and the job
/// handler each supply only who asked (<paramref name="triggeredBy"/>) and how
/// that reads in Activity (<paramref name="activityClause"/>).</para>
/// </summary>
/// <summary>Seam for testing: lets a job handler mock the season search without standing up its eleven dependencies.</summary>
public interface ISeasonSearchExecutor
{
    Task<SeasonSearchOutcome> ExecuteAsync(
        string seriesId,
        int seasonNumber,
        string triggeredBy,
        string activityClause,
        CancellationToken cancellationToken);
}

public sealed class SeasonSearchExecutor(
    ISeriesCatalogRepository repository,
    IMediaStateRepository mediaStateRepository,
    ILibrariesRepository librariesRepository,
    IQualityRepository qualityRepository,
    IReleasePreferencePlanRepository releasePreferencePlanRepository,
    IJobQueueRepository jobQueueRepository,
    IAcquisitionDecisionPipeline acquisitionPipeline,
    IDownloadClientGrabService downloadClientGrabService,
    IActivityFeedRepository activityFeedRepository,
    TimeProvider timeProvider,
    IMediaTagStore tagStore) : ISeasonSearchExecutor
{
    public async Task<SeasonSearchOutcome> ExecuteAsync(
        string seriesId,
        int seasonNumber,
        string triggeredBy,
        string activityClause,
        CancellationToken cancellationToken)
    {
        var seriesItem = await repository.GetByIdAsync(seriesId, cancellationToken);
        if (seriesItem is null)
        {
            return SeasonSearchOutcome.SeriesNotFound(seasonNumber);
        }

        var inventory = await repository.GetInventoryDetailAsync(seriesId, cancellationToken);
        if (inventory is null)
        {
            return SeasonSearchOutcome.SeriesNotFound(seasonNumber);
        }

        var seasonEpisodes = inventory.Episodes
            .Where(item => item.SeasonNumber == seasonNumber)
            .OrderBy(item => item.EpisodeNumber)
            .ToList();

        if (seasonEpisodes.Count == 0)
        {
            return SeasonSearchOutcome.SeasonNotFound(seasonNumber);
        }

        var wantedItem = (await mediaStateRepository.ListWantedByIdsAsync(
                MediaKind.Series,
                [seriesId],
                cancellationToken))
            .OrderByDescending(item => item.UpdatedUtc)
            .ThenBy(item => item.LibraryId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (wantedItem is null || string.IsNullOrWhiteSpace(wantedItem.LibraryId))
        {
            return SeasonSearchOutcome.NotSearchable(seasonNumber, seasonEpisodes.Count);
        }

        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var library = libraries.FirstOrDefault(item => item.Id == wantedItem.LibraryId);
        if (library is null)
        {
            return SeasonSearchOutcome.LibraryMissing(seasonNumber, seasonEpisodes.Count);
        }

        var routing = await librariesRepository.GetLibraryRoutingAsync(library.Id, cancellationToken);
        var configuredSources = routing?.Sources.Count ?? 0;
        var configuredClients = routing?.DownloadClients.Count ?? 0;
        var now = timeProvider.GetUtcNow();
        var nextEligibleSearchUtc = now.AddHours(Math.Max(1, library.RetryDelayHours));
        var customFormats = await ResolveCustomFormatsAsync(
            qualityRepository,
            library.QualityProfileId,
            cancellationToken);
        var profileRules = await QualityProfileResolver.ResolveSearchRulesAsync(qualityRepository, library.QualityProfileId, cancellationToken);
        var preferencePlan = await QualityProfileResolver.ResolveReleasePreferencePlanAsync(
            qualityRepository,
            releasePreferencePlanRepository,
            library.QualityProfileId,
            cancellationToken,
            customFormats);
        var tagNames = (await tagStore.ListAsync(MediaKind.Series, seriesItem.Id, cancellationToken))
            .Select(tag => tag.Name)
            .ToArray();

        // A season pack is one candidate, but an installed season has one
        // independently evaluated current file per episode. Load every
        // exact baseline now; a missing or stale snapshot holds before an
        // indexer query, while complete evidence is compared after the
        // search against the same candidate and immutable plan.
        var installedEpisodeIds = await SeriesSearchBaselineResolver.ListInstalledEpisodeIdsAsync(
            repository,
            seasonEpisodes.Select(episode => episode.EpisodeId).ToArray(),
            cancellationToken);
        var installedEpisodes = new List<SeasonPackInstalledEpisode>();
        foreach (var episodeId in installedEpisodeIds)
        {
            var baseline = await SeriesSearchBaselineResolver.ResolveEpisodeAsync(
                repository,
                mediaStateRepository,
                seriesItem.Id,
                episodeId,
                library.Id,
                cancellationToken);
            installedEpisodes.Add(new SeasonPackInstalledEpisode(
                episodeId,
                baseline.FilePath ?? string.Empty,
                baseline.PreferenceEvaluation));
        }
        var missingInstalledEvidence = preferencePlan is null
            ? installedEpisodes
            : installedEpisodes.Where(item =>
                    string.IsNullOrWhiteSpace(item.FilePath) ||
                    item.PreferenceEvaluation is null ||
                    !string.Equals(item.PreferenceEvaluation.PlanId, preferencePlan.Id, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(item.PreferenceEvaluation.PlanVersion, preferencePlan.Version, StringComparison.Ordinal) ||
                    !string.Equals(item.PreferenceEvaluation.PlanHash, preferencePlan.PlanHash, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (missingInstalledEvidence.Count > 0)
        {
            const string holdMessage = "Whole-season replacement was held because at least one installed episode does not have an exact evaluation under the current release-preference plan.";
            foreach (var episode in seasonEpisodes)
            {
                await repository.RecordSearchAttemptAsync(
                    seriesItem.Id,
                    episode.EpisodeId,
                    library.Id,
                    triggeredBy,
                    "held",
                    now,
                    nextEligibleSearchUtc,
                    holdMessage,
                    null,
                    null,
                    JsonSerializer.Serialize(new
                    {
                        episode.EpisodeId,
                        episode.SeasonNumber,
                        episode.EpisodeNumber,
                        installedFilePresent = installedEpisodeIds.Contains(episode.EpisodeId, StringComparer.OrdinalIgnoreCase),
                        currentPlanEvidencePresent = !missingInstalledEvidence.Any(item => string.Equals(item.EpisodeId, episode.EpisodeId, StringComparison.OrdinalIgnoreCase)),
                        preferencePlanId = preferencePlan?.Id,
                        preferencePlanVersion = preferencePlan?.Version,
                        preferencePlanHash = preferencePlan?.PlanHash
                    }),
                    cancellationToken);
            }

            await activityFeedRepository.RecordActivityAsync(
                "series.search.season",
                $"{seriesItem.Title} season {seasonNumber} search was held until installed-file evidence is current.",
                JsonSerializer.Serialize(new
                {
                    seasonNumber,
                    installedEpisodeIds,
                    reason = MediaSearchReasons.SeasonPackInstalledEvidenceMissing,
                    missingEvidenceEpisodeIds = missingInstalledEvidence.Select(item => item.EpisodeId).ToArray()
                }),
                null,
                "series",
                seriesItem.Id,
                cancellationToken);

            await activityFeedRepository.RecordDecisionAsync(
                new DecisionExplanationPayload(
                    Scope: "series.season-search",
                    Status: "held",
                    Reason: holdMessage,
                    Inputs: new Dictionary<string, string?>
                    {
                        ["title"] = seriesItem.Title,
                        ["seasonNumber"] = seasonNumber.ToString(),
                        ["libraryId"] = library.Id,
                        ["episodeCount"] = seasonEpisodes.Count.ToString(),
                        ["installedEpisodeCount"] = installedEpisodeIds.Count.ToString(),
                        ["preferencePlanId"] = preferencePlan?.Id,
                        ["preferencePlanVersion"] = preferencePlan?.Version,
                        ["preferencePlanHash"] = preferencePlan?.PlanHash
                    },
                    Outcome: "No indexer query or download dispatch was made until every installed episode has same-plan evidence.",
                    Alternatives:
                    [
                        new DecisionAlternativeExplanation(
                            "Targeted episode search",
                            "available",
                            "Search selected episodes individually or let the file probe produce the missing current-plan evaluation.")
                    ]),
                null,
                "series",
                seriesItem.Id,
                cancellationToken);

            return SeasonSearchOutcome.EvidenceHeld(seasonNumber, seasonEpisodes.Count, installedEpisodeIds.Count, missingInstalledEvidence.Count, holdMessage);
        }

        if (configuredSources == 0 || configuredClients == 0)
        {
            foreach (var episode in seasonEpisodes)
            {
                await repository.RecordSearchAttemptAsync(
                    seriesItem.Id,
                    episode.EpisodeId,
                    library.Id,
                    triggeredBy,
                    "blocked",
                    now,
                    nextEligibleSearchUtc,
                    configuredSources == 0
                        ? "No indexers are linked to this library yet."
                        : "No download client is linked to this library yet.",
                    null,
                    null,
                    null,
                    cancellationToken);
            }

            return SeasonSearchOutcome.Blocked(
                seasonNumber,
                seasonEpisodes.Count,
                configuredSources == 0 ? MediaSearchReasons.NoIndexers : MediaSearchReasons.NoResults);
        }

        var decisionPlan = await acquisitionPipeline.PlanAsync(
            new AcquisitionDecisionRequest(
                // The planner owns the season suffix and the TV API's
                // dedicated season parameter. Passing a title that already
                // contains "Season 01" produced queries such as
                // "Show Season 01 S01" and made season-pack matching
                // dependent on an accidental duplicate token.
                seriesItem.Title,
                seriesItem.StartYear,
                "tv",
                null,
                wantedItem.TargetQuality,
                routing?.Sources ?? [],
                routing?.DownloadClients ?? [],
                customFormats,
                SeasonNumber: seasonNumber,
                TagNames: tagNames,
                SearchKind: AcquisitionSearchKinds.Interactive,
                AvailableUtc: wantedItem.AvailableUtc,
                NumberingScheme: seriesItem.NumberingScheme,
                PreferencePlan: preferencePlan,
                RuntimeMinutes: seriesItem.RuntimeMinutes,
                ImdbId: seriesItem.ImdbId,
                Genres: seriesItem.Genres).WithProfile(profileRules),
            cancellationToken);
        var searchPlan = decisionPlan.SearchPlan;
        var bestCandidate = searchPlan.BestCandidate;

        // A season-level search does not filter single-episode releases out
        // of its candidates — LeaveEpisodesToTheirOwnSearch skips that
        // filtering whenever SeasonNumber is set, on purpose, so a real pack
        // is scored against them. That means the planner's best answer can
        // still be one episode's release when no pack exists. Grabbing it
        // here used to stand in as "the season's result": every episode of
        // the season was then marked searched, and once that one grab
        // failed, the replacement search queued for it (entity type
        // "series") lifted the retry window for the whole series rather
        // than just the episode it actually covered — restarting this same
        // near-miss in the next season down the list within the minute
        // (#653, #646). A non-pack candidate is treated as no candidate at
        // all: nothing is dispatched, and the season falls back to the
        // ordinary per-episode plan, each episode judged on its own.
        var noPackFound = bestCandidate is not null
            && !SeasonPackDetector.Classify(bestCandidate.ReleaseName).IsSeason;
        if (noPackFound)
        {
            bestCandidate = null;
            searchPlan = searchPlan with { BestCandidate = null };
        }

        SeasonPackReplacementDecision? replacementDecision = null;
        if (!noPackFound && installedEpisodes.Count > 0 && preferencePlan is not null && bestCandidate is not null)
        {
            replacementDecision = SeriesSearchBaselineResolver.EvaluateSeasonPackCandidate(
                preferencePlan,
                bestCandidate,
                installedEpisodes);
        }
        var replacementHeld = replacementDecision is { Authorized: false };

        // The targeted show-level search that runs when a series is added, or
        // any other search that already sent a release for this series, may
        // have dispatched before this job ran — the two race within seconds
        // of each other whenever a season qualifies for its own search
        // (#655). Nothing else here knows that happened, so it is checked
        // here, against the same series id every path dispatches under.
        var onItsWay = !noPackFound && !replacementHeld && decisionPlan.ShouldDispatch && bestCandidate is not null
            ? await DispatchCoverage.AlreadyOnItsWayAsync(jobQueueRepository, bestCandidate.ReleaseName, [seriesItem.Id], cancellationToken)
            : null;

        var outcome = onItsWay is not null ? "skipped" : replacementHeld ? "held" : noPackFound ? "checked" : decisionPlan.Outcome;
        var failures = (searchPlan.Failures ?? []).ToList();
        DownloadClientGrabResult? grabResult = null;
        if (onItsWay is null && !noPackFound && !replacementHeld && decisionPlan.ShouldDispatch && decisionPlan.SelectedDownloadClient is not null && decisionPlan.DispatchRequest is not null)
        {
            var downloadClient = decisionPlan.SelectedDownloadClient;
            grabResult = bestCandidate!.DownloadUrl is null
                ? new DownloadClientGrabResult(downloadClient.DownloadClientId, bestCandidate.ReleaseName, false, "planned", "No download URL was available.")
                {
                    Failure = IntegrationFailureFactory.FromLegacy(
                        "download-client",
                        downloadClient.DownloadClientId,
                        downloadClient.DownloadClientName,
                        "grab",
                        "planned",
                        "No downloadable URL was available for this release.")
                }
                : await downloadClientGrabService.GrabAsync(downloadClient.DownloadClientId, decisionPlan.DispatchRequest, cancellationToken);
            if (grabResult.Failure is { } dispatchFailure)
            {
                failures.Add(dispatchFailure);
            }
            await jobQueueRepository.RecordDownloadDispatchAsync(
                library.Id,
                "tv",
                "series",
                seriesItem.Id,
                bestCandidate.ReleaseName,
                bestCandidate.IndexerName,
                downloadClient.DownloadClientId,
                downloadClient.DownloadClientName,
                grabResult.Status,
                JsonSerializer.Serialize(new
                {
                    seasonNumber,
                    episodeIds = seasonEpisodes.Select(item => item.EpisodeId).ToArray(),
                    searchPlan,
                    replacementComparisons = replacementDecision?.Comparisons,
                    grabResult
                }),
                grabResponseCode: grabResult.Succeeded ? 200 : 400,
                grabFailureCode: grabResult.Failure?.Code ?? grabResult.FailureCode,
                cancellationToken: cancellationToken,
                failure: grabResult.Failure,
                replacementAuthorized: replacementDecision is { Authorized: true, Targets.Count: > 0 },
                replacementTargets: replacementDecision?.Targets,
                clientExternalId: grabResult.ExternalId);

            // A season's release puts the show on its way, as any grab does (#555).
            await mediaStateRepository.MarkDownloadingIfSentAsync(MediaKind.Series, seriesItem.Id, grabResult.Status, now, cancellationToken);
        }

        foreach (var episode in seasonEpisodes)
        {
            await repository.RecordSearchAttemptAsync(
                seriesItem.Id,
                episode.EpisodeId,
                library.Id,
                triggeredBy,
                outcome,
                now,
                nextEligibleSearchUtc,
                onItsWay ?? (replacementHeld ? replacementDecision!.Reason : decisionPlan.SearchResult),
                searchPlan.BestCandidate?.ReleaseName,
                searchPlan.BestCandidate?.IndexerName,
                // The route's `seasonNumber` and `episode.SeasonNumber` differ
                // only by case, and System.Text.Json refuses to bind two such
                // members to one constructor parameter — it threw on every
                // season search, so the endpoint always returned 500 (#285).
                // The per-episode value is the accurate one and the route
                // value was redundant: seasonEpisodes is already filtered to
                // that season.
                searchPlan.Candidates.Count == 0
                    ? JsonSerializer.Serialize(new
                    {
                        episode.EpisodeId,
                        episode.SeasonNumber,
                        episode.EpisodeNumber
                    })
                    : JsonSerializer.Serialize(new
                    {
                        episode.EpisodeId,
                        episode.SeasonNumber,
                        episode.EpisodeNumber,
                        searchPlan,
                        replacementComparisons = replacementDecision?.Comparisons
                    }),
                cancellationToken);
        }

        await activityFeedRepository.RecordActivityAsync(
            "series.search.season",
            $"{seriesItem.Title} season {seasonNumber} {activityClause}",
            JsonSerializer.Serialize(new
            {
                seasonNumber,
                episodeIds = seasonEpisodes.Select(item => item.EpisodeId).ToArray(),
                matched = searchPlan.BestCandidate is not null
            }),
            null,
            "series",
            seriesItem.Id,
            cancellationToken);

        await activityFeedRepository.RecordDecisionAsync(
            new DecisionExplanationPayload(
                Scope: "series.season-search",
                Status: outcome,
                Reason: onItsWay ?? (replacementHeld ? replacementDecision!.Reason : decisionPlan.SearchResult),
                Inputs: new Dictionary<string, string?>
                {
                    ["title"] = seriesItem.Title,
                    ["seasonNumber"] = seasonNumber.ToString(),
                    ["libraryId"] = library.Id,
                    ["episodeCount"] = seasonEpisodes.Count.ToString(),
                    ["sourceCount"] = configuredSources.ToString(),
                    ["downloadClientCount"] = configuredClients.ToString(),
                    ["policyVersion"] = decisionPlan.PolicyVersion
                },
                Outcome: onItsWay is not null
                    ? onItsWay
                    : replacementHeld
                    ? "No dispatch was made because the candidate was not a proven upgrade for every installed episode."
                    : grabResult is null
                    ? searchPlan.Summary
                    : $"{grabResult.Status}: {grabResult.Message}",
                Alternatives: decisionPlan.Alternatives),
            null,
            "series",
            seriesItem.Id,
            cancellationToken);

        var dispatched = searchPlan.BestCandidate is not null && !replacementHeld && onItsWay is null;
        return new SeasonSearchOutcome(
            SeasonSearchStatus.Completed,
            outcome,
            onItsWay is not null
                ? MediaSearchReasons.AlreadyOnItsWay
                : replacementHeld
                ? MediaSearchReasons.SeasonPackCandidateNotUpgradeForEveryEpisode
                : searchPlan.Reason,
            seasonNumber,
            seasonEpisodes.Count,
            installedEpisodes.Count,
            0,
            dispatched ? seasonEpisodes.Count : 0,
            dispatched ? 1 : 0,
            searchPlan.BestCandidate?.ReleaseName,
            searchPlan.BestCandidate?.IndexerName,
            grabResult?.Status,
            replacementHeld ? replacementDecision!.Reason : grabResult?.Message,
            replacementDecision?.Comparisons,
            failures.Distinct().ToArray(),
            seasonEpisodes.Select(item => item.EpisodeId).ToArray(),
            dispatched);
    }

    /// <summary>
    /// The same custom-format resolution the season-search endpoint always
    /// used, kept local rather than reused from the worker's internal
    /// <c>SearchExecutionSupport</c> (that type is deliberately not public
    /// outside <c>Deluno.Worker</c>).
    /// </summary>
    private static async Task<IReadOnlyList<CustomFormatItem>> ResolveCustomFormatsAsync(
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

public enum SeasonSearchStatus
{
    SeriesNotFound,
    SeasonNotFound,
    NotSearchable,
    LibraryMissing,
    EvidenceHeld,
    Blocked,
    Completed
}

/// <summary>
/// What one season's search decided, carrying every field either caller needs:
/// the HTTP endpoint's response body, and the automatic job's decision whether
/// to fall back to per-episode search. Fields that do not apply to a status —
/// an installed-episode count before any episode was inspected, say — read as
/// their default rather than being modelled away, so one shape serves every
/// branch instead of a shape per branch drifting out of step with it.
/// </summary>
public sealed record SeasonSearchOutcome(
    SeasonSearchStatus Status,
    string Outcome,
    string? Reason,
    int SeasonNumber,
    int SearchedEpisodes,
    int InstalledEpisodeCount,
    int MissingEvidenceEpisodeCount,
    int MatchedCount,
    int QueuedCount,
    string? ReleaseName,
    string? IndexerName,
    string? DispatchStatus,
    string? DispatchMessage,
    IReadOnlyList<SeasonPackEpisodeComparison>? ReplacementComparisons,
    IReadOnlyList<IntegrationFailure> Failures,
    IReadOnlyList<string> SeasonEpisodeIds,
    bool Dispatched)
{
    public static SeasonSearchOutcome SeriesNotFound(int seasonNumber)
        => new(SeasonSearchStatus.SeriesNotFound, "not-found", null, seasonNumber, 0, 0, 0, 0, 0, null, null, null, null, null, [], [], false);

    public static SeasonSearchOutcome SeasonNotFound(int seasonNumber)
        => new(SeasonSearchStatus.SeasonNotFound, "not-found", "Deluno could not find that season in the tracked inventory.", seasonNumber, 0, 0, 0, 0, 0, null, null, null, null, null, [], [], false);

    public static SeasonSearchOutcome NotSearchable(int seasonNumber, int searchedEpisodes)
        => new(SeasonSearchStatus.NotSearchable, "blocked", MediaSearchReasons.NotSearchable, seasonNumber, searchedEpisodes, 0, 0, 0, 0, null, null, null, null, null, [], [], false);

    public static SeasonSearchOutcome LibraryMissing(int seasonNumber, int searchedEpisodes)
        => new(SeasonSearchStatus.LibraryMissing, "blocked", MediaSearchReasons.LibraryMissing, seasonNumber, searchedEpisodes, 0, 0, 0, 0, null, null, null, null, null, [], [], false);

    public static SeasonSearchOutcome EvidenceHeld(int seasonNumber, int searchedEpisodes, int installedEpisodeCount, int missingEvidenceEpisodeCount, string holdMessage)
        => new(SeasonSearchStatus.EvidenceHeld, "held", MediaSearchReasons.SeasonPackInstalledEvidenceMissing, seasonNumber, searchedEpisodes, installedEpisodeCount, missingEvidenceEpisodeCount, 0, 0, null, null, null, holdMessage, null, [], [], false);

    public static SeasonSearchOutcome Blocked(int seasonNumber, int searchedEpisodes, string reason)
        => new(SeasonSearchStatus.Blocked, "blocked", reason, seasonNumber, searchedEpisodes, 0, 0, 0, 0, null, null, null, null, null, [], [], false);
}
