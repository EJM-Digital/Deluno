using Deluno.Integrations.Search;
using Deluno.Quality;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// #600: a single TV episode is sized against the profile's episode band
/// (MB), not the film band (GB).
///
/// <para><b>Why this file exists.</b> Every release was judged against the
/// film band. On the soak rig the TV profile says a WEB 1080p episode runs
/// 420-3800 MB and a WEB 1080p film at least 1.5 GB, so an ordinary 1.2 GB
/// episode would have been turned down as below a film's minimum.</para>
/// </summary>
public sealed class AnEpisodeIsSizedAsAnEpisodeTests
{
    private static readonly IReadOnlyList<ProfileSizeRule> Rules = [new ProfileSizeRule("WEB 1080p", 1.5, 25, 420, 3800)];

    [Fact]
    public void An_ordinary_episode_below_a_films_floor_is_within_the_episode_band()
    {
        var decision = Decide("Breaking.Bad.S02E01.1080p.WEB-DL.DDP5.1.H.264-GROUP", 1.2, "tv");

        Assert.NotEqual(ReleaseDecisionStatuses.Rejected, decision.Status);
        Assert.Contains(decision.Reasons, reason => reason == "Size 1229 MB is within the expected range for WEB 1080p.");
    }

    [Fact]
    public void An_episode_below_the_episode_floor_is_turned_down_in_megabytes()
    {
        var decision = Decide("Breaking.Bad.S02E01.1080p.WEB-DL.DDP5.1.H.264-SHORTY", 0.3, "tv");

        Assert.Equal(ReleaseDecisionStatuses.Rejected, decision.Status);
        Assert.Contains(decision.RiskFlags, flag => flag == "Size 307 MB is below the 420 MB minimum configured for WEB 1080p.");
    }

    [Fact]
    public void Films_and_season_packs_keep_the_film_band()
    {
        Assert.Equal(ReleaseDecisionStatuses.Rejected, Decide("Cleaner.2007.1080p.WEB-DL.DDP5.1.H.264-GROUP", 1.2, "movies").Status);
        Assert.Equal(ReleaseDecisionStatuses.Rejected, Decide("Breaking.Bad.S02.1080p.WEB-DL.DDP5.1.H.264-GROUP", 1.2, "tv").Status);
        Assert.Equal(ReleaseDecisionStatuses.Rejected, Decide("Cleaner.2007.1080p.WEB-DL.DDP5.1.H.264-GROUP", 1.2, mediaType: null).Status);
    }

    private static ReleaseDecision Decide(string releaseName, double sizeGb, string? mediaType)
        => ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: releaseName,
            Quality: "WEB 1080p",
            CurrentQuality: null,
            TargetQuality: "WEB 1080p",
            SizeBytes: (long)(sizeGb * 1024 * 1024 * 1024),
            Seeders: 20,
            DownloadUrl: "https://example.com/release",
            SourcePriorityScore: 100,
            CustomFormatScore: 0,
            SizeRules: Rules,
            MediaType: mediaType));
}
