using Deluno.Contracts;
using Deluno.Movies.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Quality;
using Deluno.Quality.Contracts;

namespace Deluno.Movies.Services;

public interface IMovieWorkflowService
{
    MovieWorkflowDecision EvaluateWantedStatus(
        bool hasFile,
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

    /// <summary>
    /// Whether a file is on disk is its own fact, passed in rather than guessed
    /// from whether its quality is known. It used to be guessed, so a film
    /// imported from an existing library with an unnamed quality was reported
    /// as "still looking for this movie" while the catalogue, rightly, said it
    /// was already in the library.
    /// </summary>
    public MovieWorkflowDecision EvaluateWantedStatus(
        bool hasFile,
        string? currentQuality,
        string? targetQuality,
        bool qualityCutoffMet,
        bool upgradeUntilCutoff,
        bool upgradeUnknownItems)
    {
        var decision = MediaDecisionRules.DecideWantedState(new MediaWantedDecisionInput(
            MediaType: "movies",
            HasFile: hasFile,
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

        // A candidate evaluation carries no file fact of its own; a known current
        // quality is the only evidence of a file it has, as it always was here.
        var wantedDecision = EvaluateWantedStatus(
            !string.IsNullOrWhiteSpace(input.CurrentQuality),
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

        if (wantedStatus == WantedStatuses.Missing)
        {
            return new MovieWorkflowDecision(
                WantedStatus: WantedStatuses.Missing,
                Reason: "This movie is missing from your library.",
                IsReplacementAllowed: true,
                QualityDelta: qualityDelta,
                CurrentQuality: currentQuality,
                TargetQuality: targetQuality);
        }

        if (wantedStatus == WantedStatuses.Upgrade)
        {
            var reason = qualityDelta.HasValue
                ? $"Quality upgrade available: {candidateQuality} (+{qualityDelta})"
                : "Quality upgrade available.";

            return new MovieWorkflowDecision(
                WantedStatus: WantedStatuses.Upgrade,
                Reason: reason,
                IsReplacementAllowed: true,
                QualityDelta: qualityDelta,
                CurrentQuality: currentQuality,
                TargetQuality: targetQuality);
        }

        return new MovieWorkflowDecision(
            WantedStatus: WantedStatuses.Covered,
            Reason: "This movie is already at or above target quality.",
            IsReplacementAllowed: false,
            QualityDelta: qualityDelta,
            CurrentQuality: currentQuality,
            TargetQuality: targetQuality);
    }
}
