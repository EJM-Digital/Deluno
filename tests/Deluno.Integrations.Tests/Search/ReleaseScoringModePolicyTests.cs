using Deluno.Integrations.Search;
using Deluno.Platform.Contracts;

namespace Deluno.Integrations.Tests.Search;

public sealed class ReleaseScoringModePolicyTests
{
    [Fact]
    public void RulesOnly_ignores_boost_and_keeps_rule_score()
    {
        var result = ReleaseScoringModePolicy.Compute(
            ruleScore: 1200,
            boost: new ReleaseRankingBoostResult(true, true, 12, "ml boost"),
            mode: SearchScoringModes.RulesOnly);

        Assert.Equal(1200, result.FinalScore);
        Assert.Equal(SearchScoringModes.RulesOnly, result.Mode);
        Assert.False(result.UsesModelSignal);
    }

    [Fact]
    public void Hybrid_combines_rule_score_and_boost()
    {
        var result = ReleaseScoringModePolicy.Compute(
            ruleScore: 1200,
            boost: new ReleaseRankingBoostResult(true, true, 12, "ml boost"),
            mode: SearchScoringModes.Hybrid);

        Assert.Equal(1212, result.FinalScore);
        Assert.Equal(SearchScoringModes.Hybrid, result.Mode);
        Assert.True(result.UsesModelSignal);
    }

    [Fact]
    public void MlOnly_scales_boost_when_available()
    {
        var result = ReleaseScoringModePolicy.Compute(
            ruleScore: 1200,
            boost: new ReleaseRankingBoostResult(true, true, 9, "ml boost"),
            mode: SearchScoringModes.MlOnly);

        Assert.Equal(360, result.FinalScore);
        Assert.Equal(SearchScoringModes.MlOnly, result.Mode);
        Assert.True(result.UsesModelSignal);
    }

    [Fact]
    public void MlOnly_falls_back_to_rules_when_model_signal_missing()
    {
        var result = ReleaseScoringModePolicy.Compute(
            ruleScore: 1200,
            boost: new ReleaseRankingBoostResult(true, false, 0, "offline"),
            mode: SearchScoringModes.MlOnly);

        Assert.Equal(1200, result.FinalScore);
        Assert.False(result.UsesModelSignal);
    }
}
