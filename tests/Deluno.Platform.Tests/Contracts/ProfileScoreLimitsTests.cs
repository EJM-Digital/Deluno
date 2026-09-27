using Deluno.Quality;

namespace Deluno.Platform.Tests.Contracts;

/// <summary>
/// The TRaSH-style score guardrails behind "Stop upgrading once a copy is",
/// "Only replace when clearly better" and "Never grab copies rated bad"
/// (#778).
/// </summary>
public sealed class ProfileScoreLimitsTests
{
    [Fact]
    public void Having_no_opinion_is_the_starting_state_and_stores_nothing()
    {
        var none = new ProfileScoreLimits();

        Assert.True(none.IsEmpty);
        Assert.Equal(string.Empty, ProfileScoreLimitsCodec.Serialize(none));
        Assert.Null(ProfileScoreLimitsCodec.Deserialize(string.Empty));
        Assert.Null(ProfileScoreLimitsCodec.Deserialize(null));
    }

    [Fact]
    public void A_zero_or_negative_limit_reads_as_no_opinion()
    {
        var limits = new ProfileScoreLimits(MinFormatScore: 0, CutoffFormatScore: -5, MinUpgradeFormatScore: 0).Normalize();

        Assert.True(limits.IsEmpty);
        Assert.Null(limits.MinFormatScore);
        Assert.Null(limits.CutoffFormatScore);
        Assert.Null(limits.MinUpgradeFormatScore);
    }

    [Fact]
    public void A_real_set_of_limits_round_trips()
    {
        var limits = new ProfileScoreLimits(MinFormatScore: 100, CutoffFormatScore: 6000, MinUpgradeFormatScore: 3);

        var json = ProfileScoreLimitsCodec.Serialize(limits);
        Assert.NotEqual(string.Empty, json);
        Assert.Equal(limits, ProfileScoreLimitsCodec.Deserialize(json));
    }

    [Fact]
    public void An_unreadable_answer_leaves_the_profile_with_no_score_opinion()
    {
        Assert.Null(ProfileScoreLimitsCodec.Deserialize("{not json"));
    }
}
