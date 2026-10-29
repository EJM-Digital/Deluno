using Deluno.Integrations.Search;
using Deluno.Quality.ReleasePreferences;
using Moq;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// With a typed release-preference plan attached (the normal case) the rule score plus the model's boost is not what ranks a
/// release: its score stays 0, so a model that adds 500 points changes nothing about which release wins.
/// </summary>
public sealed class ATypedPlanDecidesTheWinnerTheRankingModelDoesNotTests
{
    private static IReleaseRankingModelService Model(int boostPoints, Action? asked = null)
    {
        var model = new Mock<IReleaseRankingModelService>();
        model.Setup(service => service.Score(It.IsAny<ReleaseRankingFeatures>(), It.IsAny<bool>()))
            .Callback(() => asked?.Invoke())
            .Returns(new ReleaseRankingBoostResult(true, true, boostPoints, "A big boost."));
        return model.Object;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    [InlineData(-500)]
    public void A_selected_release_judged_by_a_typed_plan_scores_the_same_whatever_the_model_adds(int boost)
    {
        var asked = 0;
        var pipeline = new AcquisitionDecisionPipeline(new Mock<IMediaSearchPlanner>().Object, Model(boost, () => asked++));
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan("movies", "WEB 1080p");

        var result = pipeline.EvaluateSelectedRelease(new AcquisitionSelectedReleaseRequest(
            ReleaseName: "Movie.2023.WEB.1080p-GROUP", IndexerId: null, IndexerName: null, DownloadUrl: "https://example.com/movie.torrent",
            CurrentQuality: null, TargetQuality: "WEB 1080p", SizeBytes: 8_000_000_000L, Seeders: 20, ForceOverride: false,
            OverrideReason: null, NeverGrabPatterns: null, PreventLowerQualityReplacements: false) { PreferencePlan = plan, ScoringMode = "hybrid" });

        Assert.Equal(1, asked);
        Assert.Equal(0, result.Candidate.Score);
    }
}
