using Deluno.Integrations.DownloadClients;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Jobs.Decisions;
using Deluno.Libraries.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Quality.Contracts;
using Deluno.Quality.Guides;
using Deluno.Quality.ReleasePreferences;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Quality;

namespace Deluno.Integrations.Search;

public interface IAcquisitionDecisionPipeline
{
    Task<AcquisitionDecisionPlan> PlanAsync(
        AcquisitionDecisionRequest request,
        CancellationToken cancellationToken = default);

    AcquisitionSelectedReleaseDecision EvaluateSelectedRelease(AcquisitionSelectedReleaseRequest request);

    /// <summary>
    /// The routed client a release a person picked by hand goes to: the first by
    /// priority that can take what its source offers. <c>null</c> when the
    /// library has no client of that kind, which the caller says out loud
    /// rather than sending a torrent to a Usenet client.
    /// </summary>
    Task<LibraryDownloadClientLinkItem?> ChooseDownloadClientAsync(
        string? indexerId,
        IReadOnlyList<LibraryDownloadClientLinkItem> routedClients,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? titleTags = null);

    /// <summary>
    /// Why <see cref="ChooseDownloadClientAsync"/> found nothing, when the reason is that every routed client is
    /// switched off; null for any other reason (no client of the right kind, say), which the caller words itself.
    /// </summary>
    Task<string?> ExplainNoDownloadClientAsync(
        string? indexerId,
        IReadOnlyList<LibraryDownloadClientLinkItem> routedClients,
        CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// The plain-English reason a title's tags sent it to this client rather
    /// than another one routed to the same library (#808) — null when the
    /// client is untagged, because an untagged client takes any title and
    /// there is nothing about tags to explain.
    /// </summary>
    Task<string?> ExplainDownloadClientRoutingAsync(
        string downloadClientId,
        IReadOnlyList<string>? titleTags,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The plain-English reason a title's tags let (or would let) an indexer
    /// be searched for it (#990) — null when the indexer is untagged, since
    /// an untagged indexer is searched for every title and there is nothing
    /// about tags to explain.
    /// </summary>
    Task<string?> ExplainIndexerRoutingAsync(
        string indexerId,
        IReadOnlyList<string>? titleTags,
        CancellationToken cancellationToken = default);
}

public sealed class AcquisitionDecisionPipeline : IAcquisitionDecisionPipeline
{
    private static readonly IReleaseRankingModelService DisabledRankingModelService = new DisabledReleaseRankingService();
    private readonly IMediaSearchPlanner mediaSearchPlanner;
    private readonly IReleaseRankingModelService rankingModelService;
    private readonly IIntelligentRoutingService? intelligentRoutingService;
    private readonly IConnectionsRepository? connectionsRepository;
    private readonly IBlockedReleaseRepository? blockedReleases;
    private readonly ITurnedDownReleaseRecorder? turnedDownRecorder;
    private readonly IRealtimeEventPublisher? realtimeEventPublisher;
    private readonly TimeProvider timeProvider;

    public AcquisitionDecisionPipeline(
        IMediaSearchPlanner mediaSearchPlanner,
        IReleaseRankingModelService? rankingModelService = null,
        IIntelligentRoutingService? intelligentRoutingService = null,
        IConnectionsRepository? connectionsRepository = null,
        IBlockedReleaseRepository? blockedReleases = null,
        ITurnedDownReleaseRecorder? turnedDownRecorder = null,
        IRealtimeEventPublisher? realtimeEventPublisher = null,
        TimeProvider? timeProvider = null)
    {
        this.mediaSearchPlanner = mediaSearchPlanner;
        this.rankingModelService = rankingModelService ?? DisabledRankingModelService;
        this.intelligentRoutingService = intelligentRoutingService;
        this.connectionsRepository = connectionsRepository;
        this.blockedReleases = blockedReleases;
        this.turnedDownRecorder = turnedDownRecorder;
        this.realtimeEventPublisher = realtimeEventPublisher;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AcquisitionDecisionPlan> PlanAsync(
        AcquisitionDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var readyConnections = await ResolveReadyConnectionsAsync(request, cancellationToken);
        var sourceCount = readyConnections.Sources.Count;
        var clientCount = readyConnections.DownloadClients.Count;
        var currentCustomFormatScore = request.CurrentCustomFormatScore;
        if (currentCustomFormatScore is null && !string.IsNullOrWhiteSpace(request.CurrentReleaseName))
        {
            currentCustomFormatScore = CustomFormatMatcher.EvaluateUpgradeScore(
                request.CurrentReleaseName,
                request.CustomFormats,
                out _);
        }

        var searchPlan = sourceCount == 0 || clientCount == 0
            ? new MediaSearchPlan(
                BestCandidate: null,
                Candidates: [],
                Summary: sourceCount == 0
                    ? request.Sources.Count == 0
                        ? "No indexers are linked to this library yet."
                        : readyConnections.LibraryTagSkips is { Count: > 0 } && readyConnections.Sources.Count == 0
                            && request.Sources.Count == readyConnections.LibraryTagSkips.Count
                            ? string.Join(" ", readyConnections.LibraryTagSkips)
                        : readyConnections.NoClientForAnySource
                            ? "None of this library's download clients can take what its search sources offer. Link a torrent client for torrent sources, or a Usenet client for Usenet sources, in Library Routing."
                            : "No linked search source is ready. Test a source successfully before Deluno searches or dispatches releases."
                    : request.DownloadClients.Count == 0
                        ? "No download client is linked to this library yet."
                        : "No linked download client is ready. Test a client successfully before Deluno dispatches releases.")
            : await mediaSearchPlanner.BuildPlanAsync(
                request.Title,
                request.Year,
                request.MediaType,
                request.CurrentQuality,
                request.TargetQuality,
                readyConnections.Sources,
                request.CustomFormats,
                request.SeasonNumber,
                request.EpisodeNumber,
                request.AllowedQualities,
                cancellationToken,
                request.TagNames,
                request.SearchKind,
                request.AvailableUtc,
                currentCustomFormatScore,
                request.CurrentReleaseName,
                request.UpgradeUntilCutoff,
                request.NumberingScheme,
                request.AbsoluteNumber,
                request.AirDate,
                request.SceneSeasonNumber,
                request.SceneEpisodeNumber,
                WithInstalledSizeFloor(request.CurrentPreferenceEvaluation, request.CurrentQuality, request.SizeRules, request.MediaType, request.EpisodeNumber is not null || request.AbsoluteNumber is not null || request.AirDate is not null),
                request.PreferencePlan,
                request.CurrentFilePresent,
                request.SizeRules,
                request.UpgradeStop,
                request.ProfileAcquisition,
                request.RuntimeMinutes,
                request.ImdbId,
                request.ScoreLimits,
                request.Genres,
                request.OtherTitles,
                episodeTitles: request.EpisodeTitles,
                episodeTitlesBySeason: request.EpisodeTitlesBySeason,
                patientIndexerIds: request.PatientIndexerIds);

        if (sourceCount > 0 && readyConnections.LibraryTagSkips is { Count: > 0 } tagSkips)
        {
            searchPlan = searchPlan with { Summary = $"{searchPlan.Summary} {string.Join(" ", tagSkips)}" };
        }

        var beforeBlocklist = searchPlan;
        searchPlan = await SkipBlockedReleasesAsync(searchPlan, cancellationToken);
        if (!request.PreviewOnly)
        {
            await NoteTurnedDownAsync(request, beforeBlocklist, searchPlan, cancellationToken);
        }

        searchPlan = LeaveEpisodesToTheirOwnSearch(searchPlan, request);
        searchPlan = LeavePacksThatWouldRestoreRemovedEpisodes(searchPlan, request);
        searchPlan = NeverNameARejectedCandidateBest(searchPlan);

        var bestCandidate = searchPlan.BestCandidate;
        var wouldDispatch = sourceCount > 0 && clientCount > 0 && bestCandidate is not null
            && IsSafeForAutomaticDispatch(bestCandidate) && !request.PreviewOnly;
        // The client is chosen for the release that won, not before the search:
        // a torrent goes to a torrent client and an NZB to a Usenet one.
        var (selectedClient, clientChoiceReason) = await ChooseDownloadClientAsync(
            readyConnections.ClientsFor(bestCandidate?.IndexerId, request.TagNames),
            wouldDispatch,
            // The client's own priority, set on its drawer; the routing link only records the order it was ticked in.
            client => readyConnections.DownloadClientDetails.TryGetValue(client.DownloadClientId, out var detail) ? detail.Priority : client.Priority,
            cancellationToken);
        var outcome = sourceCount == 0 || clientCount == 0
            ? "blocked"
            : bestCandidate is null
                ? "checked"
                : IsSafeForAutomaticDispatch(bestCandidate)
                    ? "matched"
                    : "held";

        return new AcquisitionDecisionPlan(
            SearchPlan: searchPlan,
            PolicyVersion: bestCandidate?.PolicyVersion ?? Deluno.Quality.MediaPolicyCatalog.CurrentVersion,
            Outcome: outcome,
            SearchResult: BuildSearchResult(searchPlan, clientCount),
            SourceCount: sourceCount,
            DownloadClientCount: clientCount,
            SelectedDownloadClient: selectedClient,
            ClientChoiceReason: clientChoiceReason,
            ShouldDispatch: outcome == "matched" && !request.PreviewOnly,
            DispatchRequest: bestCandidate is null || selectedClient is null
                ? null
                : BuildGrabRequest(
                    bestCandidate,
                    request.MediaType,
                    DispatchCategory(request.MediaType, selectedClient, readyConnections.DownloadClientDetails),
                    selectedClient),
            Alternatives: BuildDecisionAlternatives(searchPlan));
    }

    /// <summary>
    /// The releases this search looked at and did not take, as the groups the Live screen shows (#1098): each
    /// once, by the first thing that turned it down. Written and pushed once per search; a search that took
    /// everything it saw writes nothing. Never lets a failure here undo or fail the search itself.
    /// </summary>
    private async Task NoteTurnedDownAsync(
        AcquisitionDecisionRequest request,
        MediaSearchPlan beforeBlocklist,
        MediaSearchPlan afterBlocklist,
        CancellationToken cancellationToken)
    {
        if (turnedDownRecorder is null || beforeBlocklist.Candidates.Count == 0)
        {
            return;
        }

        var turnedDown = TurnedDownFrom(beforeBlocklist, afterBlocklist);
        if (turnedDown.Count == 0)
        {
            return;
        }

        var entityType = string.Equals(request.MediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "series" : "movie";
        IReadOnlySet<string>? newToday = null;
        try
        {
            newToday = await turnedDownRecorder.RecordAsync(request.Title, entityType, request.EntityId, turnedDown, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The search is already done; a missed count is not worth failing it.
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = turnedDown
            .Where(release => seen.Add(release.Release))
            .Select(release => new TurnedDownItemEvent(release.Release, release.Group, TurnedDownGroups.Label(release.Group), newToday?.Contains(release.Release) ?? true))
            .ToArray();
        await realtimeEventPublisher.PublishSafelyAsync(publisher => publisher.PublishReleasesTurnedDownAsync(
            new ReleasesTurnedDownEvent(
                request.Title,
                entityType,
                request.EntityId,
                timeProvider.GetUtcNow().UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                items),
            cancellationToken));
    }

    /// <summary>The turned-down releases of one search: those a person had blocked, then those the rules refused or found no better.</summary>
    public static IReadOnlyList<TurnedDownRelease> TurnedDownFrom(MediaSearchPlan beforeBlocklist, MediaSearchPlan afterBlocklist)
    {
        var turnedDown = new List<TurnedDownRelease>();
        var kept = new HashSet<MediaSearchCandidate>(afterBlocklist.Candidates, ReferenceEqualityComparer.Instance);
        foreach (var candidate in beforeBlocklist.Candidates)
        {
            if (!kept.Contains(candidate))
            {
                turnedDown.Add(new TurnedDownRelease(
                    candidate.ReleaseName,
                    TurnedDownReasons.GroupFor(TurnedDownReasons.BlocklistedReason),
                    TurnedDownReasons.BlocklistedReason,
                    candidate.IndexerName));
            }
        }

        foreach (var candidate in afterBlocklist.Candidates)
        {
            if (!TurnedDownReasons.IsTurnedDown(candidate.DecisionStatus))
            {
                continue;
            }

            var raw = TurnedDownReasons.FirstRejectingReason(candidate.RejectionGate, candidate.DecisionReasons, candidate.RiskFlags);
            turnedDown.Add(new TurnedDownRelease(
                candidate.ReleaseName,
                TurnedDownReasons.GroupFor(raw, candidate.DecisionStatus),
                raw,
                candidate.IndexerName));
        }

        return turnedDown;
    }

    public async Task<LibraryDownloadClientLinkItem?> ChooseDownloadClientAsync(
        string? indexerId,
        IReadOnlyList<LibraryDownloadClientLinkItem> routedClients,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? titleTags = null)
    {
        var byPriority = routedClients.OrderBy(client => client.Priority).ToArray();
        if (connectionsRepository is null)
        {
            return byPriority.FirstOrDefault();
        }

        var clients = (await connectionsRepository.ListDownloadClientsAsync(cancellationToken))
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);

        // A client that is switched off takes nothing: a send to it was held back and never sent again
        // (golden path rc.30.9). Only enabled clients are considered.
        byPriority = byPriority
            .Where(client => !clients.TryGetValue(client.DownloadClientId, out var detail) || detail.IsEnabled)
            .ToArray();

        string? TagsOf(LibraryDownloadClientLinkItem client)
            => clients.TryGetValue(client.DownloadClientId, out var detail) ? detail.Tags : null;

        // A client tagged for this title wins over untagged ones (#1041);
        // priority orders within each group.
        var source = string.IsNullOrWhiteSpace(indexerId)
            ? null
            : (await connectionsRepository.ListIndexersAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, indexerId, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            return DownloadClientTagMatch.PreferTagged(
                    byPriority.Where(client => !clients.ContainsKey(client.DownloadClientId)
                        || DownloadClientTagMatch.IsEligible(TagsOf(client), titleTags)),
                    TagsOf,
                    titleTags)
                .FirstOrDefault();
        }

        var kind = DownloadProtocols.SourceKind(source.Protocol);
        return DownloadClientTagMatch.PreferTagged(
                byPriority.Where(client =>
                    clients.TryGetValue(client.DownloadClientId, out var detail)
                    && string.Equals(DownloadProtocols.ClientKind(detail.Protocol), kind, StringComparison.OrdinalIgnoreCase)
                    && DownloadClientTagMatch.IsEligible(detail.Tags, titleTags)),
                TagsOf,
                titleTags)
            .FirstOrDefault();
    }

    public async Task<string?> ExplainNoDownloadClientAsync(
        string? indexerId,
        IReadOnlyList<LibraryDownloadClientLinkItem> routedClients,
        CancellationToken cancellationToken = default)
    {
        if (connectionsRepository is null || routedClients.Count == 0)
        {
            return null;
        }

        var clients = (await connectionsRepository.ListDownloadClientsAsync(cancellationToken))
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var routed = routedClients.Where(client => clients.ContainsKey(client.DownloadClientId)).ToArray();
        if (routed.Length == 0 || routed.Any(client => clients[client.DownloadClientId].IsEnabled))
        {
            return null;
        }

        var names = string.Join(", ", routed.Select(client => clients[client.DownloadClientId].Name));
        return routed.Length == 1
            ? $"{names} is switched off, so nothing can be sent to it. Switch it on in Download clients, or route another client to this library."
            : $"Every download client for this library is switched off ({names}), so nothing can be sent. Switch one on in Download clients.";
    }

    public async Task<string?> ExplainDownloadClientRoutingAsync(
        string downloadClientId,
        IReadOnlyList<string>? titleTags,
        CancellationToken cancellationToken = default)
    {
        if (connectionsRepository is null)
        {
            return null;
        }

        var client = (await connectionsRepository.ListDownloadClientsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, downloadClientId, StringComparison.OrdinalIgnoreCase));
        return client is null ? null : DownloadClientTagMatch.Explain(client.Tags, titleTags, client.Name);
    }

    public async Task<string?> ExplainIndexerRoutingAsync(
        string indexerId,
        IReadOnlyList<string>? titleTags,
        CancellationToken cancellationToken = default)
    {
        if (connectionsRepository is null)
        {
            return null;
        }

        var indexer = (await connectionsRepository.ListIndexersAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, indexerId, StringComparison.OrdinalIgnoreCase));
        return indexer is null ? null : DownloadClientTagMatch.Explain(indexer.Tags, titleTags, indexer.Name);
    }

    public AcquisitionSelectedReleaseDecision EvaluateSelectedRelease(AcquisitionSelectedReleaseRequest request)
    {
        // The same reading a search gives it; never a guess of WEB 1080p (#610).
        var quality = request.CandidateQuality ?? Deluno.Quality.ReleaseNameQuality.Read(request.ReleaseName);
        // A selected release must be judged by the same immutable profile plan
        // as automatic search.  Manual selection is still an acquisition path,
        // not a licence to silently recompile a migrated profile against the
        // current guide package.
        var preferencePlan = request.PreferencePlan ?? ReleasePreferencePlanFactory.CreateQualityPlan(
            request.MediaType,
            request.TargetQuality,
            upgradeUntilCutoff: true,
            customFormats: request.CustomFormats,
            guidePackage: request.GuidePackage);
        var matchedCustomFormats = CustomFormatMatcher.EvaluateMatches(
            request.ReleaseName,
            request.CustomFormats);
        var decision = ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            request.ReleaseName,
            quality,
            request.CurrentQuality,
            request.TargetQuality,
            request.SizeBytes,
            request.Seeders,
            request.DownloadUrl,
            SourcePriorityScore: request.SourcePriorityScore ?? 0,
            CustomFormatScore: 0,
            request.NeverGrabPatterns,
            SizeRules: request.SizeRules,
            UpgradeStop: request.UpgradeStop,
            PreferencePlan: preferencePlan,
            CurrentReleaseName: request.CurrentReleaseName,
            CurrentPreferenceEvaluation: WithInstalledSizeFloor(request.CurrentPreferenceEvaluation, request.CurrentQuality, request.SizeRules, request.MediaType, episodeScope: false),
            CurrentFilePresent: request.CurrentFilePresent,
            MediaType: request.MediaType,
            RuntimeMinutes: request.RuntimeMinutes,
            ScoreLimits: request.ScoreLimits));
        var boost = rankingModelService.Score(new ReleaseRankingFeatures(
            Seeders: request.Seeders,
            SizeBytes: request.SizeBytes,
            QualityDelta: decision.QualityDelta,
            CustomFormatScore: decision.CustomFormatScore,
            SourcePriorityScore: request.SourcePriorityScore ?? 0,
            EstimatedBitrateMbps: decision.EstimatedBitrateMbps,
            ReleaseAgeHours: null), hardBlocked: decision.Status == "rejected");
        var scoreComputation = ReleaseScoringModePolicy.Compute(decision.Score, boost, request.ScoringMode);
        var boostedScore = preferencePlan is null ? scoreComputation.FinalScore : 0;
        // How the score was combined (rules, ranking model) is for the logs; a person is told why, in plain words (#1159).
        var boostedSummary = decision.Summary;
        var boostedReasons = decision.Reasons;

        var candidate = new MediaSearchCandidate(
            ReleaseName: request.ReleaseName,
            IndexerId: request.IndexerId ?? "manual",
            IndexerName: string.IsNullOrWhiteSpace(request.IndexerName) ? "Manual selection" : request.IndexerName,
            Quality: quality,
            Score: boostedScore,
            MeetsCutoff: decision.MeetsCutoff,
            Summary: boostedSummary,
            DownloadUrl: request.DownloadUrl,
            SizeBytes: request.SizeBytes,
            Seeders: request.Seeders,
            DecisionStatus: decision.Status,
            DecisionReasons: boostedReasons,
            RiskFlags: decision.RiskFlags,
            QualityDelta: decision.QualityDelta,
            CustomFormatScore: decision.CustomFormatScore,
            SeederScore: decision.SeederScore,
            SizeScore: decision.SizeScore,
            ReleaseGroup: decision.ReleaseGroup,
            EstimatedBitrateMbps: decision.EstimatedBitrateMbps,
            PolicyVersion: decision.PolicyVersion,
            MatchedCustomFormats: matchedCustomFormats,
            PreferenceEvaluation: decision.PreferenceEvaluation,
            PreferenceComparison: decision.PreferenceComparison);

        var safe = IsSafeForAutomaticDispatch(candidate);

        // Replacement protection is a hard block — cannot be bypassed with force override.
        // A user who wants to downgrade must explicitly disable protection on the movie/series first.
        var replacementBlocked =
            request.PreventLowerQualityReplacements &&
            !string.IsNullOrWhiteSpace(request.CurrentQuality) &&
            candidate.QualityDelta < 0;

        var canDispatch = !replacementBlocked && (safe || request.ForceOverride);
        var reason = replacementBlocked
            ? $"Replacement protection is enabled. {candidate.Quality} is lower quality than your current file ({request.CurrentQuality}). " +
              "Disable replacement protection on this item to allow downgrades."
            : safe
                ? candidate.Summary
                : request.ForceOverride
                    ? $"User override accepted {candidate.ReleaseName}: {request.OverrideReason ?? "No override reason supplied."}"
                    : $"Release requires force override because Deluno classified it as {ReleaseDecisionStatuses.Describe(candidate.DecisionStatus)}.";

        return new AcquisitionSelectedReleaseDecision(
            Candidate: candidate,
            PolicyVersion: decision.PolicyVersion,
            CanDispatch: canDispatch,
            RequiresOverride: !safe && !replacementBlocked,
            Reason: reason,
            Alternatives: BuildDecisionAlternatives(new MediaSearchPlan(candidate, [candidate], candidate.Summary)));
    }

    /// <summary>
    /// Drops candidates Deluno has already decided not to use, and says how
    /// many.
    ///
    /// <para>The saying is the point. A silent skip is the behaviour James
    /// objected to in Radarr: a title stops arriving and nothing accounts for
    /// it. Worse, once every candidate has been blocked, a silent skip becomes
    /// "no results found" — which is a lie about the indexers.</para>
    ///
    /// <para>Applied after the plan is built rather than inside the planner.
    /// By then the candidates are ordered and de-duplicated and the best is
    /// simply the first of them, so filtering here and taking the first
    /// survivor reuses every tie-break rather than repeating any of it.</para>
    /// </summary>
    private async Task<MediaSearchPlan> SkipBlockedReleasesAsync(
        MediaSearchPlan plan,
        CancellationToken cancellationToken)
    {
        if (blockedReleases is null || plan.Candidates.Count == 0)
        {
            return plan;
        }

        var blockedKeys = await blockedReleases.ListKeysAsync(cancellationToken);
        return ApplyBlocklist(plan, blockedKeys);
    }

    /// <summary>
    /// The decision itself, with nothing to fetch — separated so it can be
    /// tested against a set of keys rather than against a planner with twenty
    /// arguments and a database behind it.
    /// </summary>
    public static MediaSearchPlan ApplyBlocklist(MediaSearchPlan plan, IReadOnlySet<string> blockedKeys)
    {
        if (blockedKeys.Count == 0 || plan.Candidates.Count == 0)
        {
            return plan;
        }

        // Hash/GUID first, whatever indexer or name it shows up under this
        // time (#802, #874); then the name from any indexer, for every
        // refusal reason (#585, #874). A candidate with no known hash yet
        // only has the name-based keys to check.
        var kept = plan.Candidates
            .Where(candidate =>
                !(!string.IsNullOrWhiteSpace(candidate.TorrentHashOrItemId)
                    && blockedKeys.Contains(BlockedReleaseKeys.ForHash(candidate.TorrentHashOrItemId)))
                && !blockedKeys.Contains(BlockedReleaseKeys.For(candidate.ReleaseName, candidate.IndexerName))
                && !blockedKeys.Contains(BlockedReleaseKeys.ForEveryIndexer(candidate.ReleaseName)))
            .ToArray();

        var skipped = plan.Candidates.Count - kept.Length;
        if (skipped == 0)
        {
            return plan;
        }

        var best = kept.FirstOrDefault();
        var note = skipped == 1
            ? "Skipped 1 release you have blocked."
            : $"Skipped {skipped} releases you have blocked.";

        return plan with
        {
            BestCandidate = best,
            Candidates = kept,
            // When everything was blocked the old summary described a best
            // candidate that no longer exists, and "no usable release" alone
            // would blame the indexers for a decision Deluno made.
            Summary = best is null
                ? $"{note} Nothing else was offered."
                : $"{SummaryNaming(plan, best)} {note}",
            Reason = best is null ? MediaSearchReasons.NoUsableRelease : plan.Reason
        };
    }

    /// <summary>
    /// A season pack is never taken while an episode of its season has been removed or unmonitored: it would
    /// download that episode again (#1072). A person choosing from the preview list still sees everything.
    /// </summary>
    public static MediaSearchPlan LeavePacksThatWouldRestoreRemovedEpisodes(MediaSearchPlan plan, AcquisitionDecisionRequest request)
    {
        if (request.PreviewOnly
            || request.SeasonsWithRemovedEpisodes is not { Count: > 0 } removed
            || !string.Equals(request.MediaType, "tv", StringComparison.OrdinalIgnoreCase)
            || plan.Candidates.Count == 0)
        {
            return plan;
        }

        var kept = plan.Candidates
            .Where(candidate =>
            {
                var classification = SeasonPackDetector.Classify(candidate.ReleaseName);
                return !(classification.IsSeason && (classification.Season is { } season ? removed.Contains(season) : true));
            })
            .ToArray();
        if (kept.Length == plan.Candidates.Count)
        {
            return plan;
        }

        var best = kept.FirstOrDefault();
        return plan with
        {
            BestCandidate = best,
            Candidates = kept,
            Summary = best is null
                ? "Only a season pack was offered, and it would download episodes that were unmonitored or removed. New episodes are fetched one at a time."
                : SummaryNaming(plan, best),
            Reason = best is null ? MediaSearchReasons.NoUsableRelease : plan.Reason
        };
    }

    /// <summary>
    /// A search for a whole show does not take one episode (#593).
    ///
    /// <para>A show-level query names no season or episode, so an indexer answers
    /// with whatever it has, single episodes included, and the plan weighed each
    /// against the show's quality. On the soak rig Breaking Bad held two episodes
    /// at WEB 1080p, so a show search held a missing episode as "equivalent to the
    /// installed file" and asked for none of the 67 that were missing. A single
    /// episode is the episode search's to judge, against that episode's own file
    /// (#591). Season packs and releases that name no episode stay here. A person
    /// choosing from the preview list still sees everything.</para>
    /// </summary>
    public static MediaSearchPlan LeaveEpisodesToTheirOwnSearch(MediaSearchPlan plan, AcquisitionDecisionRequest request)
    {
        if (request.PreviewOnly
            || !string.Equals(request.MediaType, "tv", StringComparison.OrdinalIgnoreCase)
            || request.SeasonNumber is not null
            || request.EpisodeNumber is not null
            || request.AbsoluteNumber is not null
            || request.AirDate is not null
            || plan.Candidates.Count == 0)
        {
            return plan;
        }

        var kept = plan.Candidates
            .Where(candidate => !SeasonPackDetector.Classify(candidate.ReleaseName).IsEpisode)
            .ToArray();
        if (kept.Length == plan.Candidates.Count)
        {
            return plan;
        }

        var best = kept.FirstOrDefault();
        return plan with
        {
            BestCandidate = best,
            Candidates = kept,
            Summary = best is null
                ? "Only single episodes were offered. Each missing episode is searched for on its own."
                : SummaryNaming(plan, best),
            Reason = best is null ? MediaSearchReasons.NoUsableRelease : plan.Reason
        };
    }

    /// <summary>
    /// A held file below its tier's size floor fails the profile (#597).
    ///
    /// <para>The floor was only ever applied to releases on offer. Cleaner was
    /// held as a WEB 1080p below the profile's WEB 1080p floor, so a release
    /// that size would have been turned down, yet the file itself counted as
    /// meeting every target and an 18 GB Bluray was held as "equivalent". The
    /// evaluator already replaces a file that fails a hard gate; the floor
    /// just never reached it.</para>
    ///
    /// <para>Only the profile's own rule counts here. A tier the profile says
    /// nothing about has no floor for a file already in the library.</para>
    /// </summary>
    /// <param name="episodeScope">True when the search is for one episode, whose file is one episode (#600).</param>
    public static PreferenceEvaluationSnapshot? WithInstalledSizeFloor(
        PreferenceEvaluationSnapshot? current,
        string? currentQuality,
        IReadOnlyList<ProfileSizeRule>? sizeRules,
        string? mediaType,
        bool episodeScope)
    {
        var tv = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase);
        // A show or a season has no one file to measure.
        if (current is not { FileSizeBytes: > 0 } || !current.Evaluation.HardGatesPassed || (tv && !episodeScope)
            || ReleaseDecisionEngine.SizeBand(currentQuality, sizeRules, episode: tv, typicalWhenUnset: false) is not { MinBytes: > 0 } band
            || current.FileSizeBytes.Value >= band.MinBytes)
        {
            return current;
        }

        var violation = $"Your file is {ReleaseDecisionEngine.DescribeSize(current.FileSizeBytes.Value, band.Episode)}, below the {ReleaseDecisionEngine.DescribeSize(band.MinBytes, band.Episode)} minimum for {currentQuality}.";
        return current with
        {
            Evaluation = current.Evaluation with
            {
                Status = PreferenceEvaluationStatus.BelowGoal,
                HardGatesPassed = false,
                Reasons = [.. current.Evaluation.Reasons, violation],
                InstalledFileViolation = violation
            }
        };
    }

