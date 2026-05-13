using System.Globalization;
using System.Text.RegularExpressions;
using Deluno.Platform.Quality;

namespace Deluno.Integrations.Search;

public static partial class ReleaseDecisionEngine
{
    public static ReleaseDecision Decide(ReleaseDecisionInput input, QualityModelSnapshot? qualityModel = null)
    {
        var normalizedCurrent = LibraryQualityDecider.NormalizeQuality(input.CurrentQuality);
        var normalizedTarget = LibraryQualityDecider.NormalizeQuality(input.TargetQuality) ?? "WEB 1080p";
        var normalizedCandidate = LibraryQualityDecider.NormalizeQuality(input.Quality) ?? input.Quality;
        var candidateRank = QualityRank(normalizedCandidate);
        var currentRank = QualityRank(normalizedCurrent);
        var targetRank = QualityRank(normalizedTarget);
        var qualityDelta = currentRank == 0 ? candidateRank : candidateRank - currentRank;
        var meetsCutoff = candidateRank >= targetRank;
        var reasons = new List<string>();
        var risks = new List<string>();
        var hardReject = false;

        if (string.IsNullOrWhiteSpace(input.DownloadUrl))
        {
            risks.Add("No downloadable URL was returned by the indexer.");
        }

        if (LooksLikeSample(input.ReleaseName))
        {
            hardReject = true;
            risks.Add("Release name looks like a sample, trailer, proof, or extras file.");
        }

        if (ContainsBlockedToken(input.ReleaseName))
        {
            hardReject = true;
            risks.Add("Release name contains a blocked token such as CAM, Telesync, workprint, or screener.");
        }

        var matchedNeverGrab = MatchNeverGrabPattern(input.ReleaseName, input.NeverGrabPatterns);
        if (!string.IsNullOrWhiteSpace(matchedNeverGrab))
        {
            hardReject = true;
            risks.Add($"Release name matched the never-grab pattern '{matchedNeverGrab}'.");
        }

        reasons.Add(meetsCutoff
            ? $"Quality {normalizedCandidate} meets or exceeds cutoff {normalizedTarget}."
            : $"Quality {normalizedCandidate} is below cutoff {normalizedTarget}.");

        if (currentRank > 0)
        {
            if (qualityDelta > 0)
            {
                reasons.Add($"Quality rank improves current file by {qualityDelta} step(s).");
            }

            if (qualityDelta == 0)
            {
                reasons.Add("Quality rank matches the current file, so custom formats and risk decide whether it is worthwhile.");
            }

            if (qualityDelta < 0)
            {
                var currentMeetsCutoff = currentRank >= targetRank;
                if (currentMeetsCutoff)
                {
                    hardReject = true;
                    risks.Add($"Downgrade blocked: current file ({normalizedCurrent}) already meets the quality target ({normalizedTarget}). Grab this manually if you want to downgrade.");
                }
                else
                {
                    risks.Add($"Quality rank is {Math.Abs(qualityDelta)} step(s) below the current file ({normalizedCurrent} -> {normalizedCandidate}).");
                }
            }
        }

        if (!hardReject &&
            qualityModel?.UpgradeStop.StopWhenCutoffMet == true &&
            currentRank >= targetRank &&
            qualityDelta <= 0)
        {
            var currentScore = input.CurrentCustomFormatScore ?? 0;
            var requiresGain = qualityModel.UpgradeStop.RequireCustomFormatGainForSameQuality;
            if (!requiresGain || input.CustomFormatScore <= currentScore)
            {
                hardReject = true;
                risks.Add("Upgrade stop policy blocked this release because the current file already meets cutoff and the candidate does not improve the custom-format score.");
            }
        }

        if (input.CurrentCustomFormatScore is > 0 && input.CustomFormatScore < input.CurrentCustomFormatScore)
        {
            risks.Add($"Custom format score ({input.CustomFormatScore}) is lower than the current file's score ({input.CurrentCustomFormatScore.Value}).");
        }

        var seederScore = ScoreSeeders(input.Seeders, risks, reasons);
        var sizeScore = ScoreSize(input.SizeBytes, normalizedCandidate, qualityModel, risks, reasons, out var estimatedBitrate);
        var releaseGroup = InferReleaseGroup(input.ReleaseName);
        if (!string.IsNullOrWhiteSpace(releaseGroup))
        {
            reasons.Add($"Release group detected: {releaseGroup}.");
        }

        var codecScore = ScoreCodecAndHdr(input.ReleaseName, reasons, risks);
        var score = 1000
            + input.SourcePriorityScore
            + candidateRank * 90
            + Math.Max(-300, qualityDelta * 80)
            + input.CustomFormatScore
            + seederScore
            + sizeScore
            + codecScore;

        if (!meetsCutoff)
        {
            score -= 250;
        }

        if (risks.Count > 0)
        {
            score -= Math.Min(400, risks.Count * 85);
        }

        var status = hardReject
            ? "rejected"
            : risks.Count >= 3
                ? "risky"
                : meetsCutoff
                    ? "preferred"
                    : "eligible";

        if (hardReject)
        {
            score = Math.Min(score, -10000);
        }

        var summary = BuildSummary(status, normalizedCandidate, normalizedTarget, input.CustomFormatScore, input.Seeders, risks.Count, risks);
        return new ReleaseDecision(
            MediaPolicyCatalog.CurrentVersion,
            status,
            score,
            meetsCutoff,
            summary,
            reasons,
            risks,
            qualityDelta,
            input.CustomFormatScore,
            seederScore,
            sizeScore,
            releaseGroup,
            estimatedBitrate);
    }

