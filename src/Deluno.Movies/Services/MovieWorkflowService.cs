using Deluno.Contracts;
using Deluno.Movies.Contracts;
using Deluno.Quality;

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
}

public sealed class MovieWorkflowService : IMovieWorkflowService
{
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

}
