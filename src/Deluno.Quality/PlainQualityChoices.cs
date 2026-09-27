namespace Deluno.Quality;

/// <summary>
/// "Stop upgrading once a copy is: Good / Great / The best available."
/// </summary>
public enum QualityCutoffChoice
{
    Good,
    Great,
    BestAvailable
}

/// <summary>
/// "Only replace a file when the new copy is: A little better / Clearly
/// better / Much better."
/// </summary>
public enum QualityUpgradeGainChoice
{
    ALittleBetter,
    ClearlyBetter,
    MuchBetter
}

/// <summary>
/// Maps the three plain choices behind <see cref="ProfileScoreLimits"/> to and
/// from TRaSH custom-format points, so the number stays under the hood for the
/// 99% and only "Show the numbers" ever prints one (#938).
///
/// <para>The mapping is relative to a TRaSH profile's own <c>minFormatScore</c>
/// (the floor beneath which TRaSH itself calls a release bad) and
/// <c>cutoffFormatScore</c> (TRaSH's own "keep chasing the top" ceiling, e.g.
/// 10000 or 35000 in the pinned inventory - a number no real release reaches,
/// used as a sentinel for "never stop"). Good and Great sit at a quarter and
/// three-fifths of the span between those two, so a profile that picks a
/// gentler cutoff than TRaSH's own default still lands somewhere a real
/// release can meet; Best available takes TRaSH's own ceiling unchanged,
/// because "the best available" and "never really stop" are the same
/// instruction.</para>
///
/// <para>For minUpgradeFormatScore, every pinned TRaSH profile already says 1
/// (any positive gain is an upgrade), so "a little better" keeps that number
/// unchanged and "clearly"/"much" scale it up rather than reinventing it.</para>
/// </summary>
public static class PlainQualityChoiceMapper
{
    private const double GoodFraction = 0.25;
    private const double GreatFraction = 0.6;
    private const int ClearlyBetterMultiplier = 3;
    private const int MuchBetterMultiplier = 6;

    public static int MapCutoffScore(QualityCutoffChoice choice, int guideMinFormatScore, int guideCutoffFormatScore)
    {
        var floor = Math.Max(0, guideMinFormatScore);
        var ceiling = Math.Max(floor, guideCutoffFormatScore);
        var span = ceiling - floor;
        return choice switch
        {
            QualityCutoffChoice.Good => floor + (int)Math.Round(span * GoodFraction),
            QualityCutoffChoice.Great => floor + (int)Math.Round(span * GreatFraction),
            _ => ceiling
        };
    }

    public static int MapUpgradeGainScore(QualityUpgradeGainChoice choice, int guideMinUpgradeFormatScore)
    {
        var baseline = Math.Max(1, guideMinUpgradeFormatScore);
        return choice switch
        {
            QualityUpgradeGainChoice.ALittleBetter => baseline,
            QualityUpgradeGainChoice.ClearlyBetter => baseline * ClearlyBetterMultiplier,
            _ => baseline * MuchBetterMultiplier
        };
    }

    /// <summary>"Never grab copies TRaSH rates as bad" (on/off) as a minimum score.</summary>
    public static int? MapMinScore(bool neverGrabBad, int guideMinFormatScore)
        => neverGrabBad && guideMinFormatScore > 0 ? guideMinFormatScore : null;

    /// <summary>
    /// The plain level closest to a stored number, for round-tripping a
    /// hand-edited or previously-synced value back onto the plain control. Used
    /// so "Show the numbers" and the plain select always agree, and so a
    /// decision's reason can say "already 'Great'" rather than a number.
    /// </summary>
    public static QualityCutoffChoice NearestCutoffChoice(int cutoffFormatScore, int guideMinFormatScore, int guideCutoffFormatScore)
    {
        var candidates = new[]
        {
            (Choice: QualityCutoffChoice.Good, Score: MapCutoffScore(QualityCutoffChoice.Good, guideMinFormatScore, guideCutoffFormatScore)),
            (Choice: QualityCutoffChoice.Great, Score: MapCutoffScore(QualityCutoffChoice.Great, guideMinFormatScore, guideCutoffFormatScore)),
            (Choice: QualityCutoffChoice.BestAvailable, Score: MapCutoffScore(QualityCutoffChoice.BestAvailable, guideMinFormatScore, guideCutoffFormatScore))
        };
        return candidates
            .OrderBy(candidate => Math.Abs(candidate.Score - cutoffFormatScore))
            .ThenBy(candidate => candidate.Choice)
            .First()
            .Choice;
    }

    public static QualityUpgradeGainChoice NearestUpgradeGainChoice(int minUpgradeFormatScore, int guideMinUpgradeFormatScore)
    {
        var candidates = new[]
        {
            (Choice: QualityUpgradeGainChoice.ALittleBetter, Score: MapUpgradeGainScore(QualityUpgradeGainChoice.ALittleBetter, guideMinUpgradeFormatScore)),
            (Choice: QualityUpgradeGainChoice.ClearlyBetter, Score: MapUpgradeGainScore(QualityUpgradeGainChoice.ClearlyBetter, guideMinUpgradeFormatScore)),
            (Choice: QualityUpgradeGainChoice.MuchBetter, Score: MapUpgradeGainScore(QualityUpgradeGainChoice.MuchBetter, guideMinUpgradeFormatScore))
        };
        return candidates
            .OrderBy(candidate => Math.Abs(candidate.Score - minUpgradeFormatScore))
            .ThenBy(candidate => candidate.Choice)
            .First()
            .Choice;
    }

    public static string CutoffLabel(QualityCutoffChoice choice) => choice switch
    {
        QualityCutoffChoice.Good => "Good",
        QualityCutoffChoice.Great => "Great",
        _ => "the best available"
    };

    public static string UpgradeGainLabel(QualityUpgradeGainChoice choice) => choice switch
    {
        QualityUpgradeGainChoice.ALittleBetter => "a little better",
        QualityUpgradeGainChoice.ClearlyBetter => "clearly better",
        _ => "much better"
    };

    /// <summary>The generic floor/ceiling/baseline used when a profile has no
    /// guide to compare against (e.g. it was never started from a TRaSH plan).
    /// These mirror the values every pinned TRaSH profile agrees on: a wide-open
    /// cutoff (10000) nothing plausibly reaches, and a minimum upgrade gain of
    /// 1 (any positive gain counts).</summary>
    public const int GenericMinFormatScore = 0;
    public const int GenericCutoffFormatScore = 10000;
    public const int GenericMinUpgradeFormatScore = 1;
}