    public static bool IsSafeForAutomaticDispatch(MediaSearchCandidate candidate)
        => string.Equals(candidate.DecisionStatus, ReleaseDecisionStatuses.Preferred, StringComparison.OrdinalIgnoreCase) &&
           candidate.MeetsCutoff &&
           candidate.QualityDelta >= 0;

    /// <summary>
    /// A release the engine refused is never the plan's best candidate (#1159). A different show's episode was
    /// "the best candidate", which made the title "held for manual review" and not searched again for hours, when
    /// nothing was waiting for anybody. The first usable candidate wins; with none, the plan says so plainly and
    /// leads with the real reasons. Every candidate stays listed, so a person choosing by hand still sees them.
    /// </summary>
    /// <summary>
    /// The plan's summary names its best candidate. When a later step drops that release (blocked, a pack, another
    /// episode) the summary must follow the new best, or it names a release that is not in the list (#1159).
    /// </summary>
    internal static string SummaryNaming(MediaSearchPlan plan, MediaSearchCandidate best)
        => plan.BestCandidate is { } was
            && string.Equals(was.ReleaseName, best.ReleaseName, StringComparison.Ordinal)
            && string.Equals(was.IndexerName, best.IndexerName, StringComparison.Ordinal)
            ? plan.Summary
            : $"Best feed candidate is {best.ReleaseName} from {best.IndexerName}.";

