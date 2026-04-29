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

public sealed class AcquisitionDecisionPipeline(IMediaSearchPlanner mediaSearchPlanner)
    : IAcquisitionDecisionPipeline
{
    public async Task<AcquisitionDecisionPlan> PlanAsync(
        AcquisitionDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var sourceCount = request.Sources.Count;
        var clientCount = request.DownloadClients.Count;
        var selectedClient = request.DownloadClients
            .OrderBy(client => client.Priority)
            .FirstOrDefault();

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
        var quality = Deluno.Platform.Quality.LibraryQualityDecider.DetectQuality(request.ReleaseName) ?? "WEB 1080p";
        var decision = ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            request.ReleaseName,
            quality,
            request.CurrentQuality,
            request.TargetQuality,
            request.SizeBytes,
            request.Seeders,
            request.DownloadUrl,
            SourcePriorityScore: 0,
            CustomFormatScore: 0,
            request.NeverGrabPatterns));

        var candidate = new MediaSearchCandidate(
            ReleaseName: request.ReleaseName,
            IndexerId: request.IndexerId ?? "manual",
            IndexerName: string.IsNullOrWhiteSpace(request.IndexerName) ? "Manual selection" : request.IndexerName,
            Quality: quality,
            Score: decision.Score,
            MeetsCutoff: decision.MeetsCutoff,
            Summary: decision.Summary,
            DownloadUrl: request.DownloadUrl,
            SizeBytes: request.SizeBytes,
            Seeders: request.Seeders,
            DecisionStatus: decision.Status,
            DecisionReasons: decision.Reasons,
            RiskFlags: decision.RiskFlags,
            QualityDelta: decision.QualityDelta,
            CustomFormatScore: decision.CustomFormatScore,
            SeederScore: decision.SeederScore,
            SizeScore: decision.SizeScore,
            ReleaseGroup: decision.ReleaseGroup,
            EstimatedBitrateMbps: decision.EstimatedBitrateMbps);

        var safe = IsSafeForAutomaticDispatch(candidate);
        var canDispatch = safe || request.ForceOverride;
        return new AcquisitionSelectedReleaseDecision(
            Candidate: candidate,
            CanDispatch: canDispatch,
            RequiresOverride: !safe,
            Reason: safe
                ? candidate.Summary
                : request.ForceOverride
                    ? $"User override accepted {candidate.ReleaseName}: {request.OverrideReason ?? "No override reason supplied."}"
                    : $"Release requires force override because Deluno classified it as {candidate.DecisionStatus}.",
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
    bool PreviewOnly = false);

public sealed record AcquisitionDecisionPlan(
    MediaSearchPlan SearchPlan,
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
    long? SizeBytes = null,
    int? Seeders = null,
    bool ForceOverride = false,
    string? OverrideReason = null,
    IReadOnlyList<string>? NeverGrabPatterns = null);

public sealed record AcquisitionSelectedReleaseDecision(
    MediaSearchCandidate Candidate,
    bool CanDispatch,
    bool RequiresOverride,
    string Reason,
    IReadOnlyList<DecisionAlternativeExplanation> Alternatives);
