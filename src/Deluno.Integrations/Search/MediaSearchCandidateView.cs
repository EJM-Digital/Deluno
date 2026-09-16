namespace Deluno.Integrations.Search;

/// <summary>
/// The one shape a candidate takes when a screen is shown it.
///
/// <para>Movies and TV had a copy each, written out by hand, and they had
/// already drifted: the movie route returned <c>MatchedCustomFormats</c> and
/// the series route did not. So the same question — "what did Deluno find, and
/// what did it match?" — was answered two different ways depending on whether
/// the thing had episodes in it, and on TV the custom-format evidence was
/// simply missing from the answer.</para>
///
/// <para>The legacy score fields are null whenever a typed preference
/// evaluation is present. They describe the old score-based decision, and
/// showing both invites a reader to add up numbers from two different
/// systems.</para>
/// </summary>
public static class MediaSearchCandidateView
{
    public static object For(MediaSearchCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var typed = candidate.PreferenceEvaluation is not null;

        return new
        {
            candidate.ReleaseName,
            candidate.IndexerName,
            candidate.Quality,
            Score = typed ? null : (int?)candidate.Score,
            candidate.MeetsCutoff,
            candidate.Summary,
            candidate.DownloadUrl,
            candidate.SizeBytes,
            candidate.Seeders,
            candidate.DecisionStatus,
            candidate.DecisionReasons,
            candidate.RiskFlags,
            candidate.QualityDelta,
            CustomFormatScore = typed ? null : (int?)candidate.CustomFormatScore,
            SeederScore = typed ? null : (int?)candidate.SeederScore,
            SizeScore = typed ? null : (int?)candidate.SizeScore,
            candidate.ReleaseGroup,
            candidate.EstimatedBitrateMbps,
            candidate.PolicyVersion,
            MatchedCustomFormats = typed ? null : candidate.MatchedCustomFormats,
            candidate.PreferenceEvaluation,
            candidate.PreferenceComparison
        };
    }

    public static object[] ForAll(IEnumerable<MediaSearchCandidate>? candidates)
        => (candidates ?? []).Select(For).ToArray();
}