    public static int QualityRank(string? quality)
        => LibraryQualityDecider.GetRank(quality);

    private static int ScoreSeeders(int? seeders, ICollection<string> risks, ICollection<string> reasons)
    {
        if (seeders is null)
        {
            risks.Add("Indexer did not report seeders, so availability confidence is unknown.");
            return -40;
        }

        if (seeders <= 0)
        {
            risks.Add("No seeders were reported.");
            return -160;
        }

        if (seeders < 3)
        {
            risks.Add("Very low seed count may stall or fail.");
            return -70;
        }

        var score = Math.Min(220, seeders.Value * 6);
        reasons.Add($"{seeders.Value.ToString(CultureInfo.InvariantCulture)} seeders reported.");
        return score;
    }

    private static int ScoreSize(
        long? sizeBytes,
        string quality,
        QualityModelSnapshot? qualityModel,
        ICollection<string> risks,
        ICollection<string> reasons,
        out double? estimatedBitrate)
    {
        estimatedBitrate = null;
        if (sizeBytes is null or <= 0)
        {
            risks.Add("Indexer did not report release size.");
            return -50;
        }

        var sizeGb = sizeBytes.Value / 1_073_741_824d;
        estimatedBitrate = Math.Round(sizeBytes.Value * 8d / (2.0 * 60 * 60) / 1_000_000, 1);
        var (min, max) = ExpectedSizeRangeGb(quality, qualityModel);
        if (sizeGb < min)
        {
            risks.Add($"Size {sizeGb:0.0} GB is unusually small for {quality}.");
            return -180;
        }

        if (sizeGb > max)
        {
            risks.Add($"Size {sizeGb:0.0} GB is unusually large for {quality}.");
            return -80;
        }

        reasons.Add($"Size {sizeGb:0.0} GB is within the expected range for {quality}.");
        return 80;
    }

    private static (double Min, double Max) ExpectedSizeRangeGb(string quality, QualityModelSnapshot? model)
    {
        var tier = model?.Tiers.FirstOrDefault(item => string.Equals(item.Name, quality, StringComparison.OrdinalIgnoreCase));
        if (tier is not null)
        {
            return (tier.MovieMinGb, tier.MovieMaxGb);
        }

        var normalized = quality.ToLowerInvariant();
        if (normalized.Contains("2160") && normalized.Contains("remux")) return (35, 130);
        if (normalized.Contains("2160")) return (7, 60);
        if (normalized.Contains("1080") && normalized.Contains("remux")) return (15, 60);
        if (normalized.Contains("1080")) return (1.5, 25);
        if (normalized.Contains("720")) return (0.5, 8);
        return (0.5, 80);
    }

