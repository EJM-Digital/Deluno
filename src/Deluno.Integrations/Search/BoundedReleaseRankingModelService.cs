using Microsoft.Extensions.Configuration;

namespace Deluno.Integrations.Search;

public sealed class BoundedReleaseRankingModelService(IConfiguration configuration) : IReleaseRankingModelService
{
    public ReleaseRankingBoostResult Score(ReleaseRankingFeatures features, bool hardBlocked)
    {
        var status = ReadStatus();
        if (!status.Enabled)
        {
            return new ReleaseRankingBoostResult(false, false, 0, "ML ranking pilot is disabled.");
        }

        if (hardBlocked)
        {
            return new ReleaseRankingBoostResult(true, false, 0, "Hard safety blocks override model boost.");
        }

        // Lightweight bounded model approximation. This is intentionally narrow so
        // deterministic rules remain primary.
        var raw = 0d;
        raw += Math.Clamp(features.QualityDelta, -2, 3) * 6.0;
        raw += Math.Clamp(features.CustomFormatScore, -100, 150) * 0.08;
        raw += Math.Clamp(features.Seeders ?? 0, 0, 120) * 0.22;
        raw += Math.Clamp(features.SourcePriorityScore, 0, 220) * 0.05;

        if (features.ReleaseAgeHours is > 0)
        {
            raw -= Math.Clamp(features.ReleaseAgeHours.Value, 0, 240) * 0.03;
        }

        if (features.EstimatedBitrateMbps is > 0 and < 1.2)
        {
            raw -= 8;
        }

        var maxBoost = status.MaxAbsoluteBoost;
        var boost = (int)Math.Round(Math.Clamp(raw, -maxBoost, maxBoost));
        var applied = boost != 0;
        var explanation = applied
            ? $"Bounded ML pilot boost {boost:+#;-#;0} applied."
            : "Bounded ML pilot produced no score adjustment.";
        return new ReleaseRankingBoostResult(true, applied, boost, explanation);
    }

    public RankingModelStatus GetStatus() => ReadStatus();

    private RankingModelStatus ReadStatus()
    {
        var enabled = configuration.GetValue("Deluno:RankingModel:Enabled", false);
        var autoDispatchImpactEnabled = configuration.GetValue("Deluno:RankingModel:AutoDispatchImpactEnabled", false);
        var maxAbsoluteBoost = Math.Clamp(configuration.GetValue("Deluno:RankingModel:MaxAbsoluteBoost", 28), 1, 60);
        var mode = configuration["Deluno:RankingModel:Mode"] ?? "offline";
        var notes = autoDispatchImpactEnabled
            ? "Model boost can influence runtime ranking only; deterministic blocks still win."
            : "Model boost is evaluated in bounded offline-safe mode with no auto-dispatch impact.";
        return new RankingModelStatus(
            Enabled: enabled,
            AutoDispatchImpactEnabled: autoDispatchImpactEnabled,
            MaxAbsoluteBoost: maxAbsoluteBoost,
            Mode: mode,
            Notes: notes);
    }
}
