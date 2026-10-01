using Deluno.Contracts;
using Deluno.Integrations.Search;
using Deluno.Quality;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// #1098: the Live screen's "Turned down today" groups every raw decision reason into six plain
/// groups, in one shared helper. Every sentence the decision engine, the title match and the
/// preference evaluator can write for a rejection is listed here with the group it must land in,
/// and the real engine is asked for each kind so the helper cannot drift from what it writes.
/// </summary>
public sealed class TurnedDownReasonsTests
{
    [Fact]
    public void The_six_groups_are_in_the_screens_order_with_the_exact_plain_labels()
    {
        Assert.Equal(["language", "notBetter", "tooBig", "fewSeeders", "fake", "other"], TurnedDownGroups.All);
        Assert.Equal("Not in a language you want", TurnedDownGroups.Label(TurnedDownGroups.Language));
        Assert.Equal("No better than what you have", TurnedDownGroups.Label(TurnedDownGroups.NotBetter));
        Assert.Equal("Bigger than your size limit", TurnedDownGroups.Label(TurnedDownGroups.TooBig));
        Assert.Equal("Too few people sharing it", TurnedDownGroups.Label(TurnedDownGroups.FewSeeders));
        Assert.Equal("Fake or mislabelled", TurnedDownGroups.Label(TurnedDownGroups.Fake));
        Assert.Equal("Other reasons", TurnedDownGroups.Label(TurnedDownGroups.Other));
        Assert.Equal("Other reasons", TurnedDownGroups.Label("something nobody named"));
    }

    // Every known raw reason: the sentences the engine writes, copied as they are written.
    [Theory]
    // fake or mislabelled
    [InlineData("Release name looks like a sample, trailer, proof, or extras file.", TurnedDownGroups.Fake)]
    [InlineData("Release name contains a blocked token such as CAM, Telesync, workprint, or screener.", TurnedDownGroups.Fake)]
    [InlineData("Release name matched the never-grab pattern 'x265'.", TurnedDownGroups.Fake)]
    [InlineData("This release is for something else: its name does not give the title \"Say Yes to the Dress\".", TurnedDownGroups.Fake)]
    [InlineData("This release is from 2012; The Kid is from 1921.", TurnedDownGroups.Fake)]
    // language
    [InlineData("Required trait 'audio.language.en' is not known to be present.", TurnedDownGroups.Language)]
    [InlineData("Required trait 'audio.language.en' is absent.", TurnedDownGroups.Language)]
    [InlineData("Forbidden trait 'audio.language.dubbed' is present.", TurnedDownGroups.Language)]
    [InlineData("At least one trait in required-any group 'audio.language.en, audio.language.original' must be proven, but evidence is incomplete.", TurnedDownGroups.Language)]
    [InlineData("No trait in required-any group 'audio.language.en' is present.", TurnedDownGroups.Language)]
    // too few people sharing it
    [InlineData("No one is seeding this torrent, so it cannot download.", TurnedDownGroups.FewSeeders)]
    [InlineData("No seeders were reported.", TurnedDownGroups.FewSeeders)]
    [InlineData("Release has 2 seeder(s), below this indexer's configured minimum of 5.", TurnedDownGroups.FewSeeders)]
    [InlineData("Very low seed count may stall or fail.", TurnedDownGroups.FewSeeders)]
    // bigger than the size limit
    [InlineData("Release size exceeds this indexer's 4000 MB maximum.", TurnedDownGroups.TooBig)]
    [InlineData("Size 9.2 GB is above the 4.0 GB maximum configured for Bluray 1080p for a 100 min runtime.", TurnedDownGroups.TooBig)]
    // no better than what you have
    [InlineData("Downgrade blocked: current file (Bluray 1080p) already meets the quality target (WEB 1080p). Grab this manually if you want to downgrade.", TurnedDownGroups.NotBetter)]
    [InlineData("Equivalent replacement blocked: the installed file already meets cutoff and this candidate does not improve its upgrade-driving custom formats.", TurnedDownGroups.NotBetter)]
    [InlineData("Upgrade stop policy blocked this release because the current file already meets cutoff and the candidate does not improve the custom-format score.", TurnedDownGroups.NotBetter)]
    [InlineData("Upgrade stop policy could not compare custom formats because the installed file evaluation is unknown.", TurnedDownGroups.NotBetter)]
    [InlineData("Stopped upgrading: your copy is already 'Good enough'.", TurnedDownGroups.NotBetter)]
    [InlineData("Not grabbed: not clearly better than the copy you have, which is what you asked for.", TurnedDownGroups.NotBetter)]
    [InlineData("Your installed file is better than this release.", TurnedDownGroups.NotBetter)]
    [InlineData("No preference in this plan ranks this release above your file.", TurnedDownGroups.NotBetter)]
    [InlineData("Resolution decided it; this release ranks lower.", TurnedDownGroups.NotBetter)]
    [InlineData("The installed file and candidate have equivalent proven satisfaction under this plan.", TurnedDownGroups.NotBetter)]
    [InlineData("Installed file already meets every persistent target; automatic upgrades stop here.", TurnedDownGroups.NotBetter)]
    [InlineData("Candidate is acceptable but does not persistently improve the installed file.", TurnedDownGroups.NotBetter)]
    // anything else
    [InlineData("Release name is missing required term 'REMUX'.", TurnedDownGroups.Other)]
    [InlineData("Release name contains excluded term 'HDR10'.", TurnedDownGroups.Other)]
    [InlineData("Release name is missing this indexer's required word 'x264'.", TurnedDownGroups.Other)]
    [InlineData("Release name contains this indexer's ignored word 'hardsub'.", TurnedDownGroups.Other)]
    [InlineData("Release is older than this indexer's 1000-day retention window.", TurnedDownGroups.Other)]
    [InlineData("Release age is unavailable, so the 1000-day retention limit cannot be verified.", TurnedDownGroups.Other)]
    [InlineData("Bluray 2160p is not one of the qualities this profile allows (Bluray 1080p, WEB 1080p).", TurnedDownGroups.Other)]
    [InlineData("This indexer is configured for season-pack searches only, and this release is a single episode.", TurnedDownGroups.Other)]
    [InlineData("This indexer is configured for episode searches only, and this release is a season pack.", TurnedDownGroups.Other)]
    [InlineData("Not grabbed: TRaSH rates this copy as bad, and you asked Deluno to never grab those.", TurnedDownGroups.Other)]
    [InlineData("Size 0.4 GB is below the 2.2 GB minimum configured for Bluray 1080p for a 100 min runtime.", TurnedDownGroups.Other)]
    [InlineData("This release is season 3; the search was for season 2.", TurnedDownGroups.Other)]
    [InlineData("Candidate failed a hard safety gate.", TurnedDownGroups.Other)]
    [InlineData(TurnedDownReasons.BlocklistedReason, TurnedDownGroups.Other)]
    [InlineData("", TurnedDownGroups.Other)]
    [InlineData(null, TurnedDownGroups.Other)]
    public void Every_known_raw_reason_lands_in_its_group(string? reason, string expected)
        => Assert.Equal(expected, TurnedDownReasons.GroupFor(reason));

