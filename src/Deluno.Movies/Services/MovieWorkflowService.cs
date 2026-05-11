using Deluno.Movies.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Quality;

namespace Deluno.Movies.Services;

public interface IMovieWorkflowService
{
    MovieWorkflowDecision EvaluateWantedStatus(
        string? currentQuality,
        string? targetQuality,
        bool qualityCutoffMet,
        bool upgradeUntilCutoff,
        bool upgradeUnknownItems);

    MovieWorkflowDecision EvaluateCandidate(
        MovieCandidateEvaluationInput input);

    bool IsReplacementAllowed(
        string? currentQuality,
        string candidateQuality,
        bool preventLowerQualityReplacements);

    int? CalculateQualityDelta(
        string? currentQuality,
        string candidateQuality,
        QualityProfileItem? profile);
}

public sealed class MovieWorkflowService : IMovieWorkflowService
{
    private readonly IVersionedMediaPolicyEngine _policyEngine;

    public MovieWorkflowService(IVersionedMediaPolicyEngine policyEngine)
    {
        _policyEngine = policyEngine;
    }

    public MovieWorkflowDecision EvaluateWantedStatus(
        string? currentQuality,
        string? targetQuality,
        bool qualityCutoffMet,
        bool upgradeUntilCutoff,
        bool upgradeUnknownItems)
    {
        var decision = MediaDecisionRules.DecideWantedState(new MediaWantedDecisionInput(
            MediaType: "movies",
            HasFile: !string.IsNullOrWhiteSpace(currentQuality),
            CurrentQuality: currentQuality,
            CutoffQuality: targetQuality,
            UpgradeUntilCutoff: upgradeUntilCutoff,
            UpgradeUnknownItems: upgradeUnknownItems));

        return new MovieWorkflowDecision(
            WantedStatus: decision.WantedStatus,
            Reason: decision.WantedReason,
            IsReplacementAllowed: true,
            QualityDelta: null,
            CurrentQuality: currentQuality,
            TargetQuality: targetQuality);
    }

    public MovieWorkflowDecision EvaluateCandidate(MovieCandidateEvaluationInput input)
    {
        if (string.IsNullOrWhiteSpace(input.CandidateQuality))
        {
            return new MovieWorkflowDecision(
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

        var wantedDecision = EvaluateWantedStatus(
            input.CurrentQuality,
            input.TargetQuality,
            qualityCutoffMet,
            input.UpgradeUntilCutoff,
            input.UpgradeUnknownItems);

        var isReplacementAllowed = IsReplacementAllowed(
            input.CurrentQuality,
            input.CandidateQuality,
            input.PreventLowerQualityReplacements);

        var decision = EvaluateCandidateGrab(
            currentQuality: input.CurrentQuality,
            candidateQuality: input.CandidateQuality,
            targetQuality: input.TargetQuality,
            qualityDelta: delta,
            wantedStatus: wantedDecision.WantedStatus,
            isReplacementAllowed: isReplacementAllowed,
            preventLowerQualityReplacements: input.PreventLowerQualityReplacements);

        return decision;
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

    private MovieWorkflowDecision EvaluateCandidateGrab(
        string? currentQuality,
        string candidateQuality,
        string? targetQuality,
        int? qualityDelta,
        string wantedStatus,
        bool isReplacementAllowed,
        bool preventLowerQualityReplacements)
    {
        if (wantedStatus == "archived")
        {
            return new MovieWorkflowDecision(
                WantedStatus: "archived",
                Reason: "This movie is archived and not wanted.",
                IsReplacementAllowed: false,
                QualityDelta: qualityDelta,
                CurrentQuality: currentQuality,
                TargetQuality: targetQuality);
        }

        if (!string.IsNullOrWhiteSpace(currentQuality) && !isReplacementAllowed)
        {
            return new MovieWorkflowDecision(
                WantedStatus: "blocked",
                Reason: $"Replacement protection is enabled. Current quality ({currentQuality}) is equal to or higher than candidate ({candidateQuality}).",
                IsReplacementAllowed: false,
                QualityDelta: qualityDelta,
                CurrentQuality: currentQuality,
                TargetQuality: targetQuality);
        }

        if (wantedStatus == "missing")
        {
            return new MovieWorkflowDecision(
                WantedStatus: "missing",
                Reason: "This movie is missing from your library.",
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

            return new MovieWorkflowDecision(
                WantedStatus: "upgrade",
                Reason: reason,
                IsReplacementAllowed: true,
                QualityDelta: qualityDelta,
                CurrentQuality: currentQuality,
                TargetQuality: targetQuality);
        }

        return new MovieWorkflowDecision(
            WantedStatus: "waiting",
            Reason: "This movie is already at or above target quality.",
            IsReplacementAllowed: false,
            QualityDelta: qualityDelta,
            CurrentQuality: currentQuality,
            TargetQuality: targetQuality);
    }
}
