using Deluno.Platform.Contracts;
using Deluno.Platform.Quality;
using Deluno.Series.Contracts;

namespace Deluno.Series.Services;

public interface ISeriesWorkflowService
{
    EpisodeWorkflowDecision EvaluateEpisodeWantedStatus(
        string? currentQuality,
        string? targetQuality,
        bool qualityCutoffMet,
        bool upgradeUntilCutoff,
        bool upgradeUnknownItems);

    EpisodeWorkflowDecision EvaluateCandidate(
        EpisodeCandidateEvaluationInput input);

    bool IsReplacementAllowed(
        string? currentQuality,
        string candidateQuality,
        bool preventLowerQualityReplacements);

    int? CalculateQualityDelta(
        string? currentQuality,
        string candidateQuality,
        QualityProfileItem? profile);

    SeasonPackDecision EvaluateSeasonPackStrategy(
        IReadOnlyList<SeriesEpisodeInventoryItem> seasonEpisodes,
        bool monitoredOnly);
}

public sealed record SeasonPackDecision(
    bool PreferSeasonPack,
    string Reason,
    int MonitoredMissingCount,
    int TotalMonitoredCount);

public sealed class SeriesWorkflowService : ISeriesWorkflowService
{
    private readonly IVersionedMediaPolicyEngine _policyEngine;

    public SeriesWorkflowService(IVersionedMediaPolicyEngine policyEngine)
    {
        _policyEngine = policyEngine;
    }

    public EpisodeWorkflowDecision EvaluateEpisodeWantedStatus(
        string? currentQuality,
        string? targetQuality,
        bool qualityCutoffMet,
        bool upgradeUntilCutoff,
        bool upgradeUnknownItems)
    {
        var decision = MediaDecisionRules.DecideWantedState(new MediaWantedDecisionInput(
            MediaType: "tv",
            HasFile: !string.IsNullOrWhiteSpace(currentQuality),
            CurrentQuality: currentQuality,
            CutoffQuality: targetQuality,
            UpgradeUntilCutoff: upgradeUntilCutoff,
            UpgradeUnknownItems: upgradeUnknownItems));

        return new EpisodeWorkflowDecision(
            WantedStatus: decision.WantedStatus,
            Reason: decision.WantedReason,
            IsReplacementAllowed: true,
            QualityDelta: null,
            CurrentQuality: currentQuality,
            TargetQuality: targetQuality);
    }

    public EpisodeWorkflowDecision EvaluateCandidate(EpisodeCandidateEvaluationInput input)
    {
        if (string.IsNullOrWhiteSpace(input.CandidateQuality))
        {
            return new EpisodeWorkflowDecision(
                WantedStatus: "unknown",
                Reason: "Candidate quality could not be detected.",
                IsReplacementAllowed: false,
                QualityDelta: null,
                CurrentQuality: input.CurrentQuality,
                TargetQuality: input.TargetQuality);
        }

        var delta = CalculateQualityDelta(input.CurrentQuality, input.CandidateQuality, input.Profile);

        var qualityCutoffMet = string.IsNullOrWhiteSpace(input.TargetQuality) ||
            string.IsNullOrWhiteSpace(input.CurrentQuality)
            ? false
            : CalculateQualityDelta(input.CurrentQuality, input.TargetQuality, input.Profile) is >= 0;

        var wantedDecision = EvaluateEpisodeWantedStatus(
            input.CurrentQuality,
            input.TargetQuality,
            qualityCutoffMet,
            input.UpgradeUntilCutoff,
            input.UpgradeUnknownItems);

        var isReplacementAllowed = IsReplacementAllowed(
            input.CurrentQuality,
            input.CandidateQuality,
            input.PreventLowerQualityReplacements);

        return EvaluateCandidateGrab(
            currentQuality: input.CurrentQuality,
            candidateQuality: input.CandidateQuality,
            targetQuality: input.TargetQuality,
            qualityDelta: delta,
            wantedStatus: wantedDecision.WantedStatus,
            isReplacementAllowed: isReplacementAllowed);
    }

    public bool IsReplacementAllowed(
        string? currentQuality,
        string candidateQuality,
        bool preventLowerQualityReplacements)
    {
        if (!preventLowerQualityReplacements)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(currentQuality))
        {
            return true;
        }