    [Theory]
    [InlineData("current-better", TurnedDownGroups.NotBetter)]
    [InlineData("equivalent", TurnedDownGroups.NotBetter)]
    public void A_release_that_only_loses_to_your_copy_is_not_better_whatever_its_sentence(string status, string expected)
        => Assert.Equal(expected, TurnedDownReasons.GroupFor(null, status));

    [Theory]
    [InlineData("rejected", true)]
    [InlineData("current-better", true)]
    [InlineData("equivalent", true)]
    [InlineData("preferred", false)]
    [InlineData("acceptable", false)]
    [InlineData("eligible", false)]
    [InlineData("held", false)]
    [InlineData("risky", false)]
    [InlineData("delayed", false)]
    public void Only_a_refused_or_no_better_release_counts_as_turned_down(string status, bool turnedDown)
        => Assert.Equal(turnedDown, TurnedDownReasons.IsTurnedDown(status));

    [Fact]
    public void The_first_rejecting_reason_is_the_gate_the_engine_named_else_the_first_known_cause()
    {
        Assert.Equal("gate", TurnedDownReasons.FirstRejectingReason("gate", ["No seeders were reported."], null));
        Assert.Equal(
            "No seeders were reported.",
            TurnedDownReasons.FirstRejectingReason(null, ["Quality X is below cutoff Y."], ["Indexer did not report release size.", "No seeders were reported."]));
        Assert.Null(TurnedDownReasons.FirstRejectingReason(null, ["Quality X is below cutoff Y."], ["Indexer did not report release size."]));
    }

    // The real engine: what each kind of refusal actually writes lands in the right group.
    private static readonly IReadOnlyList<ProfileSizeRule> Floor = [new ProfileSizeRule("Bluray 1080p", 2.2, 0, 0, 0)];

