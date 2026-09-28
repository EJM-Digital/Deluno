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

    public AcquisitionDecisionPipeline(
        IMediaSearchPlanner mediaSearchPlanner,
        IReleaseRankingModelService? rankingModelService = null,
        IIntelligentRoutingService? intelligentRoutingService = null,
        IConnectionsRepository? connectionsRepository = null,
        IBlockedReleaseRepository? blockedReleases = null)
    {
        this.mediaSearchPlanner = mediaSearchPlanner;
        this.rankingModelService = rankingModelService ?? DisabledRankingModelService;
        this.intelligentRoutingService = intelligentRoutingService;
        this.connectionsRepository = connectionsRepository;
        this.blockedReleases = blockedReleases;
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
                request.OtherTitles);

        searchPlan = await SkipBlockedReleasesAsync(searchPlan, cancellationToken);
        searchPlan = LeaveEpisodesToTheirOwnSearch(searchPlan, request);

        var bestCandidate = searchPlan.BestCandidate;
        // The client is chosen for the release that won, not before the search:
        // a torrent goes to a torrent client and an NZB to a Usenet one.
        var selectedClient = await SelectDownloadClientAsync(
            readyConnections.ClientsFor(bestCandidate?.IndexerId, request.TagNames),
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

        if (string.IsNullOrWhiteSpace(indexerId))
        {
            return byPriority.FirstOrDefault(client =>
                !clients.TryGetValue(client.DownloadClientId, out var detail)
                || DownloadClientTagMatch.IsEligible(detail.Tags, titleTags));
        }

        var source = (await connectionsRepository.ListIndexersAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, indexerId, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            return byPriority.FirstOrDefault(client =>
                !clients.TryGetValue(client.DownloadClientId, out var detail)
                || DownloadClientTagMatch.IsEligible(detail.Tags, titleTags));
        }

        var kind = DownloadProtocols.SourceKind(source.Protocol);
        return byPriority.FirstOrDefault(client =>
            clients.TryGetValue(client.DownloadClientId, out var detail)
            && string.Equals(DownloadProtocols.ClientKind(detail.Protocol), kind, StringComparison.OrdinalIgnoreCase)
            && DownloadClientTagMatch.IsEligible(detail.Tags, titleTags));
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
        var boostedSummary = preferencePlan is null
            ? scoreComputation.UsesModelSignal && boost.Applied
                ? $"{decision.Summary} {boost.Explanation} {scoreComputation.Explanation}"
                : $"{decision.Summary} {scoreComputation.Explanation}"
            : decision.Summary;
        var boostedReasons = preferencePlan is null
            ? scoreComputation.UsesModelSignal
                ? decision.Reasons.Concat([boost.Explanation, scoreComputation.Explanation]).ToArray()
                : decision.Reasons.Concat([scoreComputation.Explanation]).ToArray()
            : decision.Reasons;

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
    /// <para>The saying is the point. A silent skip is the behaviour the owner
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
                : $"{plan.Summary} {note}",
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
                : plan.Summary,
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
            candidate.IndexerName);

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

    private async Task<LibraryDownloadClientLinkItem?> SelectDownloadClientAsync(
        IReadOnlyList<LibraryDownloadClientLinkItem> clients,
        CancellationToken cancellationToken)
    {
        if (clients.Count == 0)
        {
            return null;
        }

        if (intelligentRoutingService is null)
        {
            return clients.OrderBy(client => client.Priority).FirstOrDefault();
        }

        LibraryDownloadClientLinkItem? selected = null;
        var bestScore = double.MinValue;
        foreach (var client in clients)
        {
            var successRate = await intelligentRoutingService.GetDownloadClientSuccessRateAsync(client.DownloadClientId, cancellationToken) ?? 0.5;
            var priorityScore = Math.Max(0, 120 - client.Priority);
            var composite = successRate * 100d * 0.65 + priorityScore * 0.35;
            if (composite > bestScore)
            {
                bestScore = composite;
                selected = client;
            }
        }

        return selected ?? clients.OrderBy(client => client.Priority).First();
    }

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
        var readySources = request.Sources
            .Where(source => readyIndexerIds.Contains(source.IndexerId))
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
            NoClientForAnySource: readySources.Length > 0 && usableSources.Length == 0);
    }

    private static bool IsReadyDownloadClient(DownloadClientItem client)
        => string.Equals(client.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase);

    private sealed record ReadyConnections(
        IReadOnlyList<LibrarySourceLinkItem> Sources,
        IReadOnlyList<LibraryDownloadClientLinkItem> DownloadClients,
        IReadOnlyDictionary<string, DownloadClientItem> DownloadClientDetails,
        IReadOnlyDictionary<string, string> SourceKinds,
        bool NoClientForAnySource = false)
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

            return byKind
                .Where(client => !DownloadClientDetails.TryGetValue(client.DownloadClientId, out var detail)
                    || DownloadClientTagMatch.IsEligible(detail.Tags, titleTags))
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
        => Deluno.Contracts.TagRoutingMatch.Explain(clientTags, titleTags, clientName);
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
    IReadOnlyList<string>? OtherTitles = null);

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
    IReadOnlyList<DecisionAlternativeExplanation> Alternatives);

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
