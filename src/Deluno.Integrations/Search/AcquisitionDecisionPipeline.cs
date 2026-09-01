using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Decisions;
using Deluno.Libraries.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Quality.Contracts;
using Deluno.Quality.Guides;
using Deluno.Quality.ReleasePreferences;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;

namespace Deluno.Integrations.Search;

public interface IAcquisitionDecisionPipeline
{
    Task<AcquisitionDecisionPlan> PlanAsync(
        AcquisitionDecisionRequest request,
        CancellationToken cancellationToken = default);

    AcquisitionSelectedReleaseDecision EvaluateSelectedRelease(AcquisitionSelectedReleaseRequest request);
}

public sealed class AcquisitionDecisionPipeline : IAcquisitionDecisionPipeline
{
    private static readonly IReleaseRankingModelService DisabledRankingModelService = new DisabledReleaseRankingService();
    private readonly IMediaSearchPlanner mediaSearchPlanner;
    private readonly IReleaseRankingModelService rankingModelService;
    private readonly IIntelligentRoutingService? intelligentRoutingService;
    private readonly IConnectionsRepository? connectionsRepository;

    public AcquisitionDecisionPipeline(
        IMediaSearchPlanner mediaSearchPlanner,
        IReleaseRankingModelService? rankingModelService = null,
        IIntelligentRoutingService? intelligentRoutingService = null,
        IConnectionsRepository? connectionsRepository = null)
    {
        this.mediaSearchPlanner = mediaSearchPlanner;
        this.rankingModelService = rankingModelService ?? DisabledRankingModelService;
        this.intelligentRoutingService = intelligentRoutingService;
        this.connectionsRepository = connectionsRepository;
    }

    public async Task<AcquisitionDecisionPlan> PlanAsync(
        AcquisitionDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var readyConnections = await ResolveReadyConnectionsAsync(request, cancellationToken);
        var sourceCount = readyConnections.Sources.Count;
        var clientCount = readyConnections.DownloadClients.Count;
        var selectedClient = await SelectDownloadClientAsync(readyConnections.DownloadClients, cancellationToken);
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
                request.CurrentPreferenceEvaluation,
                request.PreferencePlan,
                request.CurrentFilePresent);

        var bestCandidate = searchPlan.BestCandidate;
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

    public AcquisitionSelectedReleaseDecision EvaluateSelectedRelease(AcquisitionSelectedReleaseRequest request)
    {
        var quality = request.CandidateQuality
            ?? Deluno.Quality.LibraryQualityDecider.DetectQuality(request.ReleaseName)
            ?? "WEB 1080p";
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
            PreferencePlan: preferencePlan,
            CurrentReleaseName: request.CurrentReleaseName,
            CurrentPreferenceEvaluation: request.CurrentPreferenceEvaluation,
            CurrentFilePresent: request.CurrentFilePresent));
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
                    : $"Release requires force override because Deluno classified it as {candidate.DecisionStatus}.";

        return new AcquisitionSelectedReleaseDecision(
            Candidate: candidate,
            PolicyVersion: decision.PolicyVersion,
            CanDispatch: canDispatch,
            RequiresOverride: !safe && !replacementBlocked,
            Reason: reason,
            Alternatives: BuildDecisionAlternatives(new MediaSearchPlan(candidate, [candidate], candidate.Summary)));
    }

    public static bool IsSafeForAutomaticDispatch(MediaSearchCandidate candidate)
        => string.Equals(candidate.DecisionStatus, "preferred", StringComparison.OrdinalIgnoreCase) &&
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
            return $"{plan.Summary} Held for manual review because the best candidate is {plan.BestCandidate.DecisionStatus}.";
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
                new Dictionary<string, DownloadClientItem>(StringComparer.OrdinalIgnoreCase));
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

        return new ReadyConnections(
            request.Sources.Where(source => readyIndexerIds.Contains(source.IndexerId)).ToArray(),
            request.DownloadClients.Where(client => readyClientDetails.ContainsKey(client.DownloadClientId)).ToArray(),
            readyClientDetails);
    }

    private static bool IsReadyDownloadClient(DownloadClientItem client)
        => string.Equals(client.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase);

    private sealed record ReadyConnections(
        IReadOnlyList<LibrarySourceLinkItem> Sources,
        IReadOnlyList<LibraryDownloadClientLinkItem> DownloadClients,
        IReadOnlyDictionary<string, DownloadClientItem> DownloadClientDetails);

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
    bool CurrentFilePresent = false);

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
    bool CurrentFilePresent = false);

public sealed record AcquisitionSelectedReleaseDecision(
    MediaSearchCandidate Candidate,
    string PolicyVersion,
    bool CanDispatch,
    bool RequiresOverride,
    string Reason,
    IReadOnlyList<DecisionAlternativeExplanation> Alternatives);