    public static MediaSearchPlan NeverNameARejectedCandidateBest(MediaSearchPlan plan)
    {
        static bool Refused(MediaSearchCandidate candidate)
            => string.Equals(candidate.DecisionStatus, ReleaseDecisionStatuses.Rejected, StringComparison.OrdinalIgnoreCase);

        if (plan.BestCandidate is null || !Refused(plan.BestCandidate))
        {
            return plan;
        }

        var usable = plan.Candidates.FirstOrDefault(candidate => !Refused(candidate));
        if (usable is not null)
        {
            return plan with
            {
                BestCandidate = usable,
                Summary = $"Best feed candidate is {usable.ReleaseName} from {usable.IndexerName}."
            };
        }

        return plan with
        {
            BestCandidate = null,
            Summary = NoUsableReleaseSummary(plan.Candidates),
            Reason = MediaSearchReasons.NoUsableRelease
        };
    }

    /// <summary>"No usable release yet: 9 were cams or telesyncs, 3 had nobody seeding" (#1159).</summary>
    public static string NoUsableReleaseSummary(IReadOnlyList<MediaSearchCandidate> candidates)
    {
        var counts = candidates
            .GroupBy(candidate => PlainRefusal(TurnedDownReasons.FirstRejectingReason(candidate.RejectionGate, candidate.DecisionReasons, candidate.RiskFlags)))
            .Select(group => (Phrase: group.Key, Count: group.Count()))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Phrase, StringComparer.Ordinal)
            .ToArray();
        if (counts.Length == 0)
        {
            return "No usable release yet: nothing was offered.";
        }

