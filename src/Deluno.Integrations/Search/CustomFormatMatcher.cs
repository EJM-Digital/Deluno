using Deluno.Quality.Contracts;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Integrations.Search;

/// <summary>
/// Evaluates custom formats through Quality's shared immutable matcher. The
/// legacy numeric result remains available to compatibility diagnostics, while
/// normal acquisition uses the same Boolean match evidence as typed plans.
/// </summary>
public static class CustomFormatMatcher
{
    /// <summary>
    /// Evaluates all supplied custom formats against <paramref name="releaseName"/>
    /// and returns the legacy total plus the matched-format provenance.
    /// </summary>
    public static int Evaluate(
        string releaseName,
        IReadOnlyList<CustomFormatItem>? formats,
        out IReadOnlyList<CustomFormatMatchResult> matched)
        => EvaluateCore(releaseName, formats, includeInformationalScores: true, out matched);

    /// <summary>
    /// Returns only the legacy score from formats allowed to drive an upgrade.
    /// Informational matches are still returned for compatibility explanations.
    /// Typed release plans do not use this numeric value.
    /// </summary>
    public static int EvaluateUpgradeScore(
        string releaseName,
        IReadOnlyList<CustomFormatItem>? formats,
        out IReadOnlyList<CustomFormatMatchResult> matched)
        => EvaluateCore(releaseName, formats, includeInformationalScores: false, out matched);

    /// <summary>
    /// Returns matcher provenance without producing a numeric decision value.
    /// </summary>
    public static IReadOnlyList<CustomFormatMatchResult> EvaluateMatches(
        string releaseName,
        IReadOnlyList<CustomFormatItem>? formats)
    {
        _ = EvaluateCore(releaseName, formats, includeInformationalScores: false, out var matched);
        return matched;
    }

    private static int EvaluateCore(
        string releaseName,
        IReadOnlyList<CustomFormatItem>? formats,
        bool includeInformationalScores,
        out IReadOnlyList<CustomFormatMatchResult> matched)
    {
        if (formats is null || formats.Count == 0)
        {
            matched = [];
            return 0;
        }

        var results = new List<CustomFormatMatchResult>();
        var totalScore = 0;
        foreach (var format in formats)
        {
            var result = CustomFormatDefinitionMatcher.Evaluate(
                releaseName,
                format.Conditions,
                anyCondition: format.TrashId is not null);
            if (!result.IsMatch)
            {
                continue;
            }

            if (includeInformationalScores || format.UpgradeAllowed)
            {
                totalScore += format.Score;
            }

            results.Add(new CustomFormatMatchResult(
                FormatId: format.Id,
                FormatName: format.Name,
                Score: format.Score,
                MatchedConditions: result.MatchedConditions,
                MissedConditions: [],
                UpgradeAllowed: format.UpgradeAllowed));
        }

        matched = results;
        return totalScore;
    }

    /// <summary>
    /// Evaluates every format and returns per-condition hit/miss detail for the
    /// UI, using exactly the matcher that acquisition and installed-file
    /// re-evaluation use.
    /// </summary>
    public static IReadOnlyList<CustomFormatDryRunResult> DryRun(
        string releaseName,
        IReadOnlyList<CustomFormatItem> formats)
        => formats.Select(format =>
        {
            var match = CustomFormatDefinitionMatcher.Evaluate(
                releaseName,
                format.Conditions,
                anyCondition: format.TrashId is not null);
            return new CustomFormatDryRunResult(
                FormatId: format.Id,
                FormatName: format.Name,
                MediaType: format.MediaType,
                Score: format.Score,
                IsMatch: match.IsMatch,
                MatchedConditions: match.MatchedConditions,
                MissedConditions: match.MissedConditions);
        }).ToArray();
}

/// <summary>Per-format match result for the compatibility scoring path.</summary>
public sealed record CustomFormatMatchResult(
    string FormatId,
    string FormatName,
    int Score,
    IReadOnlyList<string> MatchedConditions,
    IReadOnlyList<string> MissedConditions,
    bool UpgradeAllowed = true);

/// <summary>One format's complete dry-run result.</summary>
public sealed record CustomFormatDryRunResult(
    string FormatId,
    string FormatName,
    string MediaType,
    int Score,
    bool IsMatch,
    IReadOnlyList<string> MatchedConditions,
    IReadOnlyList<string> MissedConditions);
