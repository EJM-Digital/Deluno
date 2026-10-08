using Deluno.Integrations.Search;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// #1159: a person reads "Not taken: ..." and "It isn't better than the file you have.", never the engine's own
/// vocabulary. The internal status codes stay for logs; the words a person sees are written in one place, the
/// decision engine and the preference evaluator, and this renders them for the common outcomes.
/// </summary>
public sealed class ReasonsAreInPlainWordsTests
{
    private static readonly string[] Internal =
    [
        "typed", "release plan", "preference evaluation", "hybrid", "ML", "persistently", "eligible under",
        "deterministic", "hard gate", "upgrade-driving"
    ];

    [Fact]
    public void Every_common_outcome_reads_in_plain_words()
    {
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan("movies", "WEB 1080p", null, upgradeUntilCutoff: true);
        var needsHdr = plan with { RequiredTraitIds = ["video.dynamic-range.hdr10"] };

        var outcomes = new Dictionary<string, ReleaseDecision>
        {
            ["taken, nothing on disk"] = Decide(plan, "Film.2020.1080p.WEB-DL.x264-GRP", "WEB 1080p", seeders: 20),
            ["below the target"] = Decide(plan, "Film.2020.720p.WEB-DL.x264-GRP", "WEB 720p", seeders: 20),
            ["nobody seeding"] = Decide(plan, "Film.2020.1080p.WEB-DL.x264-GRP", "WEB 1080p", seeders: 0),
            ["wrong title"] = Decide(plan, "Other.2020.1080p.WEB-DL.x264-GRP", "WEB 1080p", seeders: 20, mismatch: "This release is for another show: Other (2019), not Film (2020)."),
            ["must-have rule missing"] = Decide(needsHdr, "Film.2020.1080p.WEB-DL.x264-GRP", "WEB 1080p", seeders: 20),
            ["no better than the file"] = Decide(plan, "Film.2020.1080p.WEB-DL.x264-NEW", "WEB 1080p", seeders: 20, current: "WEB 1080p", currentFile: true),
            ["worse than the file"] = Decide(plan, "Film.2020.720p.WEB-DL.x264-NEW", "WEB 720p", seeders: 20, current: "WEB 1080p", currentFile: true),
            ["better than the file"] = Decide(plan, "Film.2020.2160p.WEB-DL.x265-NEW", "WEB 2160p", seeders: 20, current: "WEB 1080p", currentFile: true)
        };

        foreach (var (name, decision) in outcomes)
        {
            var shown = new List<string> { decision.Summary };
            shown.AddRange(decision.Reasons);
            shown.AddRange(decision.RiskFlags);
            foreach (var text in shown)
            {
                foreach (var term in Internal)
                {
                    Assert.False(
                        text.Contains(term, term == "ML" ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase),
                        $"'{name}' shows the internal term '{term}': {text}");
                }
            }
        }

        Assert.StartsWith("Not taken: ", outcomes["nobody seeding"].Summary, StringComparison.Ordinal);
        Assert.StartsWith("Not taken: ", outcomes["wrong title"].Summary, StringComparison.Ordinal);
        Assert.StartsWith("Would be taken", outcomes["taken, nothing on disk"].Summary, StringComparison.Ordinal);
        Assert.Contains(outcomes["no better than the file"].Reasons, reason => reason.Contains("has not been checked yet", StringComparison.Ordinal));
    }

    [Fact]
    public void A_rejected_release_leads_with_the_reason_it_was_rejected()
    {
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan("tv", "WEB 1080p", null, upgradeUntilCutoff: true);

        // Refused at a gate: nobody is seeding (the S38E02 playWEB copy).
        var gate = Decide(plan, "The Simpsons S38E02 Diary of a Chimpy Kid 1080p FOX WEB H264-playWEB", "WEB 1080p", seeders: 0, mediaType: "tv");
        Assert.Equal(ReleaseDecisionStatuses.Rejected, gate.Status);
        Assert.DoesNotContain("meets or exceeds cutoff", gate.Reasons[0], StringComparison.Ordinal);
        Assert.Contains("seeding", gate.Reasons[0], StringComparison.OrdinalIgnoreCase);

        // Refused by the plan itself, with no gate: a required trait it lacks.
        var needsHdr = plan with { RequiredTraitIds = ["video.dynamic-range.hdr10"] };
        var byPlan = Decide(needsHdr, "The Simpsons S38E02 1080p FOX WEB H264-playWEB", "WEB 1080p", seeders: 20, mediaType: "tv");
        Assert.Equal(ReleaseDecisionStatuses.Rejected, byPlan.Status);
        Assert.DoesNotContain("meets or exceeds cutoff", byPlan.Reasons[0], StringComparison.Ordinal);
    }

    private static ReleaseDecision Decide(
        ReleasePreferencePlan plan,
        string name,
        string quality,
        int? seeders,
        string? mismatch = null,
        string? current = null,
        bool currentFile = false,
        string mediaType = "movies")
        => ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: name,
            Quality: quality,
            CurrentQuality: current,
            TargetQuality: "WEB 1080p",
            SizeBytes: 4L * 1024 * 1024 * 1024,
            Seeders: seeders,
            DownloadUrl: "magnet:?xt=urn:btih:20",
            SourcePriorityScore: 100,
            CustomFormatScore: 0,
            IndexerProtocol: "tracker",
            PreferencePlan: plan,
            CurrentReleaseName: currentFile ? "/library/Film.mkv" : null,
            CurrentFilePresent: currentFile,
            MediaType: mediaType,
            TitleMismatch: mismatch));
}
