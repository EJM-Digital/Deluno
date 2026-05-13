using Deluno.Platform.Contracts;

namespace Deluno.Integrations.Search;

public sealed record ReleaseScoreComputation(
    int FinalScore,
    string Mode,
    bool UsesModelSignal,
    string Explanation);

public static class ReleaseScoringModePolicy
{
    public static ReleaseScoreComputation Compute(
        int ruleScore,
        ReleaseRankingBoostResult boost,
        string? mode)
    {
        var normalizedMode = SearchScoringModes.Normalize(mode);
        return normalizedMode switch
        {
            SearchScoringModes.RulesOnly => new ReleaseScoreComputation(
                FinalScore: ruleScore,
                Mode: normalizedMode,
                UsesModelSignal: false,
                Explanation: "Rules-only mode kept deterministic score."),
            SearchScoringModes.MlOnly => ComputeMlOnly(ruleScore, boost, normalizedMode),
            _ => new ReleaseScoreComputation(
                FinalScore: ruleScore + boost.BoostPoints,
                Mode: SearchScoringModes.Hybrid,
                UsesModelSignal: boost.Applied,
                Explanation: boost.Applied
                    ? $"Hybrid mode combined deterministic score with ML boost ({boost.BoostPoints:+#;-#;0})."
                    : "Hybrid mode used deterministic score because ML boost was not applied.")
        };
    }

    private static ReleaseScoreComputation ComputeMlOnly(
        int ruleScore,
        ReleaseRankingBoostResult boost,
        string normalizedMode)
    {
        if (!boost.Enabled)
        {
            return new ReleaseScoreComputation(
                FinalScore: ruleScore,
                Mode: normalizedMode,
                UsesModelSignal: false,
                Explanation: "ML-only mode fell back to deterministic score because ML ranking is disabled.");
        }

        if (!boost.Applied)
        {
            return new ReleaseScoreComputation(
                FinalScore: ruleScore,
                Mode: normalizedMode,
                UsesModelSignal: false,
                Explanation: "ML-only mode fell back to deterministic score because no ML boost was available.");
        }

        var scaledMlScore = boost.BoostPoints * 40;
        return new ReleaseScoreComputation(
            FinalScore: scaledMlScore,
            Mode: normalizedMode,
            UsesModelSignal: true,
            Explanation: $"ML-only mode ranked using ML signal ({boost.BoostPoints:+#;-#;0}, scaled to {scaledMlScore:+#;-#;0}).");
    }
}