        var delta = CalculateQualityDelta(currentQuality, candidateQuality, null);
        return delta == null || delta >= 0;
    }

    public int? CalculateQualityDelta(
        string? currentQuality,
        string candidateQuality,
        QualityProfileItem? profile)
    {
        if (string.IsNullOrWhiteSpace(currentQuality) || string.IsNullOrWhiteSpace(candidateQuality))
        {
            return null;
        }

        var currentRank = _policyEngine.QualityRank(currentQuality);
        var candidateRank = _policyEngine.QualityRank(candidateQuality);

        if (currentRank < 0 || candidateRank < 0)
        {
            return null;
        }

        return candidateRank - currentRank;
    }

    public SeasonPackDecision EvaluateSeasonPackStrategy(
        IReadOnlyList<SeriesEpisodeInventoryItem> seasonEpisodes,
        bool monitoredOnly)
    {
        var relevant = monitoredOnly
            ? seasonEpisodes.Where(e => e.Monitored).ToList()
            : seasonEpisodes.ToList();

        if (relevant.Count == 0)
        {
            return new SeasonPackDecision(
                PreferSeasonPack: false,
                Reason: "No monitored episodes found in this season.",
                MonitoredMissingCount: 0,
                TotalMonitoredCount: 0);
        }

        var missingCount = relevant.Count(e => !e.HasFile);
        var totalCount = relevant.Count;
        var missingRatio = (double)missingCount / totalCount;

        // Prefer season pack when more than 60% of episodes are missing
        var preferSeasonPack = missingRatio >= 0.6;
        var reason = preferSeasonPack
            ? $"{missingCount}/{totalCount} episodes are missing — a season pack is preferred."
            : $"Only {missingCount}/{totalCount} episodes are missing — searching episode-by-episode is more efficient.";

        return new SeasonPackDecision(
            PreferSeasonPack: preferSeasonPack,
            Reason: reason,
            MonitoredMissingCount: missingCount,
            TotalMonitoredCount: totalCount);
    }

    private EpisodeWorkflowDecision EvaluateCandidateGrab(
        string? currentQuality,
        string candidateQuality,
        string? targetQuality,
        int? qualityDelta,
        string wantedStatus,
        bool isReplacementAllowed)
    {
        if (wantedStatus == "archived")
        {
            return new EpisodeWorkflowDecision(
                WantedStatus: "archived",
                Reason: "This episode is archived and not wanted.",
                IsReplacementAllowed: false,
                QualityDelta: qualityDelta,
                CurrentQuality: currentQuality,
                TargetQuality: targetQuality);
        }

        if (!string.IsNullOrWhiteSpace(currentQuality) && !isReplacementAllowed)
        {
            return new EpisodeWorkflowDecision(
                WantedStatus: "blocked",
                Reason: $"Replacement protection is enabled. Current quality ({currentQuality}) is equal to or higher than candidate ({candidateQuality}).",
                IsReplacementAllowed: false,
                QualityDelta: qualityDelta,
                CurrentQuality: currentQuality,
                TargetQuality: targetQuality);
        }

        if (wantedStatus == "missing")
        {
            return new EpisodeWorkflowDecision(
                WantedStatus: "missing",
                Reason: "This episode is missing from your library.",
                IsReplacementAllowed: true,
                QualityDelta: qualityDelta,
                CurrentQuality: currentQuality,
                TargetQuality: targetQuality);
        }

        if (wantedStatus == "upgrade")
        {
            var reason = qualityDelta.HasValue
                ? $"Quality upgrade available: {candidateQuality} (+{qualityDelta})"
                : "Quality upgrade available.";

            return new EpisodeWorkflowDecision(
                WantedStatus: "upgrade",
                Reason: reason,
                IsReplacementAllowed: true,
                QualityDelta: qualityDelta,
                CurrentQuality: currentQuality,
                TargetQuality: targetQuality);
        }

        return new EpisodeWorkflowDecision(
            WantedStatus: "waiting",
            Reason: "This episode is already at or above target quality.",
            IsReplacementAllowed: false,
            QualityDelta: qualityDelta,
            CurrentQuality: currentQuality,
            TargetQuality: targetQuality);
    }
}