        var shown = counts.Take(3).Select(item => $"{item.Count} {(item.Count == 1 && item.Phrase.StartsWith("were ", StringComparison.Ordinal) ? "was " + item.Phrase[5..] : item.Phrase)}");
        var more = counts.Length > 3 ? $", and {counts.Skip(3).Sum(item => item.Count)} for other reasons" : string.Empty;
        return $"No usable release yet: {string.Join(", ", shown)}{more}.";
    }

    private static string PlainRefusal(string? reason)
    {
        if (!string.IsNullOrWhiteSpace(reason))
        {
            if (reason.Contains("blocked token", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("looks like a sample", StringComparison.OrdinalIgnoreCase))
            {
                return "were cams, telesyncs or samples";
            }

            if (reason.Contains("for something else", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("for another show", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("but its title is", StringComparison.OrdinalIgnoreCase))
            {
                return "were for a different title";
            }

            if (reason.Contains("the search was for", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("does not say which episode", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("whole-season pack", StringComparison.OrdinalIgnoreCase))
            {
                return "were for a different episode";
            }
        }

        return TurnedDownReasons.GroupFor(reason) switch
        {
            TurnedDownGroups.Language => "were not in a language you want",
            TurnedDownGroups.NotBetter => "were no better than what you have",
            TurnedDownGroups.TooBig => "were over your size limit",
            TurnedDownGroups.FewSeeders => "had nobody seeding",
            TurnedDownGroups.Fake => "looked fake or mislabelled",
            TurnedDownGroups.NotQuality => "were not a quality you want",
            TurnedDownGroups.TooSmall => "were too small for their quality",
            TurnedDownGroups.Rules => "were blocked by your release rules",
            TurnedDownGroups.TooOld => "were older than your indexer keeps",
            TurnedDownGroups.Blocklisted => "were on your blocklist",
            _ => "were turned down for other reasons"
        };
    }

    public static string BuildSearchResult(MediaSearchPlan plan, int configuredClients)
    {
        if (plan.BestCandidate is null)
        {
            return plan.Summary;
        }

        if (!IsSafeForAutomaticDispatch(plan.BestCandidate))
        {
            return $"{plan.Summary} Held for manual review because the best candidate is {ReleaseDecisionStatuses.Describe(plan.BestCandidate.DecisionStatus)}.";
        }

        return $"{plan.Summary} Ready to send to {configuredClients} download client{(configuredClients == 1 ? "" : "s")}.";
    }

    private static DownloadClientGrabRequest BuildGrabRequest(
        MediaSearchCandidate candidate,
        string mediaType,
        string category,
        LibraryDownloadClientLinkItem downloadClient)
        => new(
            candidate.ReleaseName,
            candidate.DownloadUrl ?? string.Empty,
            NormalizeMediaType(mediaType),
            category,
            candidate.IndexerName)
        {
            // The hash the indexer's own listing named, kept for the grab
            // service to check against the release itself (#1062).
            ReleaseHash = candidate.TorrentHashOrItemId
        };

    private static string NormalizeMediaType(string mediaType)
        => string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(mediaType, "series", StringComparison.OrdinalIgnoreCase)
            ? "tv"
            : "movies";

    private static string DispatchCategory(
        string mediaType,
        LibraryDownloadClientLinkItem route,
        IReadOnlyDictionary<string, DownloadClientItem> downloadClientDetails)
    {
        if (!string.IsNullOrWhiteSpace(route.Category))
        {
            return route.Category.Trim();
        }

        if (downloadClientDetails.TryGetValue(route.DownloadClientId, out var client))
        {
            var configuredCategory = NormalizeCategory(
                NormalizeMediaType(mediaType) == "tv" ? client.TvCategory : client.MoviesCategory)
                ?? NormalizeCategory(client.CategoryTemplate);

            if (configuredCategory is not null)
            {
                return configuredCategory;
            }
        }

        return NormalizeMediaType(mediaType) == "tv" ? "tv" : "movies";
    }

    private static string? NormalizeCategory(string? category)
        => string.IsNullOrWhiteSpace(category) ? null : category.Trim();

    private static IReadOnlyList<DecisionAlternativeExplanation> BuildDecisionAlternatives(MediaSearchPlan plan)
        => plan.Candidates
            .Select(candidate => new DecisionAlternativeExplanation(
                Name: candidate.ReleaseName,
                Status: candidate.DecisionStatus,
                Reason: candidate.Summary,
                Score: candidate.PreferenceEvaluation is null ? candidate.Score : null))
            .ToArray();

    /// <summary>Turns taken so far among clients of equal priority, per group of clients (#1147).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> ClientTurns = new(StringComparer.Ordinal);

    /// <summary>A client whose imported-share of recent grabs is below this is passed over while another is healthy.</summary>
    private const double FailingClientSuccessRate = 0.5;

    /// <summary>
    /// Picks the download client for a release (#1147). The rule, in the order a person would apply it:
    /// a client that keeps failing is passed over (one with no record counts as fine, never worse); then the
    /// lowest priority number wins; then clients sharing that priority take turns. <paramref name="takeTurn"/> is
    /// false for previews and held results, which must not use up a turn.
    /// </summary>
    internal async Task<(LibraryDownloadClientLinkItem? Client, string? Reason)> ChooseDownloadClientAsync(
        IReadOnlyList<LibraryDownloadClientLinkItem> clients,
        bool takeTurn,
        Func<LibraryDownloadClientLinkItem, int> priorityOf,
        CancellationToken cancellationToken)
    {
        if (clients.Count == 0)
        {
            return (null, null);
        }

        if (clients.Count == 1)
        {
            return (clients[0], $"{clients[0].DownloadClientName} is the only client that can take it.");
        }

        var pool = clients;
        var passedOver = new List<string>();
        var recordsByName = new Dictionary<string, DownloadClientRecord?>(StringComparer.Ordinal);
        if (intelligentRoutingService is not null)
        {
            var healthy = new List<LibraryDownloadClientLinkItem>();
            var due = new List<(LibraryDownloadClientLinkItem Client, DownloadClientRecord Record)>();
            var now = timeProvider.GetUtcNow();
            foreach (var client in clients)
            {
                var record = await intelligentRoutingService.GetDownloadClientRecordAsync(client.DownloadClientId, cancellationToken);
                var rate = record?.Rate
                    ?? await intelligentRoutingService.GetDownloadClientSuccessRateAsync(client.DownloadClientId, cancellationToken);
                if (rate is { } known && known < FailingClientSuccessRate)
                {
                    // A passed-over client is given a trial send again once it has had none for the cool-off, so
                    // it can show it has recovered (#1222). Without that its record could never change.
                    if (record is { } failing
                        && (failing.LastSendUtc is null || now - failing.LastSendUtc.Value >= DispatchRateCounting.ClientCoolOff))
                    {
                        due.Add((client, failing));
                    }

                    passedOver.Add(client.DownloadClientName);
                    recordsByName[client.DownloadClientName] = record;
                }
                else
                {
                    healthy.Add(client);
                }
            }

            if (due.Count > 0)
            {
                var trial = due.OrderBy(entry => priorityOf(entry.Client)).ThenBy(entry => entry.Client.DownloadClientId, StringComparer.Ordinal).First();
                var others = passedOver.Where(name => !string.Equals(name, trial.Client.DownloadClientName, StringComparison.Ordinal)).ToArray();
                var trialReason = $"{trial.Client.DownloadClientName} is being given a trial send: only {DescribeRecord(trial.Record)}, but it has had no send for at least {(int)DispatchRateCounting.ClientCoolOff.TotalHours} hours, so this one shows whether it has recovered.";
                foreach (var name in others)
                {
                    trialReason += $" {DescribePassedOver(name, recordsByName.GetValueOrDefault(name))}.";
                }

                return (trial.Client, trialReason);
            }

            if (healthy.Count > 0)
            {
                pool = healthy;
            }
            else
            {
                passedOver.Clear();
            }
        }

        var best = pool.Min(priorityOf);
        var tied = pool.Where(client => priorityOf(client) == best)
            .OrderBy(client => client.DownloadClientId, StringComparer.Ordinal)
            .ToArray();

        LibraryDownloadClientLinkItem chosen;
        string reason;
        if (tied.Length == 1)
        {
            chosen = tied[0];
            reason = $"{chosen.DownloadClientName} has the highest priority ({priorityOf(chosen)}) of the clients that can take it.";
        }
        else
        {
            var key = string.Join('|', tied.Select(client => client.DownloadClientId));
            var turn = takeTurn
                ? ClientTurns.AddOrUpdate(key, 0, (_, value) => value + 1)
                : ClientTurns.GetValueOrDefault(key);
            chosen = tied[turn % tied.Length];
            reason = $"{chosen.DownloadClientName} shares the top priority ({best}) with {tied.Length - 1} other client{(tied.Length == 2 ? "" : "s")}, and it was its turn.";
        }

        if (passedOver.Count > 0)
        {
            foreach (var name in passedOver)
            {
                reason += $" {DescribePassedOver(name, recordsByName.GetValueOrDefault(name))}.";
            }
        }

        return (chosen, reason);
    }

    /// <summary>"2 of its last 10 downloads imported (counting the last 30 days)": what the rate is made of and over what window (#1222).</summary>
    internal static string DescribeRecord(DownloadClientRecord record)
        => $"{record.Imported} of its last {record.Counted} download{(record.Counted == 1 ? "" : "s")} imported (counting the last {record.WindowDays} days)";

    private static string DescribePassedOver(string name, DownloadClientRecord? record)
        => record is null
            ? $"{name} was passed over because most of its recent downloads failed"
            : $"{name} was passed over: {DescribeRecord(record)}";

    private async Task<ReadyConnections> ResolveReadyConnectionsAsync(
        AcquisitionDecisionRequest request,
        CancellationToken cancellationToken)
    {
        // Tests and pure policy callers may intentionally supply a planner without
        // a connections store. Runtime DI always supplies it, so real acquisition
        // never treats a merely linked connection as ready.
        if (connectionsRepository is null)
        {
            return new ReadyConnections(
                request.Sources,
                request.DownloadClients,
                new Dictionary<string, DownloadClientItem>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        var indexers = await connectionsRepository.ListIndexersAsync(cancellationToken);
        var clients = await connectionsRepository.ListDownloadClientsAsync(cancellationToken);
        var readyIndexerIds = indexers
            .Where(item => item.IsEnabled && string.Equals(item.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var readyClientDetails = clients
            .Where(item => item.IsEnabled && IsReadyDownloadClient(item))
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);

        var readyClients = request.DownloadClients.Where(client => readyClientDetails.ContainsKey(client.DownloadClientId)).ToArray();
        var clientKinds = readyClients
            .Select(client => DownloadProtocols.ClientKind(readyClientDetails[client.DownloadClientId].Protocol))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceKinds = indexers.ToDictionary(
            item => item.Id,
            item => DownloadProtocols.SourceKind(item.Protocol),
            StringComparer.OrdinalIgnoreCase);

        // An indexer carrying tags is searched only for a title sharing one of
        // them; an untagged indexer is searched for any title (#990), the same
        // rule already applied to download clients (#808).
        var indexerTags = indexers.ToDictionary(
            item => item.Id,
            item => item.Tags,
            StringComparer.OrdinalIgnoreCase);

        // A source whose releases no ready client can take is not searched. Its
        // best release would otherwise win the search and have nowhere to go.
        // The library's own choices for this indexer (#1354): "Only for titles tagged" and "Never for titles tagged".
        var libraryTagSkips = new List<string>();
        var indexerNames = indexers.ToDictionary(item => item.Id, item => item.Name, StringComparer.OrdinalIgnoreCase);
        var readySources = request.Sources
            .Where(source => readyIndexerIds.Contains(source.IndexerId))
            .Where(source =>
            {
                var refusal = Deluno.Contracts.TagRoutingMatch.LibrarySourceRefusal(
                    source.RequiredTags,
                    source.ExcludedTags,
                    request.TagNames,
                    indexerNames.TryGetValue(source.IndexerId, out var name) ? name : source.IndexerName,
                    string.IsNullOrWhiteSpace(request.Title) ? "this title" : request.Title);
                if (refusal is not null) libraryTagSkips.Add(refusal);
                return refusal is null;
            })
            .Where(source => !indexerTags.TryGetValue(source.IndexerId, out var tags)
                || DownloadClientTagMatch.IsEligible(tags, request.TagNames))
            .ToArray();
        var usableSources = readySources
            .Where(source => readyClients.Length == 0
                || !sourceKinds.TryGetValue(source.IndexerId, out var kind)
                || clientKinds.Contains(kind))
            .ToArray();
        return new ReadyConnections(
            usableSources,
            readyClients,
            readyClientDetails,
            sourceKinds,
            NoClientForAnySource: readySources.Length > 0 && usableSources.Length == 0,
            LibraryTagSkips: libraryTagSkips);
    }

    private static bool IsReadyDownloadClient(DownloadClientItem client)
        => string.Equals(client.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase);

    private sealed record ReadyConnections(
        IReadOnlyList<LibrarySourceLinkItem> Sources,
        IReadOnlyList<LibraryDownloadClientLinkItem> DownloadClients,
        IReadOnlyDictionary<string, DownloadClientItem> DownloadClientDetails,
        IReadOnlyDictionary<string, string> SourceKinds,
        bool NoClientForAnySource = false,
        IReadOnlyList<string>? LibraryTagSkips = null)
    {
        /// <summary>
        /// The ready clients that can take a release from this source. When the
        /// source or a client is not known here (a planner built without a
        /// connections store), every client stays eligible, as before.
        ///
        /// <para>A client carrying tags is used only for a title sharing one of
        /// them; an untagged client takes any title, tagged or not (#808), the
        /// same rule Radarr/Sonarr apply.</para>
        /// </summary>
        public IReadOnlyList<LibraryDownloadClientLinkItem> ClientsFor(string? indexerId, IReadOnlyList<string>? titleTags = null)
        {
            var byKind = string.IsNullOrWhiteSpace(indexerId) || !SourceKinds.TryGetValue(indexerId, out var kind)
                ? DownloadClients
                : DownloadClients
                    .Where(client => !DownloadClientDetails.TryGetValue(client.DownloadClientId, out var detail)
                        || string.Equals(DownloadProtocols.ClientKind(detail.Protocol), kind, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

            string? TagsOf(LibraryDownloadClientLinkItem client)
                => DownloadClientDetails.TryGetValue(client.DownloadClientId, out var detail) ? detail.Tags : null;

            // A client tagged for this title wins over the untagged ones, as in
            // Radarr/Sonarr (#1041); untagged clients are the fallback.
            return DownloadClientTagMatch.PreferTagged(
                    byKind.Where(client => !DownloadClientDetails.ContainsKey(client.DownloadClientId)
                        || DownloadClientTagMatch.IsEligible(TagsOf(client), titleTags)),
                    TagsOf,
                    titleTags)
                .ToArray();
        }
    }

    private sealed class DisabledReleaseRankingService : IReleaseRankingModelService
    {
        public RankingModelStatus GetStatus() =>
            new(
                Enabled: false,
                AutoDispatchImpactEnabled: false,
                MaxAbsoluteBoost: 0,
                Mode: "disabled",
                Notes: "Ranking model disabled.");

        public ReleaseRankingBoostResult Score(ReleaseRankingFeatures features, bool hardBlocked) =>
            new(
                Enabled: false,
                Applied: false,
                BoostPoints: 0,
                Explanation: "Ranking model disabled.");
    }
}

/// <summary>
/// Radarr/Sonarr's own tag-routing rule (#808, extended to indexers and
/// notification connections by #990): a connection carrying tags is used
/// only for a title sharing one of them, and an untagged connection is used
/// for any title regardless of its tags.
///
/// <para>The actual rule lives in <see cref="Deluno.Contracts.TagRoutingMatch"/>
/// so Deluno.Notifications can share it without a project reference cycle
/// (Deluno.Integrations already depends on Deluno.Notifications). This class
/// stays as a thin forwarder so existing callers and tests keep working.</para>
/// </summary>
public static class DownloadClientTagMatch
{
    public static bool IsEligible(string? clientTags, IReadOnlyList<string>? titleTags)
        => Deluno.Contracts.TagRoutingMatch.IsEligible(clientTags, titleTags);

    /// <summary>
    /// Why a grab went to this client, in words (#808) — null for an untagged
    /// client, which takes any title and so is never the reason a routing
    /// choice needs explaining.
    /// </summary>
    public static string? Explain(string? clientTags, IReadOnlyList<string>? titleTags, string clientName)
    {
        var matchedTag = MatchedTag(clientTags, titleTags);
        return matchedTag is null
            ? null
            : $"{clientName} is tagged \"{matchedTag}\" like this title, so it was chosen before download clients with no tag.";
    }

    /// <summary>True when the client carries tags and one of them is on the title.</summary>
    public static bool IsTagMatched(string? clientTags, IReadOnlyList<string>? titleTags)
        => MatchedTag(clientTags, titleTags) is not null;

    /// <summary>
    /// Radarr/Sonarr's DownloadClientProvider rule (#1041): among the eligible
    /// clients (already in priority order), those whose tags match the title
    /// come first; only when none match do the untagged ones get the grab.
    /// </summary>
    public static IEnumerable<T> PreferTagged<T>(
        IEnumerable<T> eligibleInPriorityOrder,
        Func<T, string?> tagsOf,
        IReadOnlyList<string>? titleTags)
    {
        var list = eligibleInPriorityOrder.ToArray();
        var matched = list.Where(item => IsTagMatched(tagsOf(item), titleTags)).ToArray();
        return matched.Length > 0 ? matched : list;
    }

    private static string? MatchedTag(string? clientTags, IReadOnlyList<string>? titleTags)
    {
        if (string.IsNullOrWhiteSpace(clientTags) || titleTags is null || titleTags.Count == 0)
        {
            return null;
        }

        var held = titleTags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return clientTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(held.Contains);
    }
}

public sealed record AcquisitionDecisionRequest(
    string Title,
    int? Year,
    string MediaType,
    string? CurrentQuality,
    string? TargetQuality,
    IReadOnlyList<LibrarySourceLinkItem> Sources,
    IReadOnlyList<LibraryDownloadClientLinkItem> DownloadClients,
    IReadOnlyList<CustomFormatItem>? CustomFormats = null,
    bool PreviewOnly = false,
    int? SeasonNumber = null,
    int? EpisodeNumber = null,
    /// <summary>
    /// Quality tiers the governing profile permits. Empty leaves selection to
    /// the cutoff; a populated list rejects anything outside it.
    /// </summary>
    IReadOnlyList<string>? AllowedQualities = null,
    /// <summary>This profile's own size answers. Empty means it has none.</summary>
    IReadOnlyList<ProfileSizeRule>? SizeRules = null,
    /// <summary>When this profile stops looking for something better.</summary>
    QualityUpgradeStopPolicy? UpgradeStop = null,
    /// <summary>This profile's own acquisition answer, as a combinable rule.</summary>
    ProfileAcquisitionRules? ProfileAcquisition = null,
    /// <summary>The minimum, cutoff and minimum-upgrade-gain score guardrails this profile set (#778).</summary>
    ProfileScoreLimits? ScoreLimits = null,
    IReadOnlyList<string>? TagNames = null,
    string SearchKind = AcquisitionSearchKinds.Automatic,
    DateTimeOffset? AvailableUtc = null,
    string? CurrentReleaseName = null,
    int? CurrentCustomFormatScore = null,
    bool UpgradeUntilCutoff = true,
    string? NumberingScheme = null,
    int? AbsoluteNumber = null,
    DateOnly? AirDate = null,
    int? SceneSeasonNumber = null,
    int? SceneEpisodeNumber = null,
    /// <summary>
    /// The last installed-file evaluation, when one was persisted for this
    /// title. The decision engine accepts it only when its plan hash matches
    /// the plan compiled for this search; otherwise it re-derives a baseline
    /// instead of comparing evidence from different policy versions.
    /// </summary>
    PreferenceEvaluationSnapshot? CurrentPreferenceEvaluation = null,
    ReleasePreferencePlan? PreferencePlan = null,
    bool CurrentFilePresent = false,
    /// <summary>
    /// The title's real runtime in minutes, when metadata supplies one. Scales
    /// the Size Rules floor/ceiling against the title's actual length instead
    /// of the fixed reference runtime the profile is configured against
    /// (#630).
    /// </summary>
    int? RuntimeMinutes = null,
    /// <summary>
    /// The title's own IMDb id, when the catalogue has one. Carried down to
    /// the tracker search so a definition that only filters server-side by
    /// IMDb id (EZTV's JSON API, #606) can search the right show.
    /// </summary>
    string? ImdbId = null,
    /// <summary>
    /// The title's own genres, comma-separated, when the catalogue has them
    /// (#742). A TV search whose genres name "Anime" queries each indexer's
    /// own anime categories instead of its normal ones, when configured.
    /// </summary>
    string? Genres = null,
    /// <summary>Region-specific spellings metadata gave for this title, beyond <see cref="Title"/> itself (#861).</summary>
    IReadOnlyList<string>? OtherTitles = null,
    /// <summary>
    /// Seasons that have an episode Deluno no longer wants (removed by retention, deleted by the person, or gone from
    /// disk). A season pack for one of them is never picked, because it would download those episodes again (#1072);
    /// the new episodes are fetched one by one instead.
    /// </summary>
    IReadOnlyCollection<int>? SeasonsWithRemovedEpisodes = null,
    /// <summary>The movie or show this search is for, so the releases it turns down can be shown against it (#1098).</summary>
    string? EntityId = null,
    /// <summary>The provider's titles for the searched season's episodes, by episode number, so a release carrying another episode's title is refused (#1173).</summary>
    IReadOnlyDictionary<int, string>? EpisodeTitles = null,
    /// <summary>The provider's episode titles for every season, for a search of the whole show with no target episode (#1173).</summary>
    IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>>? EpisodeTitlesBySeason = null,
    /// <summary>The indexers whose new release started this search; each is waited for, not skipped for pacing (#1184).</summary>
    IReadOnlyCollection<string>? PatientIndexerIds = null);

public sealed record AcquisitionDecisionPlan(
    MediaSearchPlan SearchPlan,
    string PolicyVersion,
    string Outcome,
    string SearchResult,
    int SourceCount,
    int DownloadClientCount,
    LibraryDownloadClientLinkItem? SelectedDownloadClient,
    bool ShouldDispatch,
    DownloadClientGrabRequest? DispatchRequest,
    IReadOnlyList<DecisionAlternativeExplanation> Alternatives,
    /// <summary>Why this client took the release, in words, for the Activity entry (#1147).</summary>
    string? ClientChoiceReason = null);

public sealed record AcquisitionSelectedReleaseRequest(
    string ReleaseName,
    string? IndexerId,
    string? IndexerName,
    string? DownloadUrl,
    string? CurrentQuality,
    string? TargetQuality,
    string? CandidateQuality = null,
    long? SizeBytes = null,
    int? Seeders = null,
    int? SourcePriorityScore = null,
    IReadOnlyList<CustomFormatItem>? CustomFormats = null,
    bool ForceOverride = false,
    string? OverrideReason = null,
    IReadOnlyList<string>? NeverGrabPatterns = null,
    bool PreventLowerQualityReplacements = false,
    string? ScoringMode = null,
    string? CurrentReleaseName = null,
    int? CurrentCustomFormatScore = null,
    string MediaType = "movies",
    GuidePackage? GuidePackage = null,
    ReleasePreferencePlan? PreferencePlan = null,
    PreferenceEvaluationSnapshot? CurrentPreferenceEvaluation = null,
    bool CurrentFilePresent = false,
    /// <summary>
    /// The profile's own size answers, so a release chosen by hand is judged
    /// against the same sizes an automatic search would have used.
    /// </summary>
    IReadOnlyList<ProfileSizeRule>? SizeRules = null,
    /// <summary>
    /// When this profile stops looking, so a release chosen by hand is judged
    /// by the same answer an automatic search would have used.
    /// </summary>
    QualityUpgradeStopPolicy? UpgradeStop = null,
    /// <summary>
    /// The title's real runtime in minutes, so a release picked by hand is
    /// judged against the same runtime-scaled Size Rules an automatic search
    /// would have used (#630).
    /// </summary>
    int? RuntimeMinutes = null,
    /// <summary>
    /// The profile's own score guardrails, so a release picked by hand honours
    /// the same minimum, cutoff and upgrade-gain limits an automatic search
    /// would have used (#778).
    /// </summary>
    ProfileScoreLimits? ScoreLimits = null);

public sealed record AcquisitionSelectedReleaseDecision(
    MediaSearchCandidate Candidate,
    string PolicyVersion,
    bool CanDispatch,
    bool RequiresOverride,
    string Reason,
    IReadOnlyList<DecisionAlternativeExplanation> Alternatives);
