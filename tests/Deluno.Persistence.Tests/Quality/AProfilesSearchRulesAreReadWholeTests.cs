using Deluno.Persistence.Tests.Support;
using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// What a profile says about a search is read from the profile once (coverage register, engines): its allowed tiers, whether to upgrade
/// until the cutoff, its size rules, its acquisition and score rules (an empty one counts as none) and its upgrade stop; no profile id,
/// or an id no profile holds, gives "None" (every tier allowed, keep upgrading, no opinions). Refs #1296.
/// </summary>
public sealed class AProfilesSearchRulesAreReadWholeTests
{
    [Fact]
    public async Task A_profiles_allowed_tiers_upgrade_flag_size_rules_acquisition_stop_and_score_limits_are_all_read()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var quality = app.Services.GetRequiredService<IQualityRepository>();
        var profile = await quality.CreateQualityProfileAsync(
            new CreateQualityProfileRequest(
                "Strict", "movies", "Bluray 1080p", "WEB 1080p, Bluray 1080p", string.Empty, UpgradeUntilCutoff: false, UpgradeUnknownItems: false,
                SizeRules: [new ProfileSizeRule("Bluray 1080p", 4, 20, 0, 0)],
                UpgradeStop: new QualityUpgradeStopPolicy(true, true),
                Acquisition: new ProfileAcquisitionRules("usenet", 30, 60, "GRP", "CAM"),
                ScoreLimits: new ProfileScoreLimits(MinFormatScore: 10, CutoffFormatScore: 100, MinUpgradeFormatScore: 5)),
            CancellationToken.None);

        var rules = await QualityProfileResolver.ResolveSearchRulesAsync(quality, profile.Id, CancellationToken.None);

        Assert.Equal(["WEB 1080p", "Bluray 1080p"], rules.AllowedQualities);
        Assert.False(rules.UpgradeUntilCutoff);
        Assert.Equal("Bluray 1080p", Assert.Single(rules.SizeRules).Quality);
        Assert.Equal("usenet", rules.Acquisition!.PreferredProtocol);
        Assert.Equal(30, rules.Acquisition.UsenetDelayMinutes);
        Assert.True(rules.UpgradeStop!.StopWhenCutoffMet);
        Assert.Equal(10, rules.ScoreLimits!.MinFormatScore);
        Assert.Equal(100, rules.ScoreLimits.CutoffFormatScore);
        Assert.Equal(5, rules.ScoreLimits.MinUpgradeFormatScore);
    }

    [Fact]
    public async Task Empty_acquisition_and_score_rules_count_as_none_and_a_missing_or_unknown_profile_gives_the_None_answer()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var quality = app.Services.GetRequiredService<IQualityRepository>();
        var profile = await quality.CreateQualityProfileAsync(
            new CreateQualityProfileRequest(
                "Plain", "movies", "WEB 1080p", "WEB 1080p", string.Empty, UpgradeUntilCutoff: true, UpgradeUnknownItems: false,
                Acquisition: new ProfileAcquisitionRules(),
                ScoreLimits: new ProfileScoreLimits()),
            CancellationToken.None);

        var plain = await QualityProfileResolver.ResolveSearchRulesAsync(quality, profile.Id, CancellationToken.None);
        var noId = await QualityProfileResolver.ResolveSearchRulesAsync(quality, null, CancellationToken.None);
        var blankId = await QualityProfileResolver.ResolveSearchRulesAsync(quality, "  ", CancellationToken.None);
        var unknown = await QualityProfileResolver.ResolveSearchRulesAsync(quality, "no-such-profile", CancellationToken.None);
        var otherCase = await QualityProfileResolver.ResolveSearchRulesAsync(quality, profile.Id.ToUpperInvariant(), CancellationToken.None);

        Assert.Null(plain.Acquisition);
        Assert.Null(plain.ScoreLimits);
        foreach (var none in new[] { noId, blankId, unknown, otherCase })
        {
            Assert.Same(ProfileSearchRules.None, none);
        }

        Assert.Empty(ProfileSearchRules.None.AllowedQualities);
        Assert.True(ProfileSearchRules.None.UpgradeUntilCutoff);
        Assert.Empty(ProfileSearchRules.None.SizeRules);
    }
}
