using Deluno.Quality;

namespace Deluno.Platform.Tests.Contracts;

/// <summary>
/// A profile's own size answers.
///
/// <para>#394: size used to live on the tier, so a Low Storage profile and a
/// Premium 4K profile that both allowed WEB 1080p got the same range for it and
/// changing one changed the other silently. The product owner's case for why
/// that cannot work: <i>"they may want a certain quality and size for anime and
/// something different for another genre and also tv as well."</i></para>
/// </summary>
public sealed class ProfileSizeRulesTests
{
    [Fact]
    public void Two_profiles_can_want_different_sizes_for_the_same_tier()
    {
        var anime = ProfileSizeRulesCodec.Normalize([new ProfileSizeRule("WEB 1080p", 2, 5, 300, 900)]);
        var films = ProfileSizeRulesCodec.Normalize([new ProfileSizeRule("WEB 1080p", 4, 10, 800, 2400)]);

        Assert.Equal(2, ProfileSizeRulesCodec.For(anime, "WEB 1080p")!.MinGb);
        Assert.Equal(4, ProfileSizeRulesCodec.For(films, "WEB 1080p")!.MinGb);
    }

    [Fact]
    public void A_new_profile_starts_where_files_of_that_tier_actually_land()
    {
        // Not inheritance: these are written into the profile and are its own
        // from that moment. A slider has to have a position, and this is the
        // right position to start at.
        var rules = ProfileSizeRulesCodec.StartingRulesFor(["WEB 1080p", "Remux 2160p"]);

        var web = ProfileSizeRulesCodec.For(rules, "WEB 1080p")!;
        var remux = ProfileSizeRulesCodec.For(rules, "Remux 2160p")!;

        Assert.Equal(QualityTypicalSizes.FilmSizeGb("WEB 1080p"), (web.MinGb, web.MaxGb));
        Assert.Equal(QualityTypicalSizes.FilmSizeGb("Remux 2160p"), (remux.MinGb, remux.MaxGb));
        Assert.True(remux.MinGb > web.MaxGb, "A Remux 2160p floor should sit above a WEB 1080p ceiling.");
    }

    [Fact]
    public void Handles_dragged_past_each_other_are_put_back_the_right_way_round()
    {
        // A slider lets you drag the maximum below the minimum. Storing that
        // would refuse every release for the tier without ever saying so.
        var rules = ProfileSizeRulesCodec.Normalize([new ProfileSizeRule("WEB 1080p", 20, 5, 900, 300)]);
        var rule = ProfileSizeRulesCodec.For(rules, "WEB 1080p")!;

        Assert.Equal(5, rule.MinGb);
        Assert.Equal(20, rule.MaxGb);
        Assert.Equal(300, rule.MinMb);
        Assert.Equal(900, rule.MaxMb);
    }

    [Fact]
    public void Saying_nothing_about_a_tier_is_not_saying_nothing_is_allowed()
    {
        var rules = ProfileSizeRulesCodec.Normalize([new ProfileSizeRule("WEB 1080p", 2, 5, 300, 900)]);

        // The profile has an opinion about WEB 1080p and none about Remux
        // 2160p. "None" means any size passes, not that everything is refused.
        Assert.NotNull(ProfileSizeRulesCodec.For(rules, "WEB 1080p"));
        Assert.Null(ProfileSizeRulesCodec.For(rules, "Remux 2160p"));
    }

    [Fact]
    public void A_stored_rule_survives_a_round_trip_and_a_broken_one_is_not_fatal()
    {
        var rules = ProfileSizeRulesCodec.StartingRulesFor(["WEB 1080p", "Bluray 1080p"]);

        Assert.Equal(rules, ProfileSizeRulesCodec.Deserialize(ProfileSizeRulesCodec.Serialize(rules)));

        // Rules nobody can read are no rules. Refusing every release because
        // one stored row is malformed is the worse of the two failures.
        Assert.Empty(ProfileSizeRulesCodec.Deserialize("{not json"));
        Assert.Empty(ProfileSizeRulesCodec.Deserialize(null));
        Assert.Equal(string.Empty, ProfileSizeRulesCodec.Serialize([]));
    }

    [Fact]
    public void The_typical_band_is_one_list_rather_than_a_ladder_of_guesses()
    {
        // The decision engine used to carry its own substring ladder - "2160
        // and remux" meant 35 to 130 - a second copy of a physical fact. These
        // are the values that copy asserted, now read from the one place.
        Assert.Equal((35.0, 130.0), QualityTypicalSizes.FilmSizeGb("Remux 2160p"));
        Assert.Equal((1.5, 25.0), QualityTypicalSizes.FilmSizeGb("WEB 1080p"));

        // A tier nobody knows gets the widest sensible band, not a narrow
        // guess: refusing a release because Deluno could not place its tier
        // would punish the owner for a gap in the catalogue.
        Assert.Equal((0.1, 130.0), QualityTypicalSizes.FilmSizeGb("Some Future Tier"));
    }
}