    private static int ScoreCodecAndHdr(string releaseName, ICollection<string> reasons, ICollection<string> risks)
    {
        var normalized = releaseName.ToLowerInvariant();
        var score = 0;
        if (normalized.Contains("x265") || normalized.Contains("h265") || normalized.Contains("hevc"))
        {
            score += 25;
            reasons.Add("Modern HEVC/x265 video codec detected.");
        }

        if (normalized.Contains("av1"))
        {
            score += 15;
            reasons.Add("AV1 video codec detected.");
        }

        if (normalized.Contains("dv") || normalized.Contains("dolby.vision") || normalized.Contains("hdr10"))
        {
            score += 20;
            reasons.Add("HDR/Dolby Vision signal detected.");
        }

        if (normalized.Contains("hc") && normalized.Contains("sub"))
        {
            risks.Add("Hardcoded subtitles may not match user language preferences.");
            score -= 80;
        }

        return score;
    }

    private static bool LooksLikeSample(string releaseName)
        => SampleTokenRegex().IsMatch(releaseName);

    private static bool ContainsBlockedToken(string releaseName)
        => BlockedTokenRegex().IsMatch(releaseName);

    private static string? MatchNeverGrabPattern(string releaseName, IReadOnlyList<string>? patterns)
    {
        if (patterns is null || patterns.Count == 0)
        {
            return null;
        }

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            if (releaseName.Contains(pattern.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return pattern.Trim();
            }
        }

        return null;
    }

    private static string? InferReleaseGroup(string releaseName)
    {
        var match = ReleaseGroupRegex().Match(releaseName);
        return match.Success ? match.Groups["group"].Value : null;
    }

    private static string BuildSummary(string status, string quality, string target, int customFormatScore, int? seeders, int riskCount, IReadOnlyList<string> risks)
    {
        var downgradeBlock = risks.FirstOrDefault(r => r.StartsWith("Downgrade blocked:", StringComparison.Ordinal));
        var pieces = new List<string>
        {
            downgradeBlock is not null
                ? "Downgrade blocked."
                : status switch
                {
                    "rejected" => "Rejected by hard safety rules.",
                    "risky" => "Usable only with caution.",
                    "preferred" => "Preferred candidate.",
                    _ => "Eligible candidate."
                },
            $"{quality} vs cutoff {target}."
        };

        if (customFormatScore != 0) pieces.Add($"Custom formats {customFormatScore:+#;-#;0}.");
        if (seeders is not null) pieces.Add($"{seeders.Value} seeders.");
        if (riskCount > 0 && downgradeBlock is null) pieces.Add($"{riskCount} risk flag{(riskCount == 1 ? "" : "s")}.");
        return string.Join(" ", pieces);
    }

    [GeneratedRegex(@"(^|[.\s_-])(sample|trailer|extras?|proof)([.\s_-]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SampleTokenRegex();

    [GeneratedRegex(@"(^|[.\s_-])(cam|camrip|ts|telesync|tc|telecine|wp|workprint|scr|screener)([.\s_-]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockedTokenRegex();

    [GeneratedRegex(@"-(?<group>[A-Za-z0-9]{2,20})$")]
    private static partial Regex ReleaseGroupRegex();
}

public sealed record ReleaseDecisionInput(
    string ReleaseName,
    string Quality,
    string? CurrentQuality,
    string? TargetQuality,
    long? SizeBytes,
    int? Seeders,
    string? DownloadUrl,
    int SourcePriorityScore,
    int CustomFormatScore,
    IReadOnlyList<string>? NeverGrabPatterns = null,
    int? CurrentCustomFormatScore = null);