    private static ReleaseDecision Decide(
        string name,
        int? seeders = 10,
        double sizeGb = 4,
        string? currentQuality = null,
        string quality = "Bluray 1080p",
        string? target = "Bluray 1080p",
        int? maximumSizeMb = null,
        int? minimumSeeders = null,
        IReadOnlyList<string>? neverGrab = null,
        string protocol = "tracker")
        => ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: name,
            Quality: quality,
            CurrentQuality: currentQuality,
            TargetQuality: target,
            SizeBytes: (long)(sizeGb * 1024 * 1024 * 1024),
            Seeders: seeders,
            DownloadUrl: "https://tracker.example/t/1",
            SourcePriorityScore: 100,
            CustomFormatScore: 0,
            NeverGrabPatterns: neverGrab,
            SizeRules: Floor,
            IndexerProtocol: protocol,
            MaximumSizeMb: maximumSizeMb,
            MinimumSeeders: minimumSeeders,
            MediaType: "movies"));

    private static string GroupOf(ReleaseDecision decision)
    {
        Assert.Equal(ReleaseDecisionStatuses.Rejected, decision.Status);
        var raw = TurnedDownReasons.FirstRejectingReason(decision.RejectionGate, decision.Reasons, decision.RiskFlags);
        return TurnedDownReasons.GroupFor(raw, decision.Status);
    }

    [Fact]
    public void The_engine_refusing_a_cam_a_sample_or_a_never_grab_pattern_is_fake_or_mislabelled()
    {
        Assert.Equal(TurnedDownGroups.Fake, GroupOf(Decide("The.Kid.1921.CAM.x264-GRP")));
        Assert.Equal(TurnedDownGroups.Fake, GroupOf(Decide("The.Kid.1921.Sample.1080p.BluRay.x264-GRP")));
        Assert.Equal(TurnedDownGroups.Fake, GroupOf(Decide("The.Kid.1921.1080p.BluRay.x265-GRP", neverGrab: ["x265"])));
    }

    [Fact]
    public void The_engine_refusing_a_torrent_nobody_shares_is_too_few_people_sharing_it()
    {
        Assert.Equal(TurnedDownGroups.FewSeeders, GroupOf(Decide("The.Kid.1921.1080p.BluRay.x264-GRP", seeders: 0)));
        Assert.Equal(TurnedDownGroups.FewSeeders, GroupOf(Decide("The.Kid.1921.1080p.BluRay.x264-GRP", seeders: 2, minimumSeeders: 5)));
    }

    [Fact]
    public void The_engine_refusing_a_release_over_the_indexers_maximum_size_is_bigger_than_your_size_limit()
        => Assert.Equal(TurnedDownGroups.TooBig, GroupOf(Decide("The.Kid.1921.1080p.BluRay.x264-GRP", sizeGb: 8, maximumSizeMb: 4000)));

    [Fact]
    public void The_engine_refusing_a_downgrade_of_a_file_that_already_meets_the_target_is_no_better_than_what_you_have()
        => Assert.Equal(
            TurnedDownGroups.NotBetter,
            GroupOf(Decide("The.Kid.1921.720p.WEB.x264-GRP", quality: "WEB 720p", currentQuality: "Bluray 1080p", target: "WEB 1080p")));

    [Fact]
    public void The_engine_refusing_for_a_size_below_the_floor_is_other_reasons()
        => Assert.Equal(TurnedDownGroups.Other, GroupOf(Decide("The.Kid.1921.1080p.BluRay.x264-GRP", sizeGb: 0.5)));

    // The shared decision of one search: the blocklist, the rules and the comparison, once each.
    private static MediaSearchCandidate Candidate(string release, string status, string? gate = null, IReadOnlyList<string>? risks = null, string indexer = "Idx")
        => new(release, "i1", indexer, "Bluray 1080p", 0, true, "x", DecisionStatus: status, RiskFlags: risks, RejectionGate: gate);

    [Fact]
    public void A_search_turns_down_what_the_rules_refused_what_was_no_better_and_what_was_blocklisted_but_never_what_it_took()
    {
        var blocklisted = Candidate("Blocked.Film.2020", "preferred");
        var refused = Candidate("Cam.Film.2020", "rejected", gate: "Release name contains a blocked token such as CAM, Telesync, workprint, or screener.");
        var sameCopy = Candidate("Same.Film.2020", "equivalent");
        var worse = Candidate("Worse.Film.2020", "current-better");
        var taken = Candidate("Good.Film.2020", "preferred");
        var held = Candidate("Held.Film.2020", "held");
        var before = new MediaSearchPlan(taken, [blocklisted, refused, sameCopy, worse, taken, held], "found");
        var after = new MediaSearchPlan(taken, [refused, sameCopy, worse, taken, held], "found");

        var turnedDown = AcquisitionDecisionPipeline.TurnedDownFrom(before, after);

        Assert.Equal(
            ["Blocked.Film.2020", "Cam.Film.2020", "Same.Film.2020", "Worse.Film.2020"],
            turnedDown.Select(item => item.Release).ToArray());
        Assert.Equal(
            [TurnedDownGroups.Other, TurnedDownGroups.Fake, TurnedDownGroups.NotBetter, TurnedDownGroups.NotBetter],
            turnedDown.Select(item => item.Group).ToArray());
    }
}
