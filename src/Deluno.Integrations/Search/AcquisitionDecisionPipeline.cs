using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Decisions;
using Deluno.Platform.Contracts;

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

    public AcquisitionDecisionPipeline(
        IMediaSearchPlanner mediaSearchPlanner,
        IReleaseRankingModelService? rankingModelService = null,
        IIntelligentRoutingService? intelligentRoutingService = null)
    {
        this.mediaSearchPlanner = mediaSearchPlanner;
        this.rankingModelService = rankingModelService ?? DisabledRankingModelService;
        this.intelligentRoutingService = intelligentRoutingService;
    }

    public async Task<AcquisitionDecisionPlan> PlanAsync(
        AcquisitionDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var sourceCount = request.Sources.Count;
        var clientCount = request.DownloadClients.Count;
        var selectedClient = await SelectDownloadClientAsync(request.DownloadClients, cancellationToken);

        var searchPlan = sourceCount == 0 || clientCount == 0
            ? new MediaSearchPlan(
                BestCandidate: null,
                Candidates: [],
                Summary: sourceCount == 0
                    ? "No indexers are linked to this library yet."
                    : "No download client is linked to this library yet.")
            : await mediaSearchPlanner.BuildPlanAsync(
                request.Title,
                request.Year,
                request.MediaType,
                request.CurrentQuality,
                request.TargetQuality,
                request.Sources,
                request.CustomFormats,
                request.SeasonNumber,
                request.EpisodeNumber,
                cancellationToken);

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
            PolicyVersion: bestCandidate?.PolicyVersion ?? Deluno.Platform.Quality.MediaPolicyCatalog.CurrentVersion,
            Outcome: outcome,
            SearchResult: BuildSearchResult(searchPlan, clientCount),
            SourceCount: sourceCount,
            DownloadClientCount: clientCount,
            SelectedDownloadClient: selectedClient,
            ShouldDispatch: outcome == "matched" && !request.PreviewOnly,
            DispatchRequest: bestCandidate is null || selectedClient is null
                ? null
                : BuildGrabRequest(bestCandidate, request.MediaType, DispatchCategory(request.MediaType), selectedClient),
            Alternatives: BuildDecisionAlternatives(searchPlan));
    }

    public AcquisitionSelectedReleaseDecision EvaluateSelectedRelease(AcquisitionSelectedReleaseRequest request)
    {
        var quality = request.CandidateQuality
            ?? Deluno.Platform.Quality.LibraryQualityDecider.DetectQuality(request.ReleaseName)
            ?? "WEB 1080p";
        var customFormatScore = CustomFormatMatcher.Evaluate(
            request.ReleaseName,
            request.CustomFormats,
            out var matchedCustomFormats);
        var decision = ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            request.ReleaseName,
            quality,
            request.CurrentQuality,
            request.TargetQuality,
            request.SizeBytes,
            request.Seeders,
            request.DownloadUrl,
            SourcePriorityScore: request.SourcePriorityScore ?? 0,
            CustomFormatScore: customFormatScore,
            request.NeverGrabPatterns));
        var boost = rankingModelService.Score(new ReleaseRankingFeatures(
            Seeders: request.Seeders,
            SizeBytes: request.SizeBytes,
            QualityDelta: decision.QualityDelta,
            CustomFormatScore: decision.CustomFormatScore,
            SourcePriorityScore: request.SourcePriorityScore ?? 0,
            EstimatedBitrateMbps: decision.EstimatedBitrateMbps,
            ReleaseAgeHours: null), hardBlocked: decision.Status == "rejected");
        var boostedScore = decision.Score + boost.BoostPoints;
        var boostedSummary = boost.Applied ? $"{decision.Summary} {boost.Explanation}" : decision.Summary;
        var boostedReasons = boost.Applied
            ? decision.Reasons.Concat([boost.Explanation]).ToArray()
            : decision.Reasons.ToArray();

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
            MatchedCustomFormats: matchedCustomFormats);

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

    private static string DispatchCategory(string mediaType)
        => NormalizeMediaType(mediaType) == "tv" ? "tv" : "movies";

    private static IReadOnlyList<DecisionAlternativeExplanation> BuildDecisionAlternatives(MediaSearchPlan plan)
        => plan.Candidates
            .Take(12)
            .Select(candidate => new DecisionAlternativeExplanation(
                Name: candidate.ReleaseName,
                Status: candidate.DecisionStatus,
                Reason: candidate.Summary,
                Score: candidate.Score))
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
    int? EpisodeNumber = null);

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
    bool PreventLowerQualityReplacements = false);

public sealed record AcquisitionSelectedReleaseDecision(
    MediaSearchCandidate Candidate,
    string PolicyVersion,
    bool CanDispatch,
    bool RequiresOverride,
    string Reason,
    IReadOnlyList<DecisionAlternativeExplanation> Alternatives);
