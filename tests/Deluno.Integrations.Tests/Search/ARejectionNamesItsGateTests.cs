using Deluno.Contracts;
using Deluno.Integrations.Search;
using Deluno.Quality;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// #611, from the first real search against live trackers: a release refused
/// for its size was said to have been refused for its seed count, and a
/// torrent nobody seeds was "preferred".
/// </summary>
public sealed class ARejectionNamesItsGateTests
{
    private static readonly IReadOnlyList<ProfileSizeRule> Floor = [new ProfileSizeRule("Bluray 1080p", 2.2, 0, 0, 0)];

    [Fact]
    public void A_release_under_the_size_floor_says_so_even_with_one_seeder()
    {
        // The Kid 1921, as The Pirate Bay listed it on 24 September 2026.
        var decision = Decide("The.Kid.1921.1080p.BluRay.x264.anoXmous", "Bluray 1080p", sizeGb: 1.08, seeders: 1);

        Assert.Equal(ReleaseDecisionStatuses.Rejected, decision.Status);
        Assert.Contains("below the 2.2 GB minimum", decision.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("seed", decision.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_torrent_nobody_seeds_is_refused_and_says_why()
    {
        var decision = Decide("The Kid 1921 REMASTERED 1080p BluRay x264 SADPANDA[rarbg]", "Bluray 1080p", sizeGb: 5.59, seeders: 0);

        Assert.Equal(ReleaseDecisionStatuses.Rejected, decision.Status);
        Assert.Equal("Not taken: No one is seeding this torrent, so it cannot download.", decision.Summary);
    }

    [Fact]
    public void A_usenet_release_or_one_that_did_not_say_is_not_judged_on_seeders()
    {
        Assert.NotEqual(ReleaseDecisionStatuses.Rejected, Decide("The.Kid.1921.1080p.BluRay.x264-GRP", "Bluray 1080p", sizeGb: 5.6, seeders: 0, protocol: "newznab").Status);
        Assert.NotEqual(ReleaseDecisionStatuses.Rejected, Decide("The.Kid.1921.1080p.BluRay.x264-GRP", "Bluray 1080p", sizeGb: 5.6, seeders: null).Status);
    }

    /// <summary>#613: the Kirio Fan Club episode Nyaa returned for Say Yes to the Dress.</summary>
    [Fact]
    public void A_release_for_another_title_is_refused_for_that_reason_first()
    {
        var mismatch = ReleaseTitleMatch.Mismatch(
            "Kirio Fan Club S01E09 Cant Say It Wont Say It 1080p AMZN WEB-DL DUAL DDP2.0 H.264-VARYG",
            new ReleaseTitleExpectation("Say Yes to the Dress", null, IsTv: true, Season: 1, Episode: 9));

        var decision = ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: "Kirio Fan Club S01E09 Cant Say It Wont Say It 1080p AMZN WEB-DL DUAL DDP2.0 H.264-VARYG",
            Quality: "WEB 1080p",
            CurrentQuality: null,
            TargetQuality: "WEB 1080p",
            SizeBytes: 1_400_000_000,
            Seeders: 2,
            DownloadUrl: "magnet:?xt=urn:btih:20",
            SourcePriorityScore: 100,
            CustomFormatScore: 0,
            IndexerProtocol: "tracker",
            PreferencePlan: ReleasePreferencePlanFactory.CreateQualityPlan("tv", "WEB 1080p", null, upgradeUntilCutoff: true),
            MediaType: "tv",
            TitleMismatch: mismatch));

        Assert.Equal(ReleaseDecisionStatuses.Rejected, decision.Status);
        Assert.Equal($"Not taken: {mismatch}", decision.Summary);
    }

    [Fact]
    public void A_seeded_torrent_in_range_is_still_preferred()
        => Assert.Equal("preferred", Decide("The Kid 1921 CC 1080p BluRay x264 nikt0", "Bluray 1080p", sizeGb: 2.49, seeders: 10).Status);

    private static ReleaseDecision Decide(string name, string quality, double sizeGb, int? seeders, string protocol = "tracker")
        => ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: name,
            Quality: quality,
            CurrentQuality: null,
            TargetQuality: "Bluray 1080p",
            SizeBytes: (long)(sizeGb * 1024 * 1024 * 1024),
            Seeders: seeders,
            DownloadUrl: "https://tracker.example/t/1",
            SourcePriorityScore: 100,
            CustomFormatScore: 0,
            SizeRules: Floor,
            IndexerProtocol: protocol,
            PreferencePlan: ReleasePreferencePlanFactory.CreateQualityPlan("movies", "Bluray 1080p", null, upgradeUntilCutoff: true),
            MediaType: "movies"));
}
